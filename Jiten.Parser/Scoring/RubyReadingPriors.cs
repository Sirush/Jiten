using Jiten.Core.Data.JMDict;
using MessagePack;
using MessagePack.Resolvers;

namespace Jiten.Parser.Scoring;

internal readonly record struct RubyScoreResult(int Score, int Support, string? Level);

// Immutable readings table with precomputed Total/EffectiveK (n-gram dicts never change after load).
internal sealed class ReadingTable
{
    public readonly Dictionary<string, int> Readings;
    public readonly int Total;
    public readonly int EffectiveK;

    public ReadingTable(Dictionary<string, int> readings)
    {
        Readings = readings;
        int total = 0;
        foreach (var v in readings.Values) total += v;
        Total = total;

        int threshold = Math.Max(10, (int)(total * 0.02));
        int count = 0;
        foreach (var v in readings.Values)
            if (v >= threshold) count++;
        EffectiveK = Math.Max(count, 2);
    }
}

// Context n-grams grouped under their centre kanji form so lookups hit small per-form maps, not corpus-wide tables.
internal sealed class FormPriors(ReadingTable unigram)
{
    public readonly ReadingTable Unigram = unigram;
    public Dictionary<string, ReadingTable>? Left;
    public Dictionary<string, ReadingTable>? Right;
    public Dictionary<string, ReadingTable>? Left2;
    public Dictionary<string, ReadingTable>? Right2;
    public Dictionary<(string Left, string Right), ReadingTable>? Trigrams;
}

internal sealed class RubyReadingPriors
{
    private static readonly Lazy<RubyReadingPriors?> Instance =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static bool Enabled = true;

    public static RubyReadingPriors? Current => Enabled ? Instance.Value : null;

    private readonly Dictionary<string, FormPriors> _forms;

    private readonly Dictionary<string, List<(string kanjiForm, int count)>> _reverseIndex;

    private RubyReadingPriors(Dictionary<string, FormPriors> forms)
    {
        _forms = forms;

        _reverseIndex = new Dictionary<string, List<(string, int)>>();
        foreach (var (kanjiForm, formPriors) in _forms)
        {
            foreach (var (reading, count) in formPriors.Unigram.Readings)
            {
                if (!_reverseIndex.TryGetValue(reading, out var list))
                {
                    list = [];
                    _reverseIndex[reading] = list;
                }
                list.Add((kanjiForm, count));
            }
        }

        foreach (var list in _reverseIndex.Values)
            list.Sort((a, b) => b.count.CompareTo(a.count));
    }

    private const double Alpha = 0.5;
    private const int MinUnigramTotal = 20;
    private const int MinBigramTotal = 8;
    private const int MinTrigramTotal = 5;
    private const double UnigramHalfLife = 50;
    private const double BigramHalfLife = 20;
    private const double TrigramHalfLife = 12;
    private const double ReliabilityThresholdTri = 0.30;
    private const double ReliabilityThresholdBi = 0.28;
    private const double RubyPriorWeight = 18;
    private const int MaxBonus = 35;
    private const int MaxPenalty = 20;
    private const double SupportReference = 50;

    public int ScoreCandidate(string? kanjiForm, string? reading, string? leftContext, string? rightContext,
        string? left2Context = null, string? right2Context = null)
        => ScoreCandidateDetailed(kanjiForm, reading, leftContext, rightContext, left2Context, right2Context).Score;

    public RubyScoreResult ScoreCandidateDetailed(string? kanjiForm, string? reading,
        string? leftContext, string? rightContext,
        string? left2Context = null, string? right2Context = null)
    {
        if (kanjiForm == null || reading == null) return default;
        if (!_forms.TryGetValue(kanjiForm, out var formPriors)) return default;
        return ScoreFormDetailed(formPriors, reading, leftContext, rightContext, left2Context, right2Context);
    }

