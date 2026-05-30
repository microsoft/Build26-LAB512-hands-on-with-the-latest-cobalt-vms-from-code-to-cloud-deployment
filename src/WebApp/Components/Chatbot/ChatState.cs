using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using eShop.WebAppComponents.Services;
using Microsoft.Extensions.AI;

namespace eShop.WebApp.Chatbot;

public class ChatState
{
    private readonly ICatalogService _catalogService;
    private readonly IBasketState _basketState;
    private readonly ClaimsPrincipal _user;
    private readonly ILogger _logger;
    private readonly IProductImageUrlProvider _productImages;
    private readonly IChatClient _chatClient;
    private readonly ChatOptions _chatOptions;
    private readonly bool _streamingEnabled;

    // Product cards rendered after assistant messages that triggered a search
    private List<ProductCardInfo>? _pendingProductCards;
    private readonly Dictionary<int, List<ProductCardInfo>> _productCardsByMessageIndex = new();

    public ChatState(
        ICatalogService catalogService,
        IBasketState basketState,
        ClaimsPrincipal user,
        IProductImageUrlProvider productImages,
        ILoggerFactory loggerFactory,
        IChatClient chatClient,
        bool streamingEnabled)
    {
        _catalogService = catalogService;
        _basketState = basketState;
        _user = user;
        _productImages = productImages;
        _logger = loggerFactory.CreateLogger(typeof(ChatState));
        _streamingEnabled = streamingEnabled;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("ChatModel: {model}, streaming={Streaming}", chatClient.GetService<ChatClientMetadata>()?.DefaultModelId, streamingEnabled);
        }

        _chatClient = chatClient;
        _chatOptions = new()
        {
            ToolMode = ChatToolMode.Auto,
            Tools =
            [
                AIFunctionFactory.Create(SearchCatalog),
                AIFunctionFactory.Create(AddToCart),
            ],
        };

