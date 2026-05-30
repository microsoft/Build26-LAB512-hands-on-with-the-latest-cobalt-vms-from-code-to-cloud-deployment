using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using OrtTensor = Microsoft.ML.OnnxRuntime.Tensors.Tensor<float>;

namespace eShop.Inference.Services;

/// <summary>
/// Generates embeddings using the all-MiniLM-L6-v2 ONNX model with BERT tokenization,
/// mean pooling, and L2 normalization.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    private const int MaxSequenceLength = 512;
    private const int EmbeddingDimension = 384;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(string modelPath, string vocabPath, ILogger<EmbeddingService> logger)
    {
        _logger = logger;

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = Environment.ProcessorCount,
            IntraOpNumThreads = Environment.ProcessorCount
        };

        _session = new InferenceSession(modelPath, options);
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions
        {
            LowerCaseBeforeTokenization = true
        });

        _logger.LogInformation("Embedding model loaded from {ModelPath} with {Dim}-dim output",
            modelPath, EmbeddingDimension);
    }

    /// <summary>
    /// Generates embeddings for a batch of input texts.
    /// </summary>
    public float[][] GenerateEmbeddings(IReadOnlyList<string> inputs)
    {
        var sw = Stopwatch.StartNew();
        var results = new float[inputs.Count][];

        for (int i = 0; i < inputs.Count; i++)
        {
            results[i] = GenerateSingleEmbedding(inputs[i]);
        }

        _logger.LogInformation("Embeddings: {Count} inputs in {Ms}ms ({AvgMs:F1}ms/input)",
            inputs.Count, sw.ElapsedMilliseconds,
            sw.ElapsedMilliseconds / (double)inputs.Count);

        return results;
    }

    private float[] GenerateSingleEmbedding(string text)
    {
        // Tokenize
        var encoded = _tokenizer.EncodeToIds(text, MaxSequenceLength, out _, out _);
        var tokenCount = encoded.Count;

        // Build input tensors
        var inputIds = new long[tokenCount];
        var attentionMask = new long[tokenCount];
        var tokenTypeIds = new long[tokenCount];

        for (int i = 0; i < tokenCount; i++)
        {
            inputIds[i] = encoded[i];
            attentionMask[i] = 1;
            tokenTypeIds[i] = 0;
        }

        var shape = new[] { 1, tokenCount };
        var inputIdsTensor = new DenseTensor<long>(inputIds, shape);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, shape);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, shape);

        var onnxInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        // Run inference
        using var results = _session.Run(onnxInputs);

        // Get the token embeddings output (shape: [1, seq_len, 384])
        var outputTensor = results.First().AsTensor<float>();

        // Mean pooling over non-padding tokens
        var embedding = MeanPool(outputTensor, tokenCount);

        // L2 normalize
        L2Normalize(embedding);

        return embedding;
    }

    private static float[] MeanPool(OrtTensor tokenEmbeddings, int tokenCount)
    {
        var embedding = new float[EmbeddingDimension];

        for (int t = 0; t < tokenCount; t++)
        {
            for (int d = 0; d < EmbeddingDimension; d++)
            {
                embedding[d] += tokenEmbeddings[0, t, d];
            }
        }

        for (int d = 0; d < EmbeddingDimension; d++)
        {
            embedding[d] /= tokenCount;
        }

        return embedding;
    }

    private static void L2Normalize(float[] vector)
    {
        float norm = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            norm += vector[i] * vector[i];
        }

        norm = MathF.Sqrt(norm);

        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