    private RubyScoreResult ScoreFormDetailed(FormPriors formPriors, string reading,
        string? leftContext, string? rightContext, string? left2Context, string? right2Context)
    {
        var uniTable = formPriors.Unigram;
        int uniTotal = uniTable.Total;
        if (uniTotal < MinUnigramTotal) return default;

        int effectiveK = uniTable.EffectiveK;
        double uniformLogP = Math.Log(1.0 / effectiveK);

        if (leftContext != null && rightContext != null)
        {
            var bestTri = TryTrigramExpanded(formPriors.Trigrams, leftContext, rightContext, reading, uniformLogP);
            if (bestTri.Level != null) return bestTri;
        }

        double? bestBiLogP = null;
        int bestBiTotal = 0;
        string? bestBiLevel = null;

        TryBigramExpanded(formPriors.Left, leftContext,
            reading, "left-bigram", ref bestBiLogP, ref bestBiTotal, ref bestBiLevel);
        TryBigramExpanded(formPriors.Right, rightContext,
            reading, "right-bigram", ref bestBiLogP, ref bestBiTotal, ref bestBiLevel);

        if (bestBiLogP.HasValue)
            return new RubyScoreResult(ComputeBonus(bestBiLogP.Value, uniformLogP, bestBiTotal), bestBiTotal, bestBiLevel);

        double? bestSkipLogP = null;
        int bestSkipTotal = 0;
        string? bestSkipLevel = null;

        TryBigramExpanded(formPriors.Left2, left2Context,
            reading, "left2-bigram", ref bestSkipLogP, ref bestSkipTotal, ref bestSkipLevel);
        TryBigramExpanded(formPriors.Right2, right2Context,
            reading, "right2-bigram", ref bestSkipLogP, ref bestSkipTotal, ref bestSkipLevel);

        if (bestSkipLogP.HasValue)
            return new RubyScoreResult(ComputeBonus(bestSkipLogP.Value, uniformLogP, bestSkipTotal), bestSkipTotal, bestSkipLevel);

        double uniLogP = Math.Log((uniTable.Readings.GetValueOrDefault(reading) + Alpha) / (uniTotal + Alpha * effectiveK));
        return new RubyScoreResult(ComputeBonus(uniLogP, uniformLogP, uniTotal), uniTotal, "unigram");
    }

    private static bool ShouldExpandContext(string context)
    {
        if (context.Length < 2) return false;
        foreach (var c in context)
            if (c is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿')
                  or (>= 'ァ' and <= 'ヶ') or 'ー')
                return false;
        return true;
    }

    private void TryBigramExpanded(
        Dictionary<string, ReadingTable>? table, string? context,
        string reading, string level,
        ref double? bestLogP, ref int bestTotal, ref string? bestLevel)
    {
        if (table == null || context == null) return;

        TryBigram(table, context, reading, level, ref bestLogP, ref bestTotal, ref bestLevel);

        if (!ShouldExpandContext(context) || !_reverseIndex.TryGetValue(context, out var altForms))
            return;

        int tried = 0;
        foreach (var (altCtx, _) in altForms)
        {
            if (tried >= 3) break;
            TryBigram(table, altCtx, reading, level, ref bestLogP, ref bestTotal, ref bestLevel);
            tried++;
        }
    }

    private RubyScoreResult TryTrigramExpanded(
        Dictionary<(string Left, string Right), ReadingTable>? trigrams,
        string leftContext, string rightContext,
        string reading, double uniformLogP)
    {
        var best = default(RubyScoreResult);
        if (trigrams == null) return best;

        TryTrigramSingle(trigrams, leftContext, rightContext, reading, uniformLogP, ref best);

        if (ShouldExpandContext(leftContext) && _reverseIndex.TryGetValue(leftContext, out var leftAlts))
        {
            int tried = 0;
            foreach (var (altLeft, _) in leftAlts)
            {
                if (tried >= 3) break;
                TryTrigramSingle(trigrams, altLeft, rightContext, reading, uniformLogP, ref best);
                tried++;
            }
        }

        if (ShouldExpandContext(rightContext) && _reverseIndex.TryGetValue(rightContext, out var rightAlts))
        {
            int tried = 0;
            foreach (var (altRight, _) in rightAlts)
            {
                if (tried >= 3) break;
                TryTrigramSingle(trigrams, leftContext, altRight, reading, uniformLogP, ref best);
                tried++;
            }
        }

        return best;
    }

