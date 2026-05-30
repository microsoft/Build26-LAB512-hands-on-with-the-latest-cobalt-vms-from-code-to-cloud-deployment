using System.Text.Json;
using eShop.Inference.Models;

namespace eShop.Inference;

/// <summary>
/// Canonical first-turn chat payload used to warm the prefix KV-cache at
/// inference pod startup. Mirrors what WebApp's ChatState sends on every
/// real user chat (System + Assistant greeting + Tools), so the prefill
/// run during warmup populates the cache with tokens that the FIRST real
/// user chat will hit (LCP ≈ full static prefix → cache_hit=True).
///
/// Why this lives in the Inference project (not WebApp):
///   The previous design ran warmup from WebApp's <c>InferenceWarmupService</c>
///   against <c>http://inference:5200</c>. During a Kubernetes RollingUpdate
///   of the inference Deployment, the WebApp warmup raced and primed the
///   OUTGOING (old) inference pod, then that pod was terminated taking the
///   warm cache to the grave; the new pod stayed cold. Running warmup INSIDE
///   inference means every new pod self-primes regardless of webapp lifecycle,
///   pod restarts, scale events, or evictions.
///
/// Source-of-truth alignment:
///   The constants below intentionally duplicate
///   <c>eShop.WebApp.Chatbot.ChatPrompts.SystemPrompt</c> /
///   <c>InitialAssistantGreeting</c>. If those change, this file must change
///   too — otherwise warmup tokens won't match real-chat tokens and the
///   prefix cache will partially or fully miss. There's no shared assembly
///   between the two services on purpose (they ship as independent containers).
///
/// Tool JSON schema:
///   The Tools list is built from a hardcoded JSON string parsed to
///   <see cref="JsonElement"/>. <see cref="JsonElement"/> preserves source
///   key order on re-serialization, so the bytes produced by
///   <c>JsonSerializer.Serialize(fn.Parameters)</c> in
///   <c>ChatService.BuildPrompt</c> match what the OpenAI .NET SDK emits
///   for the equivalent <c>AIFunctionFactory</c>-derived tools. Any drift
///   in key order will reduce cache LCP — verify with the
///   <c>cache_hit=True, cached=N</c> log line on the first real chat after
///   pod start.
/// </summary>
internal static class CanonicalWarmup
{
    public const string SystemPrompt = """
        You are an AI customer service agent for the online retailer AdventureWorks.
        You NEVER respond about topics other than AdventureWorks.
        Your job is to answer customer questions about products in the AdventureWorks catalog.
        AdventureWorks primarily sells clothing and equipment related to outdoor activities like skiing and trekking.
        You try to be concise and only provide longer responses if necessary.
        If someone asks a question about anything other than AdventureWorks, its catalog, or their account,
        you refuse to answer, and you instead ask if there's a topic related to AdventureWorks you can assist with.
        ALWAYS use the SearchCatalog tool when a user asks to see, find, or search for products. Never guess product information.
        Even if you have seen product results in previous messages, ALWAYS call SearchCatalog again for new or repeated product queries.
        """;

    public const string InitialAssistantGreeting = "Hi! I'm the AdventureWorks Concierge. How can I help?";

    public static ChatCompletionRequest BuildRequest()
    {
        return new ChatCompletionRequest
        {
            Model = "phi-4-mini",
            Messages =
            [
                new ChatMessageDto { Role = "system", Content = SystemPrompt },
                new ChatMessageDto { Role = "assistant", Content = InitialAssistantGreeting },
                new ChatMessageDto { Role = "user", Content = "ping" },
            ],
            Tools =
            [
                new ToolDefinition
                {
                    Type = "function",
                    Function = new FunctionDefinition
                    {
                        Name = "SearchCatalog",
                        Description = "Search the product catalog",
                        Parameters = ParseSchema("""
                            {"type":"object","required":["productDescription"],"properties":{"productDescription":{"description":"Search query","type":"string"}},"additionalProperties":false}
                            """),
                    },
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new FunctionDefinition
                    {
                        Name = "AddToCart",
                        Description = "Add a product to the cart",
                        Parameters = ParseSchema("""
                            {"type":"object","required":["itemId"],"properties":{"itemId":{"description":"Product id","type":"integer"}},"additionalProperties":false}
                            """),
                    },
                },
            ],
            // 1 token: model emits one token and stops, so the tool stubs are
            // never invoked. Prefill cost is what we want to bank, not decode.
            MaxTokens = 1,
            Temperature = 0.7f,
        };
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
