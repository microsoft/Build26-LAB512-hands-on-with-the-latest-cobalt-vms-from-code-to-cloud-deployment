using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using eShop.Inference.Models;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace eShop.Inference.Services;

/// <summary>
/// Generates chat completions using Phi-4-mini via ONNX Runtime GenAI.
/// Supports tool/function calling through prompt engineering.
/// </summary>
public sealed partial class ChatService : IDisposable
{
    private readonly Model _model;
    private readonly Tokenizer _tokenizer;
    private readonly ILogger<ChatService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Lever 1 — dynamic longest-common-prefix KV cache.
    // Keeps a single warm Generator alive across requests; on each call, we
    // tokenize the new prompt, find how many leading tokens still match the
    // generator's current sequence, RewindTo() that point, AppendTokens() the
    // diff, and decode. This skips re-prefilling the static system+tools+greeting
    // prefix (~290 tokens, ~2400ms) on every request after the first.
    private readonly bool _prefixCacheEnabled;
    private Generator? _warmGen;
    private GeneratorParams? _warmParams;
    private int[] _cachedTokenIds = [];
    private const int CacheMinL = 50;            // don't bother reusing if LCP < this
    private const int WarmGenMaxLength = 8192;   // baked into warm gen at construction

    public ChatService(string modelPath, ILogger<ChatService> logger)
    {
        _logger = logger;
        _prefixCacheEnabled = Environment.GetEnvironmentVariable("INFERENCE_PREFIX_CACHE") != "0";
        _logger.LogInformation("Prefix cache: {State} (set INFERENCE_PREFIX_CACHE=0 to disable)",
            _prefixCacheEnabled ? "enabled" : "disabled");

        // Apply env-driven ORT session_options overrides (e.g. intra_op_num_threads)
        // via Config.Overlay before constructing the Model. Lets per-environment
        // overlays (e.g. AKS) tune perf without rebuilding or shipping env-specific
        // config files. No env var = code falls back to Environment.ProcessorCount,
        // which respects the container's cgroup cpu limit.
        using (var config = new Config(modelPath))
        {
            var overlay = BuildSessionOptionsOverlay();
            config.Overlay(overlay);
            _model = new Model(config);
        }
        _tokenizer = new Tokenizer(_model);

        _logger.LogInformation("Chat model loaded from {ModelPath}", modelPath);
    }

    private string BuildSessionOptionsOverlay()
    {
        var threadsEnv = Environment.GetEnvironmentVariable("INFERENCE_INTRA_OP_THREADS");
        var threads = int.TryParse(threadsEnv, out var t) && t > 0
            ? t
            : Environment.ProcessorCount;
        var allowSpinning = Environment.GetEnvironmentVariable("INFERENCE_ALLOW_SPINNING") == "1";
        var logSevEnv = Environment.GetEnvironmentVariable("INFERENCE_ORT_LOG_SEVERITY");
        int? logSev = int.TryParse(logSevEnv, out var ls) && ls is >= 0 and <= 4 ? ls : null;

        var sessionOpts = new Dictionary<string, object>
        {
            ["intra_op_num_threads"] = threads,
        };
        if (allowSpinning)
            sessionOpts["session.intra_op.allow_spinning"] = "1";
        if (logSev is not null)
            sessionOpts["log_severity_level"] = logSev.Value;

        var overlay = new
        {
            model = new { decoder = new { session_options = sessionOpts } }
        };
        var json = JsonSerializer.Serialize(overlay);

        _logger.LogInformation("ORT session_options overlay: intra_op_num_threads={Threads} (env={Env}), allow_spinning={Spin}, log_severity_level={LogSev}",
            threads, threadsEnv ?? "unset", allowSpinning, logSev?.ToString() ?? "default");
        return json;
    }