    private static void TryTrigramSingle(
        Dictionary<(string Left, string Right), ReadingTable> trigrams,
        string leftCtx, string rightCtx,
        string reading, double uniformLogP, ref RubyScoreResult best)
    {
        if (!trigrams.TryGetValue((leftCtx, rightCtx), out var triTable))
            return;
        int triTotal = triTable.Total;
        double triReliability = (double)triTotal / (triTotal + TrigramHalfLife);
        if (triTotal < MinTrigramTotal || triReliability < ReliabilityThresholdTri)
            return;
        int triK = triTable.EffectiveK;
        double logP = Math.Log((triTable.Readings.GetValueOrDefault(reading) + Alpha) / (triTotal + Alpha * triK));
        var score = ComputeBonus(logP, uniformLogP, triTotal);
        if (score > best.Score)
            best = new RubyScoreResult(score, triTotal, "trigram");
    }

    private static void TryBigram(Dictionary<string, ReadingTable> table,
        string context, string reading, string level,
        ref double? bestLogP, ref int bestTotal, ref string? bestLevel)
    {
        if (!table.TryGetValue(context, out var rt)) return;

        int total = rt.Total;
        double reliability = (double)total / (total + BigramHalfLife);
        if (total < MinBigramTotal || reliability < ReliabilityThresholdBi) return;

        int k = rt.EffectiveK;
        double logP = Math.Log((rt.Readings.GetValueOrDefault(reading) + Alpha) / (total + Alpha * k));
        if (!bestLogP.HasValue || reliability > (double)bestTotal / (bestTotal + BigramHalfLife))
        {
            bestLogP = logP;
            bestTotal = total;
            bestLevel = level;
        }
    }

    private static int ComputeBonus(double logP, double uniformLogP, int totalCount)
    {
        double rawBonus = RubyPriorWeight * (logP - uniformLogP);
        double supportScale = Math.Min(1.0, Math.Log(totalCount) / Math.Log(SupportReference));
        int effectiveMax = (int)(MaxBonus * supportScale);
        int effectiveMin = (int)(MaxPenalty * supportScale);
        return Math.Clamp((int)Math.Round(rawBonus), -effectiveMin, effectiveMax);
    }

    public int ScoreKanaReverse(string reading, JmDictWord word, string? leftContext, string? rightContext,
        string? left2Context = null, string? right2Context = null)
        => ScoreKanaReverseDetailed(reading, word, leftContext, rightContext, left2Context, right2Context).Score;

    public RubyScoreResult ScoreKanaReverseDetailed(string reading, JmDictWord word,
        string? leftContext, string? rightContext,
        string? left2Context = null, string? right2Context = null)
    {
        if (leftContext == null && rightContext == null) return default;

        var best = default(RubyScoreResult);
        foreach (var form in word.Forms)
        {
            if (form.FormType != JmDictFormType.KanjiForm) continue;
            if (!_forms.TryGetValue(form.Text, out var formPriors) || !formPriors.Unigram.Readings.ContainsKey(reading))
                continue;

            var result = ScoreFormDetailed(formPriors, reading,
                leftContext, rightContext, left2Context, right2Context);
            if (result.Level is not ("trigram" or "left-bigram" or "right-bigram"
                or "left2-bigram" or "right2-bigram"))
                continue;
            if (result.Score > best.Score)
                best = result;
        }

        return best;
    }

