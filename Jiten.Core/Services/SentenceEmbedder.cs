using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Jiten.Core.Services;

/// <summary>
/// Runs an XLM-R based sentence encoder (ONNX + SentencePiece) and returns L2-normalised vectors.
/// Thread-safe once loaded; the model directory must hold onnx/model.onnx and sentencepiece.bpe.model.
/// Pooling and prefixes are decided by the directory name, see <see cref="Profile"/>.
/// </summary>
public sealed class SentenceEmbedder : IDisposable
{
    /// <summary>Configuration key for the model directory, shared by the API and the CLI.</summary>
    public const string ModelDirConfigKey = "DescriptionEmbeddingModelDir";

    /// <summary>Stored alongside each vector so a model swap invalidates old vectors.</summary>
    public string ModelName { get; }

    private enum Pooling { Mean, Cls }

    /// <summary>How each supported checkpoint expects to be driven; getting pooling or prefixes wrong silently degrades results.</summary>
    private sealed record Profile(string Name, Pooling Pooling, string QueryPrefix, string PassagePrefix);

    private static readonly Dictionary<string, Profile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["multilingual-e5-small"] = new("multilingual-e5-small", Pooling.Mean, "query: ", "passage: "),
        ["multilingual-e5-base"] = new("multilingual-e5-base", Pooling.Mean, "query: ", "passage: "),
        ["multilingual-e5-large"] = new("multilingual-e5-large", Pooling.Mean, "query: ", "passage: "),
        ["bge-m3"] = new("bge-m3", Pooling.Cls, "", "")
    };

    /// <summary>XLM-R positional limit including the two sentinel tokens.</summary>
    private const int MaxTokens = 512;

    // XLM-R vocabulary = fairseq layout: <s>=0 <pad>=1 </s>=2 <unk>=3, then every SentencePiece id shifted by one.
    private const long BosId = 0, PadId = 1, EosId = 2, UnkId = 3;

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly bool _wantsTokenTypeIds;
    private readonly Profile _profile;

    public int Dimension { get; }

    /// <summary>Configuration key for the ONNX intra-op thread cap; unset lets OnnxRuntime take every core.</summary>
    public const string ThreadsConfigKey = "DescriptionEmbeddingThreads";

    /// <param name="intraOpThreads">Cores a single forward pass may use; the API caps this so a backfill cannot starve request handling.</param>
    public SentenceEmbedder(string modelDir, int? intraOpThreads = null)
    {
        var modelPath = Path.Combine(modelDir, "onnx", "model.onnx");
        var spmPath = Path.Combine(modelDir, "sentencepiece.bpe.model");
        if (!File.Exists(modelPath) || !File.Exists(spmPath))
            throw new FileNotFoundException($"Sentence embedding model not found in '{modelDir}' (expected onnx/model.onnx and sentencepiece.bpe.model)");

        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(modelDir));
        if (!Profiles.TryGetValue(dirName, out _profile!))
            throw new NotSupportedException($"No embedding profile for model directory '{dirName}' (known: {string.Join(", ", Profiles.Keys)})");
        ModelName = _profile.Name;

        using var spm = File.OpenRead(spmPath);
        _tokenizer = SentencePieceTokenizer.Create(spm, false, false);

        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (intraOpThreads is > 0)
            options.IntraOpNumThreads = intraOpThreads.Value;
        _session = new InferenceSession(modelPath, options);
        _wantsTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
        Dimension = _session.OutputMetadata.First().Value.Dimensions[^1];
    }

    public static bool IsAvailable(string? modelDir) =>
        !string.IsNullOrWhiteSpace(modelDir)
        && File.Exists(Path.Combine(modelDir, "onnx", "model.onnx"))
        && File.Exists(Path.Combine(modelDir, "sentencepiece.bpe.model"))
        && Profiles.ContainsKey(Path.GetFileName(Path.TrimEndingDirectorySeparator(modelDir)));

    public static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public float[] EmbedQuery(string text) => Embed([_profile.QueryPrefix + text])[0];

    public float[][] EmbedPassages(IReadOnlyList<string> texts)
    {
        var prefixed = new string[texts.Count];
        for (var i = 0; i < texts.Count; i++)
            prefixed[i] = _profile.PassagePrefix + texts[i];
        return Embed(prefixed);
    }

    /// <summary>Encodes a batch in one forward pass, right-padded to the longest sequence.</summary>
    public float[][] Embed(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
            return [];

        var sequences = new List<long>[texts.Count];
        var maxLen = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            sequences[i] = Tokenize(texts[i]);
            maxLen = Math.Max(maxLen, sequences[i].Count);
        }

        var batch = texts.Count;
        var inputIds = new DenseTensor<long>([batch, maxLen]);
        var mask = new DenseTensor<long>([batch, maxLen]);
        var tokenTypes = new DenseTensor<long>([batch, maxLen]);
        for (var b = 0; b < batch; b++)
        {
            var seq = sequences[b];
            for (var t = 0; t < maxLen; t++)
            {
                var present = t < seq.Count;
                inputIds[b, t] = present ? seq[t] : PadId;
                mask[b, t] = present ? 1 : 0;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask)
        };
        if (_wantsTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes));

        using var results = _session.Run(inputs);
        var hidden = results[0].AsTensor<float>();
        var dim = hidden.Dimensions[2];

        var output = new float[batch][];
        for (var b = 0; b < batch; b++)
        {
            var len = sequences[b].Count;
            var v = new float[dim];
            if (_profile.Pooling == Pooling.Cls)
            {
                for (var k = 0; k < dim; k++)
                    v[k] = hidden[b, 0, k];
            }
            else
            {
                for (var t = 0; t < len; t++)
                    for (var k = 0; k < dim; k++)
                        v[k] += hidden[b, t, k];
                for (var k = 0; k < dim; k++)
                    v[k] /= len;
            }

            var norm = 0f;
            for (var k = 0; k < dim; k++)
                norm += v[k] * v[k];

            norm = MathF.Sqrt(norm);
            if (norm > 0)
                for (var k = 0; k < dim; k++)
                    v[k] /= norm;
            output[b] = v;
        }

        return output;
    }

    private List<long> Tokenize(string text)
    {
        var pieces = _tokenizer.EncodeToIds(text);
        var ids = new List<long>(Math.Min(pieces.Count, MaxTokens - 2) + 2) { BosId };
        foreach (var id in pieces)
        {
            if (ids.Count >= MaxTokens - 1)
                break;
            ids.Add(id == 0 ? UnkId : id + 1);
        }

        ids.Add(EosId);
        return ids;
    }

    public void Dispose() => _session.Dispose();
}
