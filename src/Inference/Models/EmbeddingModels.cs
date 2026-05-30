using System.Text.Json.Serialization;

namespace eShop.Inference.Models;

public class EmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "all-MiniLM-L6-v2";

    [JsonPropertyName("input")]
    public object Input { get; set; } = "";
}

public class EmbeddingResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "all-MiniLM-L6-v2";

    [JsonPropertyName("data")]
    public List<EmbeddingData> Data { get; set; } = [];

    [JsonPropertyName("usage")]
    public UsageDto? Usage { get; set; }
}

public class EmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "embedding";

    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = [];

    [JsonPropertyName("index")]
    public int Index { get; set; }
}

public class UsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}