    internal static string ToHiragana(string text)
    {
        bool needsConversion = false;
        foreach (var c in text)
        {
            if (c >= 'ァ' && c <= 'ヶ') { needsConversion = true; break; }
        }
        if (!needsConversion) return text;

        return string.Create(text.Length, text, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = (c >= 'ァ' && c <= 'ヶ') ? (char)(c - 0x60) : c;
            }
        });
    }

    public string? GetKanaReading(JmDictWord word, byte candidateReadingIndex)
    {
        if (word.Forms == null) return null;

        int kanaFormCount = 0;
        JmDictWordForm? firstKanaForm = null;
        JmDictWordForm? candidateForm = null;

        foreach (var form in word.Forms)
        {
            if (form.ReadingIndex == candidateReadingIndex)
                candidateForm = form;
            if (form.FormType == JmDictFormType.KanaForm)
            {
                kanaFormCount++;
                firstKanaForm ??= form;
            }
        }

        if (kanaFormCount == 0) return null;
        if (kanaFormCount == 1) return ToHiragana(firstKanaForm!.Text);

        if (candidateForm != null && candidateForm.FormType == JmDictFormType.KanaForm)
            return ToHiragana(candidateForm.Text);

        return ToHiragana(firstKanaForm!.Text);
    }

    public string? GetKanjiForm(JmDictWord word, string? surface = null)
    {
        if (word.Forms == null) return null;

        if (surface != null)
        {
            foreach (var form in word.Forms)
            {
                if (form.FormType == JmDictFormType.KanjiForm && form.Text == surface && _forms.ContainsKey(form.Text))
                    return form.Text;
            }
        }

        foreach (var form in word.Forms)
        {
            if (form.FormType == JmDictFormType.KanjiForm && _forms.ContainsKey(form.Text))
                return form.Text;
        }
        return null;
    }

    private static RubyReadingPriors? Load()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "ruby_reading_priors.msgpack");
        if (!File.Exists(path))
        {
            path = Path.Combine("Shared", "resources", "ruby_reading_priors.msgpack");
            if (!File.Exists(path))
            {
                path = Path.Combine("..", "Shared", "resources", "ruby_reading_priors.msgpack");
                if (!File.Exists(path)) return null;
            }
        }

        var bytes = File.ReadAllBytes(path);
        var options = ContractlessStandardResolver.Options;
        var raw = MessagePackSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, int>>>>(bytes, options);

        var forms = new Dictionary<string, FormPriors>();
        foreach (var (kanjiForm, readings) in raw.GetValueOrDefault("Unigrams") ?? [])
            forms[kanjiForm] = new FormPriors(new ReadingTable(readings));

        // Scoring reaches n-grams only through their centre form's unigram, so orphan n-grams are dropped.
        foreach (var (key, readings) in raw.GetValueOrDefault("LeftBigrams") ?? [])
            if (TrySplitPair(key, out var left, out var form) && forms.TryGetValue(form, out var fp))
                (fp.Left ??= [])[left] = new ReadingTable(readings);

        foreach (var (key, readings) in raw.GetValueOrDefault("RightBigrams") ?? [])
            if (TrySplitPair(key, out var form, out var right) && forms.TryGetValue(form, out var fp))
                (fp.Right ??= [])[right] = new ReadingTable(readings);

        foreach (var (key, readings) in raw.GetValueOrDefault("Left2Bigrams") ?? [])
            if (TrySplitPair(key, out var left, out var form) && forms.TryGetValue(form, out var fp))
                (fp.Left2 ??= [])[left] = new ReadingTable(readings);

        foreach (var (key, readings) in raw.GetValueOrDefault("Right2Bigrams") ?? [])
            if (TrySplitPair(key, out var form, out var right) && forms.TryGetValue(form, out var fp))
                (fp.Right2 ??= [])[right] = new ReadingTable(readings);

        foreach (var (key, readings) in raw.GetValueOrDefault("Trigrams") ?? [])
        {
            var parts = key.Split('\t', 3);
            if (parts.Length == 3 && forms.TryGetValue(parts[1], out var fp))
                (fp.Trigrams ??= [])[(parts[0], parts[2])] = new ReadingTable(readings);
        }

        return new RubyReadingPriors(forms);
    }

    private static bool TrySplitPair(string key, out string first, out string second)
    {
        var parts = key.Split('\t', 2);
        first = parts[0];
        second = parts.Length == 2 ? parts[1] : "";
        return parts.Length == 2;
    }
}
