using System.Collections.Concurrent;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Scoring;

internal static class RubyPriorsScorer
{
    // No-context ScoreDetailed result depends only on (WordId, RubyReading, Surface, IsKanaSurface) —
    // SudachiPOS enters only via the IsContentWord guard (cached entries are always content words).
    // 2-generation bounded cache (mirrors KanaConverter), ~2x MaxGen0Entries resident.
    private const int MaxGen0Entries = 50_000;
    private static volatile ConcurrentDictionary<(int, string, string, bool), RubyScoreResult> _gen0 = new();
    private static volatile ConcurrentDictionary<(int, string, string, bool), RubyScoreResult>? _gen1;
    private static int _gen0Count;
    private static int _rotating;

    private static void RotateGenerations()
    {
        if (Interlocked.CompareExchange(ref _rotating, 1, 0) != 0)
            return;
        try
        {
            _gen1 = _gen0;
            _gen0 = new ConcurrentDictionary<(int, string, string, bool), RubyScoreResult>();
            Interlocked.Exchange(ref _gen0Count, 0);
        }
        finally
        {
            Interlocked.Exchange(ref _rotating, 0);
        }
    }

    private static bool IsContentWord(PartOfSpeech pos) =>
        pos is not (PartOfSpeech.Particle or PartOfSpeech.Auxiliary or PartOfSpeech.Conjunction
            or PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace
            or PartOfSpeech.Filler);

    private static bool IsAllKatakana(string text)
    {
        foreach (var c in text)
            if (c is not (>= 'ァ' and <= 'ヶ' or 'ー'))
                return false;
        return text.Length > 0;
    }

    private static bool WordHasMatchingForm(JmDictWord word, string surface)
    {
        foreach (var form in word.Forms)
            if (form.Text == surface)
                return true;
        return false;
    }

    private static bool ShouldUseKanaReverse(FormCandidate candidate, FormScoringContext context)
    {
        if (!context.IsKanaSurface) return false;
        if (!IsAllKatakana(context.Surface)) return true;
        return WordHasMatchingForm(candidate.Word, context.Surface);
    }

    public static int Score(FormCandidate candidate, FormScoringContext context)
        => ScoreDetailed(candidate, context).Score;

    public static RubyScoreResult ScoreDetailed(FormCandidate candidate, FormScoringContext context)
    {
        var priors = RubyReadingPriors.Current;
        if (priors == null) return default;
        if (!IsContentWord(context.SudachiPOS)) return default;

        var reading = candidate.RubyReading;
        if (reading == null) return default;

        var key = (candidate.Word.WordId, reading, context.Surface, context.IsKanaSurface);
        if (_gen0.TryGetValue(key, out var cached))
            return cached;
        var gen1 = _gen1;
        if (gen1 != null && gen1.TryGetValue(key, out cached))
        {
            _gen0.TryAdd(key, cached);
            return cached;
        }

        var result = ComputeNoContext(priors, candidate, context, reading);

        if (_gen0.TryAdd(key, result))
            if (Interlocked.Increment(ref _gen0Count) > MaxGen0Entries)
                RotateGenerations();

        return result;
    }

    private static RubyScoreResult ComputeNoContext(
        RubyReadingPriors priors, FormCandidate candidate, FormScoringContext context, string reading)
    {
        if (ShouldUseKanaReverse(candidate, context))
            return priors.ScoreKanaReverseDetailed(reading, candidate.Word, null, null);

        if (!context.IsKanaSurface)
        {
            var kanjiForm = priors.GetKanjiForm(candidate.Word, context.Surface);
            if (kanjiForm != null)
                return priors.ScoreCandidateDetailed(kanjiForm, reading, null, null);
        }

        return default;
    }

    public static int ScoreWithContext(FormCandidate candidate, FormScoringContext context,
        string? leftDictForm, string? rightDictForm,
        string? left2DictForm = null, string? right2DictForm = null)
        => ScoreWithContextDetailed(candidate, context, leftDictForm, rightDictForm, left2DictForm, right2DictForm).Score;

    public static RubyScoreResult ScoreWithContextDetailed(FormCandidate candidate, FormScoringContext context,
        string? leftDictForm, string? rightDictForm,
        string? left2DictForm = null, string? right2DictForm = null)
    {
        var priors = RubyReadingPriors.Current;
        if (priors == null) return default;

        var reading = candidate.RubyReading;
        if (reading == null) return default;

        if (ShouldUseKanaReverse(candidate, context))
            return priors.ScoreKanaReverseDetailed(reading, candidate.Word,
                leftDictForm, rightDictForm, left2DictForm, right2DictForm);

        if (!context.IsKanaSurface)
        {
            var kanjiForm = priors.GetKanjiForm(candidate.Word, context.Surface);
            if (kanjiForm != null)
                return priors.ScoreCandidateDetailed(kanjiForm, reading,
                    leftDictForm, rightDictForm, left2DictForm, right2DictForm);
        }

        return default;
    }
}
