using System.Globalization;
using System.Text.Json;

namespace Jiten.Parser.SpeechBoundaries;

/// <summary>Evaluates the LightGBM tree dump written by scripts/speech_boundaries/train.py; decision rules mirror LightGBM's own so scores match its predict().</summary>
public sealed class SpeechBoundaryModel
{
    private const byte MissingNone = 0;
    private const byte MissingZero = 1;
    private const byte MissingNaN = 2;

    // All trees share flat node arrays; a child index < 0 is leaf ~index into _leafValues, LightGBM's own convention.
    private readonly int[] _treeRoots;
    private readonly int[] _feature;
    private readonly double[] _threshold;
    private readonly int[] _left;
    private readonly int[] _right;
    private readonly byte[] _missing;
    private readonly bool[] _defaultLeft;
    // Categorical nodes point at a bitset slice of _categoryBits; numerical nodes have length 0.
    private readonly int[] _bitsOffset;
    private readonly int[] _bitsLength;
    private readonly ulong[] _categoryBits;
    private readonly double[] _leafValues;

    private readonly Dictionary<string, int>[] _vocab;
    private readonly int _featureCount;

    private SpeechBoundaryModel(Builder b, Dictionary<string, int>[] vocab, int featureCount)
    {
        _treeRoots = b.TreeRoots.ToArray();
        _feature = b.Feature.ToArray();
        _threshold = b.Threshold.ToArray();
        _left = b.Left.ToArray();
        _right = b.Right.ToArray();
        _missing = b.Missing.ToArray();
        _defaultLeft = b.DefaultLeft.ToArray();
        _bitsOffset = b.BitsOffset.ToArray();
        _bitsLength = b.BitsLength.ToArray();
        _categoryBits = b.CategoryBits.ToArray();
        _leafValues = b.LeafValues.ToArray();
        _vocab = vocab;
        _featureCount = featureCount;
    }

    private sealed class Builder
    {
        public readonly List<int> TreeRoots = [];
        public readonly List<int> Feature = [];
        public readonly List<double> Threshold = [];
        public readonly List<int> Left = [];
        public readonly List<int> Right = [];
        public readonly List<byte> Missing = [];
        public readonly List<bool> DefaultLeft = [];
        public readonly List<int> BitsOffset = [];
        public readonly List<int> BitsLength = [];
        public readonly List<ulong> CategoryBits = [];
        public readonly List<double> LeafValues = [];

        public int Add(JsonElement element)
        {
            if (element.TryGetProperty("leaf_value", out var leafValue))
            {
                LeafValues.Add(leafValue.GetDouble());
                return ~(LeafValues.Count - 1);
            }

            int index = Feature.Count;
            Feature.Add(element.GetProperty("split_feature").GetInt32());
            DefaultLeft.Add(element.GetProperty("default_left").GetBoolean());
            Missing.Add(element.GetProperty("missing_type").GetString() switch
            {
                "Zero" => MissingZero,
                "NaN" => MissingNaN,
                _ => MissingNone
            });

            var thresholdElement = element.GetProperty("threshold");
            if (element.GetProperty("decision_type").GetString() == "==")
            {
                var categories = thresholdElement.GetString()!.Split("||").Select(c => int.Parse(c, CultureInfo.InvariantCulture)).ToArray();
                var bits = new ulong[categories.Max() / 64 + 1];
                foreach (var c in categories)
                    bits[c / 64] |= 1UL << (c % 64);

                BitsOffset.Add(CategoryBits.Count);
                BitsLength.Add(bits.Length);
                CategoryBits.AddRange(bits);
                Threshold.Add(0);
            }
            else
            {
                BitsOffset.Add(0);
                BitsLength.Add(0);
                Threshold.Add(thresholdElement.GetDouble());
            }

            Left.Add(0);
            Right.Add(0);
            Left[index] = Add(element.GetProperty("left_child"));
            Right[index] = Add(element.GetProperty("right_child"));
            return index;
        }
    }

