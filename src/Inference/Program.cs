using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using eShop.Inference.Models;
using eShop.Inference.Services;

var builder = WebApplication.CreateBuilder(args);

var chatModelPath = builder.Configuration["ChatModelPath"] ?? "/models/phi-4-mini";
var embeddingModelPath = builder.Configuration["EmbeddingModelPath"] ?? "/models/all-MiniLM-L6-v2/model.onnx";
var embeddingVocabPath = builder.Configuration["EmbeddingVocabPath"] ?? "/models/all-MiniLM-L6-v2/vocab.txt";

builder.Services.AddSingleton(sp =>
    new ChatService(chatModelPath, sp.GetRequiredService<ILogger<ChatService>>()));

builder.Services.AddSingleton(sp =>
    new EmbeddingService(embeddingModelPath, embeddingVocabPath, sp.GetRequiredService<ILogger<EmbeddingService>>()));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

var _modelsReady = false;

// Health check — reports 503 while warming, 200 when models are loaded
app.MapGet("/health", () => _modelsReady
    ? Results.Ok("healthy")
    : Results.StatusCode(503));

// Liveness — always 200 once the process is running (don't kill during warmup)
app.MapGet("/alive", () => Results.Ok("alive"));

// OpenAI-compatible chat completions endpoint
async Task<IResult> HandleChatCompletion(ChatCompletionRequest request, HttpContext httpContext, ChatService chatService, ILogger<Program> logger, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    logger.LogInformation("Chat request: model={Model}, messages={MsgCount}, tools={ToolCount}, max_tokens={MaxTokens}, stream={Stream}",
        request.Model, request.Messages.Count, request.Tools?.Count ?? 0, request.MaxTokens, request.Stream);
    eShop.Inference.TimingLog.Write($"[Inference] Chat START: {request.Messages.Count} msgs, {request.Tools?.Count ?? 0} tools, max_tokens={request.MaxTokens}, stream={request.Stream}");

    if (request.Stream)
    {
        // OpenAI-compatible Server-Sent Events stream. The WebApp's IChatClient
        // (Microsoft.Extensions.AI over OpenAI SDK) reads chat.completion.chunk
        // objects from `data:` lines and terminates on `data: [DONE]`.
        // Tool-call JSON is streamed as plain text content; the WebApp side
        // detects/buffers it before rendering (see ChatState streaming path).
        var resp = httpContext.Response;
        resp.StatusCode = StatusCodes.Status200OK;
        resp.Headers.ContentType = "text/event-stream";
        resp.Headers.CacheControl = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";

        var id = $"chatcmpl-{Guid.NewGuid():N}"[..30];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var jsonOpts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        try
        {
            await foreach (var delta in chatService.GenerateStreamAsync(request, ct))
            {
                var chunk = new
                {
                    id,
                    @object = "chat.completion.chunk",
                    created,
                    model = request.Model,
                    choices = new[]
                    {
                        new { index = 0, delta = new { content = delta }, finish_reason = (string?)null }
                    }
                };
                var line = $"data: {JsonSerializer.Serialize(chunk, jsonOpts)}\n\n";
                await resp.WriteAsync(line, ct);
                await resp.Body.FlushAsync(ct);
            }

            // Final chunk with finish_reason, then [DONE] sentinel
            var finalChunk = new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model = request.Model,
                choices = new[]
                {
                    new { index = 0, delta = new { }, finish_reason = (string?)"stop" }
                }
            };
            await resp.WriteAsync($"data: {JsonSerializer.Serialize(finalChunk, jsonOpts)}\n\n", ct);
            await resp.WriteAsync("data: [DONE]\n\n", ct);
            await resp.Body.FlushAsync(ct);

            logger.LogInformation("Chat (stream) response: {TotalMs}ms, end-of-stream", sw.ElapsedMilliseconds);
            eShop.Inference.TimingLog.Write($"[Inference] Chat (stream) END in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Chat (stream) client disconnected after {Ms}ms", sw.ElapsedMilliseconds);
            eShop.Inference.TimingLog.Write($"[Inference] Chat (stream) CANCELLED after {sw.ElapsedMilliseconds}ms");
        }

        return Results.Empty;
    }

    var response = await chatService.GenerateAsync(request, ct);

    logger.LogInformation("Chat response: {TotalMs}ms, finish={FinishReason}, tokens={Total} (prompt={Prompt}+completion={Completion})",
        sw.ElapsedMilliseconds, response.Choices[0].FinishReason,
        response.Usage!.TotalTokens, response.Usage.PromptTokens, response.Usage.CompletionTokens);
    eShop.Inference.TimingLog.Write($"[Inference] Chat END in {sw.ElapsedMilliseconds}ms, finish={response.Choices[0].FinishReason}, tokens={response.Usage.TotalTokens} (prompt={response.Usage.PromptTokens}+completion={response.Usage.CompletionTokens})");

    return Results.Ok(response);
}