        Messages =
        [
            new ChatMessage(ChatRole.System, ChatPrompts.SystemPrompt),
            new ChatMessage(ChatRole.Assistant, ChatPrompts.InitialAssistantGreeting),
        ];
    }

    public IList<ChatMessage> Messages { get; }

    public Task AddUserMessageAsync(string userText, Action onMessageAdded, CancellationToken ct = default)
        => _streamingEnabled
            ? AddUserMessageStreamingAsync(userText, onMessageAdded, ct)
            : AddUserMessageNonStreamingAsync(userText, onMessageAdded, ct);

    private async Task AddUserMessageNonStreamingAsync(string userText, Action onMessageAdded, CancellationToken ct)
    {
        // Store the user's message
        Messages.Add(new ChatMessage(ChatRole.User, userText));
        onMessageAdded();

        try
        {
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("[Chat] Starting AI request for: \"{UserText}\"", userText);
            TimingLog.Write($"[WebApp] [Chat] START request: \"{userText}\"");

            // Call 1: Let the LLM decide what to do (may return tool calls or a direct answer)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _chatClient.GetResponseAsync(Messages, _chatOptions, ct);
            _logger.LogInformation("[Chat] Call 1 in {ElapsedMs}ms, {MsgCount} messages, finish={Finish}",
                sw.ElapsedMilliseconds, response.Messages.Count, response.FinishReason);
            TimingLog.Write($"[WebApp] [Chat] Call 1 in {sw.ElapsedMilliseconds}ms, finish={response.FinishReason}");

            // Check if the LLM wants to call tools
            var toolCallMessage = response.Messages
                .FirstOrDefault(m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any());

            if (toolCallMessage is not null)
            {
                // Add the assistant's tool-call message to history
                Messages.Add(toolCallMessage);

                // Execute each tool call
                var toolResults = new List<ChatMessage>();
                foreach (var fc in toolCallMessage.Contents.OfType<FunctionCallContent>())
                {
                    sw.Restart();
                    var result = await ExecuteToolAsync(fc);
                    _logger.LogInformation("[Chat] Tool '{Tool}' executed in {ElapsedMs}ms", fc.Name, sw.ElapsedMilliseconds);
                    TimingLog.Write($"[WebApp] [Chat] Tool '{fc.Name}' in {sw.ElapsedMilliseconds}ms");

                    toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(fc.CallId, result)]));
                }

                // Add tool results to history
                foreach (var tr in toolResults) { Messages.Add(tr); }

                // If SearchCatalog returned product cards, skip the second LLM call
                if (_pendingProductCards is { Count: > 0 })
                {
                    var cardMsg = "Here are the results:";
                    Messages.Add(new ChatMessage(ChatRole.Assistant, cardMsg));
                    var lastIndex = Messages.Count - 1;
                    _productCardsByMessageIndex[lastIndex] = _pendingProductCards;
                    _pendingProductCards = null;

                    _logger.LogInformation("[Chat] Skipped Call 2 — product cards rendered directly. Total: {ElapsedMs}ms", totalSw.ElapsedMilliseconds);
                    TimingLog.Write($"[WebApp] [Chat] SKIPPED Call 2 (product cards). Total: {totalSw.ElapsedMilliseconds}ms");
                }
                else
                {
                    // For non-search tools (AddToCart, GetCartContents), make the second LLM call
                    sw.Restart();
                    var followUp = await _chatClient.GetResponseAsync(Messages, _chatOptions, ct);
                    _logger.LogInformation("[Chat] Call 2 in {ElapsedMs}ms", sw.ElapsedMilliseconds);
                    TimingLog.Write($"[WebApp] [Chat] Call 2 in {sw.ElapsedMilliseconds}ms");

                    if (!string.IsNullOrWhiteSpace(followUp.Text))
                    {
                        Messages.AddMessages(followUp);
                        TryExtractProductCardsFromText(followUp.Text);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(response.Text))
            {
                // Direct answer (no tool calls)
                Messages.AddMessages(response);
                TryExtractProductCardsFromText(response.Text);
            }

            _logger.LogInformation("[Chat] Total request completed in {ElapsedMs}ms", totalSw.ElapsedMilliseconds);
            TimingLog.Write($"[WebApp] [Chat] TOTAL: {totalSw.ElapsedMilliseconds}ms");
        }
        catch (Exception e)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(e, "Error getting chat completions.");
            }
            Messages.Add(new ChatMessage(ChatRole.Assistant, $"My apologies, but I encountered an unexpected error."));
        }
        onMessageAdded();
    }

    /// <summary>
    /// Streaming counterpart to <see cref="AddUserMessageNonStreamingAsync"/>.
    /// Decides on the first non-whitespace char whether the response is a
    /// tool-call (JSON, '{') or prose (anything else):
    ///   - Tool-call: buffer the entire stream silently (Thinking… stays up),
    ///     then parse the JSON ourselves and run the same tool-execution +
    ///     product-card pipeline as the non-streaming path. Inference does NOT
    ///     emit tool_calls in streaming mode (see Inference/Program.cs SSE);
    ///     they arrive as plain text content.
    ///   - Prose: append a live assistant message and grow Messages[^1] on every
    ///     chunk, calling onMessageAdded after each so Blazor re-renders the
    ///     typewriter effect.
    /// </summary>
    private async Task AddUserMessageStreamingAsync(string userText, Action onMessageAdded, CancellationToken ct)
    {
        Messages.Add(new ChatMessage(ChatRole.User, userText));
        onMessageAdded();

        try
        {
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("[Chat-stream] Starting AI request for: \"{UserText}\"", userText);
            TimingLog.Write($"[WebApp] [Chat-stream] START request: \"{userText}\"");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var buffer = new System.Text.StringBuilder();
            bool? isToolCall = null;            // null = haven't seen first non-WS char yet
            bool firstTokenLogged = false;
            int liveMessageIndex = -1;          // index of growing assistant message in prose mode

            await foreach (var update in _chatClient.GetStreamingResponseAsync(Messages, _chatOptions, ct))
            {
                ct.ThrowIfCancellationRequested();

                // The Inference SSE emits everything as text content (no tool_calls
                // field — we parse the JSON ourselves below). Concatenate any text
                // chunks in this update.
                string deltaText = "";
                foreach (var content in update.Contents)
                {
                    if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                        deltaText += tc.Text;
                }

                if (deltaText.Length == 0) continue;

                if (!firstTokenLogged)
                {
                    _logger.LogInformation("[Chat-stream] First token after {Ms}ms", sw.ElapsedMilliseconds);
                    TimingLog.Write($"[WebApp] [Chat-stream] First token after {sw.ElapsedMilliseconds}ms");
                    firstTokenLogged = true;
                }

                buffer.Append(deltaText);

                // Decide mode the first time we see any non-whitespace
                if (isToolCall is null)
                {
                    var bufStr = buffer.ToString();
                    var firstNonWs = -1;
                    for (int i = 0; i < bufStr.Length; i++)
                    {
                        if (!char.IsWhiteSpace(bufStr[i])) { firstNonWs = i; break; }
                    }
                    if (firstNonWs < 0) continue; // still all whitespace, wait

                    isToolCall = bufStr[firstNonWs] == '{';
                    if (!isToolCall.Value)
                    {
                        // Prose mode: create the growing assistant message and re-render
                        Messages.Add(new ChatMessage(ChatRole.Assistant, bufStr));
                        liveMessageIndex = Messages.Count - 1;
                        onMessageAdded();
                        continue;
                    }
                    // Tool-call mode: keep buffering silently. Thinking... indicator stays up.
                }
                else if (isToolCall == false)
                {
                    // Prose mode: replace last message with growing buffer text.
                    Messages[liveMessageIndex] = new ChatMessage(ChatRole.Assistant, buffer.ToString());
                    onMessageAdded();
                }
                // else tool-call: just keep accumulating
            }

            ct.ThrowIfCancellationRequested();

            var fullText = buffer.ToString();

            if (isToolCall == true)
            {
                // Stream finished — now process the tool_call JSON the model emitted.
                // Reuses the tool-execution + product-card path from the non-streaming method.
                _logger.LogInformation("[Chat-stream] Stream ended ({Bytes} bytes), parsing tool call", fullText.Length);
                TimingLog.Write($"[WebApp] [Chat-stream] Stream done in {sw.ElapsedMilliseconds}ms, parsing tool call");

                var toolCalls = ExtractToolCalls(fullText);
                if (toolCalls.Count == 0)
                {
                    // First char looked like JSON but nothing parsed; treat as prose.
                    Messages.Add(new ChatMessage(ChatRole.Assistant, fullText));
                    onMessageAdded();
                }
                else
                {
                    // Add an assistant message holding the tool-call FunctionCallContent
                    // so chat history mirrors the non-streaming shape.
                    var assistantContents = new List<AIContent>();
                    foreach (var (name, argsJson, callId) in toolCalls)
                    {
                        assistantContents.Add(new FunctionCallContent(callId, name, ParseArgs(argsJson)));
                    }
                    Messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

                    foreach (var (name, argsJson, callId) in toolCalls)
                    {
                        var fc = new FunctionCallContent(callId, name, ParseArgs(argsJson));
                        sw.Restart();
                        var result = await ExecuteToolAsync(fc);
                        _logger.LogInformation("[Chat-stream] Tool '{Tool}' executed in {Ms}ms", name, sw.ElapsedMilliseconds);
                        TimingLog.Write($"[WebApp] [Chat-stream] Tool '{name}' in {sw.ElapsedMilliseconds}ms");
                        Messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]));
                    }

                    if (_pendingProductCards is { Count: > 0 })
                    {
                        var cardMsg = "Here are the results:";
                        Messages.Add(new ChatMessage(ChatRole.Assistant, cardMsg));
                        _productCardsByMessageIndex[Messages.Count - 1] = _pendingProductCards;
                        _pendingProductCards = null;
                        _logger.LogInformation("[Chat-stream] Skipped Call 2 — product cards rendered. Total: {Ms}ms", totalSw.ElapsedMilliseconds);
                        TimingLog.Write($"[WebApp] [Chat-stream] SKIPPED Call 2. Total: {totalSw.ElapsedMilliseconds}ms");
                    }
                    else
                    {
                        // Non-search tool — second LLM call to summarise.
                        // Stream the follow-up too so the summary feels instant.
                        sw.Restart();
                        await StreamFollowUpAsync(onMessageAdded, ct);
                        _logger.LogInformation("[Chat-stream] Call 2 (stream) in {Ms}ms", sw.ElapsedMilliseconds);
                    }
                }
            }
            else if (isToolCall == false)
            {
                // Prose stream — message is already in Messages[liveMessageIndex] and current.
                // Run TryExtractProductCardsFromText once at the end in case the model
                // emitted a product JSON array as prose (rare; normally caught by tool path).
                TryExtractProductCardsFromText(fullText);
            }
            else
            {
                // Stream ended with no tokens at all. Treat as empty assistant turn.
                Messages.Add(new ChatMessage(ChatRole.Assistant, ""));
            }

            _logger.LogInformation("[Chat-stream] Total in {Ms}ms", totalSw.ElapsedMilliseconds);
            TimingLog.Write($"[WebApp] [Chat-stream] TOTAL: {totalSw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("[Chat-stream] cancelled");
            TimingLog.Write("[WebApp] [Chat-stream] CANCELLED");
            // Leave any partial assistant message in place; user clearly moved on.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error streaming chat completion.");
            Messages.Add(new ChatMessage(ChatRole.Assistant, "My apologies, but I encountered an unexpected error."));
        }
        onMessageAdded();
    }

    private async Task StreamFollowUpAsync(Action onMessageAdded, CancellationToken ct)
    {
        var buffer = new System.Text.StringBuilder();
        int liveIndex = -1;
        await foreach (var update in _chatClient.GetStreamingResponseAsync(Messages, _chatOptions, ct))
        {
            ct.ThrowIfCancellationRequested();
            string delta = "";
            foreach (var content in update.Contents)
            {
                if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                    delta += tc.Text;
            }
            if (delta.Length == 0) continue;
            buffer.Append(delta);
            if (liveIndex < 0)
            {
                Messages.Add(new ChatMessage(ChatRole.Assistant, buffer.ToString()));
                liveIndex = Messages.Count - 1;
            }
            else
            {
                Messages[liveIndex] = new ChatMessage(ChatRole.Assistant, buffer.ToString());
            }
            onMessageAdded();
        }
        TryExtractProductCardsFromText(buffer.ToString());
    }

    /// <summary>
    /// Parses {"tool_call":{"name":"...","arguments":{...}}} patterns out of streamed
    /// assistant text. Mirrors the inference-side ParseToolCalls regex/JSON logic so the
    /// shape sent to ExecuteToolAsync matches what the non-streaming path produces.
    /// </summary>
    private static List<(string Name, string ArgsJson, string CallId)> ExtractToolCalls(string text)
    {
        var list = new List<(string, string, string)>();
        // Greedy {...} match; then validate per-candidate via JsonDocument.
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\{[^{}]*""tool_call""\s*:\s*\{(?:[^{}]|\{[^{}]*\})*\}\s*\}",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Value);
                if (doc.RootElement.TryGetProperty("tool_call", out var tc))
                {
                    var name = tc.GetProperty("name").GetString() ?? "";
                    var args = tc.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}";
                    list.Add((name, args, $"call_{Guid.NewGuid():N}"[..24]));
                }
            }
            catch (JsonException) { /* skip malformed */ }
        }
        return list;
    }

    private static IDictionary<string, object?>? ParseArgs(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var d = new Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                d[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.TryGetInt32(out var i) ? i : (object)p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
            }
            return d;
        }
        catch (JsonException) { return null; }
    }

    private async Task<string> ExecuteToolAsync(FunctionCallContent fc)
    {
        try
        {
            var tool = _chatOptions.Tools?.OfType<AIFunction>().FirstOrDefault(t => t.Name == fc.Name);
            if (tool is null) return $"Unknown tool: {fc.Name}";

            var args = fc.Arguments is not null ? new AIFunctionArguments(fc.Arguments) : null;
            var result = await tool.InvokeAsync(args);
            return result?.ToString() ?? "";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Tool execution failed: {Tool}", fc.Name);
            return $"Error executing {fc.Name}: {e.Message}";
        }
    }


    [Description("Gets information about the chat user")]
    private string GetUserInfo()
    {
        var claims = _user.Claims;
        return JsonSerializer.Serialize(new
        {
            Name = GetValue(claims, "name"),
            LastName = GetValue(claims, "last_name"),
            Street = GetValue(claims, "address_street"),
            City = GetValue(claims, "address_city"),
            State = GetValue(claims, "address_state"),
            ZipCode = GetValue(claims, "address_zip_code"),
            Country = GetValue(claims, "address_country"),
            Email = GetValue(claims, "email"),
            PhoneNumber = GetValue(claims, "phone_number"),
        });

        static string GetValue(IEnumerable<Claim> claims, string claimType) =>
            claims.FirstOrDefault(x => x.Type == claimType)?.Value ?? "";
    }

    [Description("Search the product catalog")]
    private async Task<string> SearchCatalog([Description("Search query")] string productDescription)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[Tool] SearchCatalog called: \"{ProductDescription}\"", productDescription);
        TimingLog.Write($"[WebApp] [Tool] SearchCatalog START: \"{productDescription}\"");
        try
        {
            var results = await _catalogService.GetCatalogItemsWithSemanticRelevance(0, 4, productDescription!);
            // Slim payload: only fields the model needs to present results
            var slim = results.Data.Select(item => new
            {
                item.Id,
                item.Name,
                Price = item.Price.ToString("F2")
            }).ToList();

            // Stash product cards for rich HTML rendering after the AI response
            _pendingProductCards = results.Data.Select(item => new ProductCardInfo(
                item.Id,
                item.Name,
                item.Price.ToString("C"),
                _productImages.GetProductImageUrl(item.Id)
            )).ToList();

            var json = JsonSerializer.Serialize(slim);
            _logger.LogInformation("[Tool] SearchCatalog completed in {ElapsedMs}ms, {Count} results, {Bytes} bytes",
                sw.ElapsedMilliseconds, slim.Count, json.Length);
            TimingLog.Write($"[WebApp] [Tool] SearchCatalog END in {sw.ElapsedMilliseconds}ms, {slim.Count} results, {json.Length} bytes");
            return json;
        }
        catch (HttpRequestException e)
        {
            _logger.LogError(e, "[Tool] SearchCatalog failed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return Error(e, "Error accessing catalog.");
        }
    }

    [Description("Add a product to the cart")]
    private async Task<string> AddToCart([Description("Product id")] int itemId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[Tool] AddToCart called: itemId={ItemId}", itemId);
        try
        {
            var item = await _catalogService.GetCatalogItem(itemId);
            await _basketState.AddAsync(item!);
            _logger.LogInformation("[Tool] AddToCart completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return "Item added to shopping cart.";
        }
        catch (Grpc.Core.RpcException e) when (e.StatusCode == Grpc.Core.StatusCode.Unauthenticated)
        {
            return "Unable to add an item to the cart. You must be logged in.";
        }
        catch (Exception e)
        {
            return Error(e, "Unable to add the item to the cart.");
        }
    }

    [Description("Gets information about the contents of the user's shopping cart (basket)")]
    private async Task<string> GetCartContents()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[Tool] GetCartContents called");
        try
        {
            var basketItems = await _basketState.GetBasketItemsAsync();
            _logger.LogInformation("[Tool] GetCartContents completed in {ElapsedMs}ms, {Count} items",
                sw.ElapsedMilliseconds, basketItems.Count());
            return JsonSerializer.Serialize(basketItems);
        }
        catch (Exception e)
        {
            return Error(e, "Unable to get the cart's contents.");
        }
    }

    private string Error(Exception e, string message)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.LogError(e, message);
        }

        return message;
    }

    /// <summary>
    /// Detects JSON product arrays in assistant text and converts them to product cards.
    /// This handles the case where the LLM responds from memory instead of calling SearchCatalog.
    /// </summary>
    private void TryExtractProductCardsFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Look for a JSON array in the text
        var startIdx = text.IndexOf('[');
        var endIdx = text.LastIndexOf(']');
        if (startIdx < 0 || endIdx <= startIdx) return;

        var jsonCandidate = text[startIdx..(endIdx + 1)];
        try
        {
            var items = JsonSerializer.Deserialize<List<JsonElement>>(jsonCandidate);
            if (items is not { Count: > 0 }) return;

            // Check if items look like products (have Id/Name/Price)
            var first = items[0];
            if (!first.TryGetProperty("Id", out _) && !first.TryGetProperty("id", out _)) return;

            var cards = new List<ProductCardInfo>();
            foreach (var item in items)
            {
                var id = item.TryGetProperty("Id", out var idProp) ? idProp.GetInt32()
                       : item.TryGetProperty("id", out idProp) ? idProp.GetInt32() : 0;
                var name = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString()
                         : item.TryGetProperty("name", out nameProp) ? nameProp.GetString() : "";
                var price = item.TryGetProperty("Price", out var priceProp) ? priceProp.ToString()
                          : item.TryGetProperty("price", out priceProp) ? priceProp.ToString() : "";

                if (id > 0 && !string.IsNullOrEmpty(name))
                {
                    cards.Add(new ProductCardInfo(id, name, $"${price}", _productImages.GetProductImageUrl(id)));
                }
            }

            if (cards.Count > 0)
            {
                // Replace the raw JSON message with a friendly text and attach cards
                var lastMsg = Messages[^1];
                if (lastMsg.Role == ChatRole.Assistant)
                {
                    Messages[^1] = new ChatMessage(ChatRole.Assistant, "Here are the results:");
                }
                _productCardsByMessageIndex[Messages.Count - 1] = cards;
                _logger.LogInformation("[Chat] Extracted {Count} product cards from LLM text response", cards.Count);
            }
        }
        catch (JsonException)
        {
            // Not valid JSON — ignore
        }
    }

    /// <summary>Gets product cards associated with a specific message index, if any.</summary>
    public List<ProductCardInfo>? GetProductCardsForMessage(int messageIndex)
        => _productCardsByMessageIndex.TryGetValue(messageIndex, out var cards) ? cards : null;

    public record ProductCardInfo(int Id, string Name, string Price, string ImageUrl);
}
