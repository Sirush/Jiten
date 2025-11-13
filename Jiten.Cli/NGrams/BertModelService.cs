using Jiten.Core.Data.JMDict;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jiten.Cli.NGrams;

public class BertModelService : IDisposable
{
    private readonly InferenceSession? _session;
    private readonly JapaneseBertTokenizer _tokenizer;
    private readonly DisambiguationConfig _config;
    private readonly SemaphoreSlim _semaphore;
    private bool _isInitialized;

    public BertModelService(
        DisambiguationConfig config)
    {
        _config = config;
        _semaphore = new SemaphoreSlim(config.MaxConcurrentInferences);

        try
        {
            if (!string.IsNullOrEmpty(config.ModelPath) && File.Exists(config.ModelPath))
            {
                var sessionOptions = new SessionOptions();

                // Try to use GPU if available
                try
                {
                    sessionOptions.AppendExecutionProvider_CUDA(0);
                    Console.WriteLine("Using CUDA GPU acceleration for BERT");
                }
                catch
                {
                    Console.WriteLine("CUDA not available, using CPU for BERT");
                }

                _session = new InferenceSession(config.ModelPath, sessionOptions);
                _tokenizer = new JapaneseBertTokenizer(config.VocabPath);
                _isInitialized = true;

                Console.WriteLine("BERT model loaded from {0}", config.ModelPath);
            }
            else
            {
                Console.WriteLine(
                                  "BERT model not found at {0}, BERT disambiguation disabled",
                                  config.ModelPath);
                _isInitialized = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to initialize BERT model");
            _isInitialized = false;
        }
    }

    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Get BERT embedding for a text
    /// </summary>
    public async Task<BertEmbedding> GetEmbeddingAsync(string text)
    {
        if (!_isInitialized || _session == null)
        {
            throw new InvalidOperationException("BERT model not initialized");
        }

        await _semaphore.WaitAsync();
        try
        {
            // Tokenize input
            var tokens = _tokenizer.Tokenize(text);
            var inputIds = _tokenizer.ConvertToIds(tokens);

            // Truncate if too long
            if (inputIds.Length > _config.MaxSequenceLength)
            {
                inputIds = inputIds.Take(_config.MaxSequenceLength).ToArray();
            }

            var sequenceLength = inputIds.Length;

            // Create attention mask (all 1s for real tokens)
            var attentionMask = CreateAttentionMask(sequenceLength);

            // Create token type IDs (all 0s for single sequence)
            var tokenTypeIds = CreateTokenTypeIds(sequenceLength);

            // Create ONNX input tensors
            var inputIdsTensor = new DenseTensor<long>(
                                                       inputIds.Select(x => (long)x).ToArray(),
                                                       new[] { 1, sequenceLength });

            var attentionMaskTensor = new DenseTensor<long>(
                                                            attentionMask.Select(x => (long)x).ToArray(),
                                                            new[] { 1, sequenceLength });

            var tokenTypeIdsTensor = new DenseTensor<long>(
                                                           tokenTypeIds.Select(x => (long)x).ToArray(),
                                                           new[] { 1, sequenceLength });

            var inputs = new List<NamedOnnxValue>
                         {
                             NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                             NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
                             NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
                         };

            // Run inference
            using var results = await Task.Run(() => _session.Run(inputs));

            // Get last hidden state (usually first output)
            // Output shape: [batch_size, sequence_length, hidden_size]
            var outputTensor = results.First().AsEnumerable<float>().ToArray();

            // Use [CLS] token embedding (first token)
            var hiddenSize = 768; // Standard BERT hidden size
            var clsEmbedding = outputTensor.Take(hiddenSize).ToArray();

            return new BertEmbedding { Embedding = clsEmbedding, TokenCount = tokens.Count, TokenIds = inputIds };
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error computing embedding for text: {0}", text);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Score multiple candidates against a context
    /// </summary>
    public async Task<List<CandidateScore>> ScoreCandidatesAsync(
        string context,
        List<JmDictWord> candidates)
    {
        if (!_isInitialized)
        {
            Console.WriteLine("BERT not initialized, returning empty scores");
            return new List<CandidateScore>();
        }

        var scores = new List<CandidateScore>();

        try
        {
            // Get context embedding
            var contextEmbedding = await GetEmbeddingAsync(context);

            // Score each candidate
            foreach (var candidate in candidates.Take(_config.MaxCandidates))
            {
                try
                {
                    // Build candidate definition context
                    var candidateText = BuildCandidateText(candidate);
                    var candidateEmbedding = await GetEmbeddingAsync(candidateText);

                    // Calculate cosine similarity
                    float similarity = CosineSimilarity(
                                                        contextEmbedding.Embedding,
                                                        candidateEmbedding.Embedding);

                    scores.Add(new CandidateScore
                               {
                                   Candidate = candidate, BertScore = similarity, PriorityScore = candidate.GetPriorityScore(false)
                               });
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                                      "Error scoring candidate {0}",
                                      candidate.WordId);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error in ScoreCandidatesAsync for context: {0}", context);
        }

        return scores;
    }

    /// <summary>
    /// Create attention mask (1 for real tokens, 0 for padding)
    /// </summary>
    private int[] CreateAttentionMask(int length)
    {
        return Enumerable.Repeat(1, length).ToArray();
    }

    /// <summary>
    /// Create token type IDs (0 for single sequence, would use 0/1 for sentence pairs)
    /// </summary>
    private int[] CreateTokenTypeIds(int length)
    {
        return Enumerable.Repeat(0, length).ToArray();
    }

    /// <summary>
    /// Build text representation of a candidate word for embedding
    /// </summary>
    private string BuildCandidateText(JmDictWord word)
    {
        // Use word + first definition
        var wordText = word.Readings.FirstOrDefault() ?? "";

        var firstDefinition = word.Definitions.FirstOrDefault();
        if (firstDefinition == null)
        {
            return wordText;
        }

        // Get first English meaning
        var meaning = firstDefinition.EnglishMeanings.FirstOrDefault() ?? "";

        // Format as "word: meaning"
        return $"{wordText}：{meaning}";
    }

    /// <summary>
    /// Calculate cosine similarity between two vectors
    /// </summary>
    public float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vectors must have same length");
        }

        float dotProduct = 0f;
        float normA = 0f;
        float normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);

        if (denominator == 0)
        {
            return 0f;
        }

        return dotProduct / denominator;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _semaphore?.Dispose();
    }
}