    private const string ResourceFileName = "speech_boundary_model.json";

    private static readonly Lazy<SpeechBoundaryModel> DefaultModel = new(() => Load(ResolveResourcePath()));

    /// <summary>The model shipped in Shared/resources, loaded on first use.</summary>
    public static SpeechBoundaryModel Default => DefaultModel.Value;

    private static string ResolveResourcePath()
    {
        string[] candidates =
        [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", ResourceFileName),
            Path.Combine("Shared", "resources", ResourceFileName),
            Path.Combine("..", "Shared", "resources", ResourceFileName)
        ];

        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException($"{ResourceFileName} not found in the resources folder", ResourceFileName);
    }

    public static SpeechBoundaryModel Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;

        var features = root.GetProperty("features").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var expected = LineBreakFeatureExtractor.CategoricalNames.Concat(LineBreakFeatureExtractor.NumericNames).ToArray();
        if (!features.SequenceEqual(expected))
            throw new InvalidDataException($"Model features [{string.Join(",", features)}] do not match the extractor's [{string.Join(",", expected)}]");

        var vocabRoot = root.GetProperty("vocab");
        var vocab = LineBreakFeatureExtractor.CategoricalNames
            .Select(name => vocabRoot.GetProperty(name).EnumerateArray()
                                     .Select((e, i) => (Value: e.GetString()!, Index: i))
                                     .ToDictionary(p => p.Value, p => p.Index, StringComparer.Ordinal))
            .ToArray();

        var builder = new Builder();
        foreach (var tree in root.GetProperty("trees").EnumerateArray())
            builder.TreeRoots.Add(builder.Add(tree.GetProperty("tree_structure")));

        return new SpeechBoundaryModel(builder, vocab, features.Length);
    }

    /// <summary>Unknown categories become NaN, which is what LightGBM does with the -1 the trainer feeds it.</summary>
    public double[] Encode(LineBreakFeatureExtractor.LineBreak lineBreak)
    {
        var values = new double[_featureCount];
        for (int i = 0; i < lineBreak.Categorical.Length; i++)
            values[i] = _vocab[i].TryGetValue(lineBreak.Categorical[i], out var code) ? code : double.NaN;

        for (int i = 0; i < lineBreak.Numeric.Length; i++)
            values[lineBreak.Categorical.Length + i] = lineBreak.Numeric[i];

        return values;
    }

    /// <summary>Probability that the break ends a sentence.</summary>
    public double Predict(LineBreakFeatureExtractor.LineBreak lineBreak) => PredictEncoded(Encode(lineBreak));

    public double PredictEncoded(ReadOnlySpan<double> features)
    {
        double sum = 0;
        foreach (var root in _treeRoots)
        {
            int current = root;
            while (current >= 0)
                current = _bitsLength[current] != 0
                    ? CategoricalDecision(current, features[_feature[current]])
                    : NumericalDecision(current, features[_feature[current]]);

            sum += _leafValues[~current];
        }

        return 1.0 / (1.0 + Math.Exp(-sum));
    }

    private int NumericalDecision(int node, double value)
    {
        byte missing = _missing[node];
        if (missing != MissingNaN && double.IsNaN(value))
            value = 0;

        if ((missing == MissingZero && Math.Abs(value) <= 1e-35) || (missing == MissingNaN && double.IsNaN(value)))
            return _defaultLeft[node] ? _left[node] : _right[node];

        return value <= _threshold[node] ? _left[node] : _right[node];
    }

    private int CategoricalDecision(int node, double value)
    {
        int category;
        if (double.IsNaN(value))
        {
            if (_missing[node] == MissingNaN)
                return _right[node];
            category = 0;
        }
        else
        {
            category = (int)value;
            if (category < 0)
                return _right[node];
        }

        int word = category / 64;
        return word < _bitsLength[node] && (_categoryBits[_bitsOffset[node] + word] & (1UL << (category % 64))) != 0
            ? _left[node]
            : _right[node];
    }
}