app.MapPost("/v1/chat/completions", HandleChatCompletion);
app.MapPost("/chat/completions", HandleChatCompletion);

// OpenAI-compatible embeddings endpoint
IResult HandleEmbeddings(EmbeddingRequest request, EmbeddingService embeddingService, ILogger<Program> logger)
{
    var sw = Stopwatch.StartNew();

    var inputs = request.Input switch
    {
        JsonElement el when el.ValueKind == JsonValueKind.String => new List<string> { el.GetString()! },
        JsonElement el when el.ValueKind == JsonValueKind.Array => el.EnumerateArray().Select(e => e.GetString()!).ToList(),
        string s => new List<string> { s },
        _ => new List<string>()
    };

    if (inputs.Count == 0)
    {
        return Results.BadRequest(new { error = new { message = "Input is required", type = "invalid_request_error" } });
    }

    eShop.Inference.TimingLog.Write($"[Inference] Embeddings START: {inputs.Count} inputs");

    var embeddings = embeddingService.GenerateEmbeddings(inputs);

    logger.LogInformation("Embeddings response: {Ms}ms, {Count} inputs", sw.ElapsedMilliseconds, inputs.Count);
    eShop.Inference.TimingLog.Write($"[Inference] Embeddings END in {sw.ElapsedMilliseconds}ms, {inputs.Count} inputs");

    var response = new EmbeddingResponse
    {
        Data = embeddings.Select((e, i) => new EmbeddingData
        {
            Embedding = e,
            Index = i
        }).ToList(),
        Usage = new UsageDto
        {
            PromptTokens = inputs.Sum(t => t.Split(' ').Length),
            TotalTokens = inputs.Sum(t => t.Split(' ').Length)
        }
    };

    return Results.Ok(response);
}

app.MapPost("/v1/embeddings", HandleEmbeddings);
app.MapPost("/embeddings", HandleEmbeddings);

// Warm up models on startup — run a tiny inference so first real request is fast
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Warmup");
        var sw = Stopwatch.StartNew();

        logger.LogInformation("Warming up models...");

        try
        {
            // Use the canonical first-turn payload (system + greeting + tools)
            // so the prefix KV-cache is populated with tokens that the FIRST
            // real user chat will hit (cache_hit=True, cached≈full prefix).
            // Replaces the prior 4-token "hi" warmup which only loaded model
            // weights and left the cache useless for real chats.
            var chatService = app.Services.GetRequiredService<ChatService>();
            await chatService.GenerateAsync(eShop.Inference.CanonicalWarmup.BuildRequest(), CancellationToken.None);
            logger.LogInformation("Chat model warm in {Ms}ms (canonical prefix primed)", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat warmup failed");
        }

        try
        {
            var embeddingService = app.Services.GetRequiredService<EmbeddingService>();
            embeddingService.GenerateEmbeddings(["warmup"]);
            logger.LogInformation("Embedding model warm in {Ms}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Embedding warmup failed");
        }

        logger.LogInformation("All models ready in {Ms}ms", sw.ElapsedMilliseconds);
        _modelsReady = true;
    });
});

app.Run();