    public async Task<ChatCompletionResponse> GenerateAsync(ChatCompletionRequest request, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        // Serialize to single-threaded access — Phi-4-mini on CPU is one-at-a-time
        var waitSw = Stopwatch.StartNew();
        await _semaphore.WaitAsync(ct);
        var waitMs = waitSw.ElapsedMilliseconds;

        try
        {
            if (waitMs > 50)
                _logger.LogWarning("Chat semaphore wait: {WaitMs}ms (queued behind another request)", waitMs);

            var result = GenerateCore(request);

            _logger.LogInformation(
                "Chat total: {TotalMs}ms (wait={WaitMs}ms, prompt={PromptTokens}tok, completion={CompletionTokens}tok, {TokPerSec:F1} tok/s)",
                totalSw.ElapsedMilliseconds, waitMs,
                result.Usage!.PromptTokens, result.Usage.CompletionTokens,
                result.Usage.CompletionTokens / (totalSw.Elapsed.TotalSeconds - waitMs / 1000.0));

            return result;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Streaming counterpart to <see cref="GenerateAsync"/>. Yields incremental
    /// text deltas as tokens are generated, so the WebApp can render typewriter-style.
    ///
    /// Reuses the prefix KV-cache (Lever 1) exactly the same way as the non-streaming
    /// path — prefill is unchanged, cache hit/miss logged the same, _cachedTokenIds
    /// updated at the end so the next request's LCP includes this turn.
    ///
    /// Tool-call JSON is NOT parsed here — streaming responses contain tool_call JSON
    /// as plain text content, and the WebApp side detects/buffers it on first non-WS
    /// char (since rendering partial tool-call JSON would break the catalog API call
    /// and product-card rendering).
    ///
    /// Cancellation: <paramref name="ct"/> is checked between every generated token.
    /// On cancel, the warm generator is disposed (cache invalidated) since we can't
    /// know if the partial sequence is in a valid state. Subsequent requests rebuild.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateStreamAsync(
        ChatCompletionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var waitSw = Stopwatch.StartNew();
        await _semaphore.WaitAsync(ct);
        var waitMs = waitSw.ElapsedMilliseconds;

        if (waitMs > 50)
            _logger.LogWarning("Chat (stream) semaphore wait: {WaitMs}ms (queued behind another request)", waitMs);

        var sw = Stopwatch.StartNew();

        // === PREFILL — identical to GenerateCore ===
        var prompt = BuildPrompt(request.Messages, request.Tools);
        var promptBuildMs = sw.ElapsedMilliseconds;

        int[] promptTokens;
        using (var sequences = _tokenizer.Encode(prompt))
        {
            promptTokens = sequences[0].ToArray();
        }
        var promptTokenCount = promptTokens.Length;
        var tokenizeMs = sw.ElapsedMilliseconds;

        _logger.LogInformation("Chat (stream) prompt: {Chars} chars, {Tokens} tokens (build={BuildMs}ms, tokenize={TokenizeMs}ms)",
            prompt.Length, promptTokenCount, promptBuildMs, tokenizeMs - promptBuildMs);

        var useCache = _prefixCacheEnabled
                    && request.MaxTokens + promptTokenCount <= WarmGenMaxLength;

        Generator generator;
        GeneratorParams? oneShotParams = null;
        bool ownsGenerator;
        bool cacheHit = false;
        int cachedTokens = 0;
        int newTokens;
        long prefillStartMs = sw.ElapsedMilliseconds;

        if (useCache)
        {
            cachedTokens = _warmGen is not null
                ? LongestCommonPrefix(_cachedTokenIds, promptTokens)
                : 0;

            if (_warmGen is not null && cachedTokens >= CacheMinL)
            {
                try
                {
                    _warmGen.RewindTo((ulong)cachedTokens);
                    if (cachedTokens < promptTokenCount)
                    {
                        _warmGen.AppendTokens(promptTokens.AsSpan(cachedTokens));
                    }
                    cacheHit = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Warm gen RewindTo/AppendTokens failed (stream); rebuilding from scratch");
                    DisposeWarmGen();
                    cachedTokens = 0;
                }
            }

            if (!cacheHit)
            {
                DisposeWarmGen();
                _warmParams = new GeneratorParams(_model);
                _warmParams.SetSearchOption("max_length", (double)WarmGenMaxLength);
                _warmParams.SetSearchOption("temperature", (double)request.Temperature);
                _warmParams.SetSearchOption("do_sample", request.Temperature > 0);
                _warmGen = new Generator(_model, _warmParams);
                _warmGen.AppendTokens(promptTokens.AsSpan());
            }

            generator = _warmGen!;
            ownsGenerator = false;
            newTokens = promptTokenCount - cachedTokens;
        }
        else
        {
            oneShotParams = new GeneratorParams(_model);
            oneShotParams.SetSearchOption("max_length", (double)(request.MaxTokens + promptTokenCount));
            oneShotParams.SetSearchOption("temperature", (double)request.Temperature);
            oneShotParams.SetSearchOption("do_sample", request.Temperature > 0);
            generator = new Generator(_model, oneShotParams);
            generator.AppendTokens(promptTokens.AsSpan());
            ownsGenerator = true;
            newTokens = promptTokenCount;
        }

        var prefillMs = sw.ElapsedMilliseconds - prefillStartMs;

        _logger.LogInformation("Chat (stream) prefill: {PrefillMs}ms ({Tokens} prompt tokens, cache_hit={CacheHit}, cached={Cached}, new={New})",
            prefillMs, promptTokenCount, cacheHit, cachedTokens, newTokens);

        // === DECODE — yields deltas as tokens land ===
        // Iterator finally blocks to guarantee semaphore release + generator cleanup.
        // We can't put yield statements inside try/catch, so split: the decode loop
        // is in a helper method that returns IEnumerable<string>; we wrap it here
        // with try/finally and re-yield.
        var generatedTokenCount = 0;
        var genStartMs = sw.ElapsedMilliseconds;
        long genMs = 0;
        bool generationFailed = false;
        try
        {
            int previouslyDecodedLen = 0;
            while (!generator.IsDone())
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Chat (stream) cancelled after {Tokens} tokens", generatedTokenCount);
                    generationFailed = true; // dispose warm gen — partial state unknown
                    break;
                }

                generator.GenerateNextToken();
                generatedTokenCount++;

                // Decode the full generated span so far and emit only the new chars.
                // BPE tokens may span multi-byte UTF-8 sequences; decoding the whole
                // generated span every step is the safe way to handle that.
                var fullSequence = generator.GetSequence(0).ToArray();
                var generatedTokens = fullSequence.AsSpan(promptTokenCount);
                var decoded = _tokenizer.Decode(generatedTokens);

                if (decoded.Length > previouslyDecodedLen)
                {
                    var delta = decoded.Substring(previouslyDecodedLen);
                    previouslyDecodedLen = decoded.Length;
                    yield return delta;
                }
            }
            genMs = sw.ElapsedMilliseconds - genStartMs;

            // Update prefix cache so the NEXT request's LCP includes this turn.
            // Skip on cancel — partial sequence may not be a valid stop point.
            if (useCache && !generationFailed)
            {
                _cachedTokenIds = generator.GetSequence(0).ToArray();
            }
        }
        finally
        {
            _logger.LogInformation("Chat (stream) decode: {Tokens} tokens in {GenMs}ms ({TokPerSec:F1} tok/s)",
                generatedTokenCount, genMs,
                generatedTokenCount / Math.Max(0.001, genMs / 1000.0));
            _logger.LogInformation("Chat (stream) total: {TotalMs}ms (wait={WaitMs}ms, prompt={PromptTokens}tok, completion={CompletionTokens}tok)",
                totalSw.ElapsedMilliseconds, waitMs, promptTokenCount, generatedTokenCount);

            if (generationFailed && useCache)
            {
                DisposeWarmGen();
            }
            if (ownsGenerator) generator.Dispose();
            oneShotParams?.Dispose();
            _semaphore.Release();
        }
    }

    private ChatCompletionResponse GenerateCore(ChatCompletionRequest request)
    {
        var sw = Stopwatch.StartNew();

        var prompt = BuildPrompt(request.Messages, request.Tools);
        var promptBuildMs = sw.ElapsedMilliseconds;

        // Tokenize once; copy to int[] so we can do LCP against the cached array
        // and cache the result for next request after sequences is disposed.
        int[] promptTokens;
        using (var sequences = _tokenizer.Encode(prompt))
        {
            promptTokens = sequences[0].ToArray();
        }
        var promptTokenCount = promptTokens.Length;
        var tokenizeMs = sw.ElapsedMilliseconds;

        _logger.LogInformation("Chat prompt: {Chars} chars, {Tokens} tokens (build={BuildMs}ms, tokenize={TokenizeMs}ms)",
            prompt.Length, promptTokenCount, promptBuildMs, tokenizeMs - promptBuildMs);

        // Decide cache strategy. Fall through to one-shot ephemeral generator if
        // cache is disabled, or if request would exceed the warm gen's baked
        // max_length (very rare for typical chat traffic).
        var useCache = _prefixCacheEnabled
                    && request.MaxTokens + promptTokenCount <= WarmGenMaxLength;

        Generator generator;
        GeneratorParams? oneShotParams = null;
        bool ownsGenerator;
        bool cacheHit = false;
        int cachedTokens = 0;
        int newTokens;
        long prefillStartMs = sw.ElapsedMilliseconds;

        if (useCache)
        {
            cachedTokens = _warmGen is not null
                ? LongestCommonPrefix(_cachedTokenIds, promptTokens)
                : 0;

            if (_warmGen is not null && cachedTokens >= CacheMinL)
            {
                try
                {
                    _warmGen.RewindTo((ulong)cachedTokens);
                    if (cachedTokens < promptTokenCount)
                    {
                        _warmGen.AppendTokens(promptTokens.AsSpan(cachedTokens));
                    }
                    cacheHit = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Warm gen RewindTo/AppendTokens failed; rebuilding cache from scratch");
                    DisposeWarmGen();
                    cachedTokens = 0;
                }
            }

            if (!cacheHit)
            {
                // Fresh warm gen — bake search options at construction time.
                // Temperature/sampling are baked here and reused across all
                // subsequent cache-hit requests; if a future request needs a
                // different temp, we'd need to rebuild. WebApp uses temp=0.7
                // for all real chats so this is stable.
                DisposeWarmGen();
                _warmParams = new GeneratorParams(_model);
                _warmParams.SetSearchOption("max_length", (double)WarmGenMaxLength);
                _warmParams.SetSearchOption("temperature", (double)request.Temperature);
                _warmParams.SetSearchOption("do_sample", request.Temperature > 0);
                _warmGen = new Generator(_model, _warmParams);
                _warmGen.AppendTokens(promptTokens.AsSpan());
            }

            generator = _warmGen!;
            ownsGenerator = false;
            newTokens = promptTokenCount - cachedTokens;
        }
        else
        {
            // One-shot path — matches legacy behavior, no cache touched.
            oneShotParams = new GeneratorParams(_model);
            oneShotParams.SetSearchOption("max_length", (double)(request.MaxTokens + promptTokenCount));
            oneShotParams.SetSearchOption("temperature", (double)request.Temperature);
            oneShotParams.SetSearchOption("do_sample", request.Temperature > 0);
            generator = new Generator(_model, oneShotParams);
            generator.AppendTokens(promptTokens.AsSpan());
            ownsGenerator = true;
            newTokens = promptTokenCount;
        }

        var prefillMs = sw.ElapsedMilliseconds - prefillStartMs;

        _logger.LogInformation("Chat prefill: {PrefillMs}ms ({Tokens} prompt tokens, cache_hit={CacheHit}, cached={Cached}, new={New})",
            prefillMs, promptTokenCount, cacheHit, cachedTokens, newTokens);

        try
        {
            // Generate tokens one at a time
            var generatedTokenCount = 0;
            var genStartMs = sw.ElapsedMilliseconds;
            while (!generator.IsDone())
            {
                generator.GenerateNextToken();
                generatedTokenCount++;
            }
            var genMs = sw.ElapsedMilliseconds - genStartMs;

            // Get the full sequence (prompt + generated) and decode only the generated part
            var fullSequence = generator.GetSequence(0).ToArray();
            var generatedTokens = fullSequence.AsSpan(promptTokenCount);
            var generatedText = _tokenizer.Decode(generatedTokens);

            // Remove trailing end tokens
            generatedText = generatedText
                .Replace("<|end|>", "")
                .Replace("<|endoftext|>", "")
                .Trim();

            _logger.LogInformation("Chat decode: {Tokens} tokens in {GenMs}ms ({TokPerSec:F1} tok/s, {MsPerTok:F0} ms/tok)",
                generatedTokenCount, genMs,
                generatedTokenCount / (genMs / 1000.0),
                genMs / (double)Math.Max(1, generatedTokenCount));

            // Update prefix cache so the NEXT request's LCP includes this turn's
            // generated tokens (multi-turn conversations get progressively warmer).
            if (useCache)
            {
                _cachedTokenIds = fullSequence;
            }

            return BuildResponse(generatedText, promptTokenCount, generatedTokenCount);
        }
        catch
        {
            // On any generation failure, kill the warm cache so the next
            // request rebuilds from scratch (don't leak a broken generator).
            if (useCache) DisposeWarmGen();
            throw;
        }
        finally
        {
            if (ownsGenerator) generator.Dispose();
            oneShotParams?.Dispose();
        }
    }

    private static int LongestCommonPrefix(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i]) return i;
        }
        return n;
    }

    private void DisposeWarmGen()
    {
        _warmGen?.Dispose();
        _warmGen = null;
        _warmParams?.Dispose();
        _warmParams = null;
        _cachedTokenIds = [];
    }

    private ChatCompletionResponse BuildResponse(string generatedText, int promptTokenCount, int generatedTokenCount)
    {

        // Check if the response contains tool calls
        var toolCalls = ParseToolCalls(generatedText);

        var message = new ChatMessageDto { Role = "assistant" };
        string finishReason;

        if (toolCalls.Count > 0)
        {
            message.ToolCalls = toolCalls;
            finishReason = "tool_calls";
        }
        else
        {
            message.Content = generatedText;
            finishReason = "stop";
        }

        return new ChatCompletionResponse
        {
            Model = "phi-4-mini",
            Choices =
            [
                new ChatChoice
                {
                    Index = 0,
                    Message = message,
                    FinishReason = finishReason
                }
            ],
            Usage = new UsageDto
            {
                PromptTokens = promptTokenCount,
                CompletionTokens = generatedTokenCount,
                TotalTokens = promptTokenCount + generatedTokenCount
            }
        };
    }

    /// <summary>
    /// Builds a Phi-4-mini chat template prompt from OpenAI-format messages.
    /// </summary>
    private static string BuildPrompt(List<ChatMessageDto> messages, List<ToolDefinition>? tools)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];

            switch (msg.Role)
            {
                case "system":
                    sb.Append("<|system|>\n");
                    sb.Append(msg.Content);
                    if (tools is { Count: > 0 } && i == 0)
                    {
                        sb.Append("\n\nYou have access to the following tools. To call a tool, respond with a JSON object in the format:\n");
                        sb.Append("{\"tool_call\": {\"name\": \"function_name\", \"arguments\": {\"arg\": \"value\"}}}\n\n");
                        sb.Append("Available tools:\n");
                        foreach (var tool in tools)
                        {
                            if (tool.Function is { } fn)
                            {
                                sb.Append($"- {fn.Name}: {fn.Description}");
                                if (fn.Parameters is not null)
                                {
                                    sb.Append($" Parameters: {JsonSerializer.Serialize(fn.Parameters)}");
                                }
                                sb.Append('\n');
                            }
                        }
                    }
                    sb.Append("<|end|>\n");
                    break;

                case "user":
                    sb.Append("<|user|>\n");
                    sb.Append(msg.Content);
                    sb.Append("<|end|>\n");
                    break;

                case "assistant":
                    sb.Append("<|assistant|>\n");
                    if (msg.ToolCalls is { Count: > 0 })
                    {
                        foreach (var tc in msg.ToolCalls)
                        {
                            sb.Append(JsonSerializer.Serialize(new
                            {
                                tool_call = new { name = tc.Function.Name, arguments = JsonSerializer.Deserialize<object>(tc.Function.Arguments) }
                            }));
                            sb.Append('\n');
                        }
                    }
                    else if (msg.Content is not null)
                    {
                        sb.Append(msg.Content);
                    }
                    sb.Append("<|end|>\n");
                    break;

                case "tool":
                    sb.Append("<|user|>\n");
                    sb.Append($"Tool result for call {msg.ToolCallId}:\n{msg.Content}");
                    sb.Append("<|end|>\n");
                    break;
            }
        }

        // Prompt the assistant to respond
        sb.Append("<|assistant|>\n");

        return sb.ToString();
    }

    /// <summary>
    /// Parses the model output for tool call JSON patterns.
    /// </summary>
    private static List<ToolCallDto> ParseToolCalls(string text)
    {
        var toolCalls = new List<ToolCallDto>();

        // Match {"tool_call": {"name": "...", "arguments": {...}}}
        var matches = ToolCallRegex().Matches(text);

        foreach (Match match in matches)
        {
            try
            {
                var json = match.Value;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("tool_call", out var tc))
                {
                    var name = tc.GetProperty("name").GetString() ?? "";
                    var args = tc.TryGetProperty("arguments", out var argsEl)
                        ? argsEl.GetRawText()
                        : "{}";

                    toolCalls.Add(new ToolCallDto
                    {
                        Id = $"call_{Guid.NewGuid():N}"[..24],
                        Type = "function",
                        Function = new FunctionCallDto
                        {
                            Name = name,
                            Arguments = args
                        }
                    });
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON
            }
        }

        return toolCalls;
    }

    [GeneratedRegex("""\{"tool_call"\s*:\s*\{[^}]*"name"\s*:\s*"[^"]+"\s*,\s*"arguments"\s*:\s*\{[^}]*\}\s*\}\s*\}""", RegexOptions.Singleline)]
    private static partial Regex ToolCallRegex();

    public void Dispose()
    {
        DisposeWarmGen();
        _tokenizer.Dispose();
        _model.Dispose();
        _semaphore.Dispose();
    }
}
