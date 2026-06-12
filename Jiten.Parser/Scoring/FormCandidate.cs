using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using WanaKanaShaapu;

namespace Jiten.Parser.Scoring;

internal sealed class FormCandidate(
    JmDictWord word,
    JmDictWordForm form,
    byte readingIndex,
    string targetHiragana,
    DeconjugationForm? deconjForm = null)
{
    public JmDictWord Word { get; } = word;
    public JmDictWordForm Form { get; } = form;
    public byte ReadingIndex { get; } = readingIndex;
    public string TargetHiragana { get; } = targetHiragana;
    public DeconjugationForm? DeconjForm { get; } = deconjForm;
    public string FormTextHiragana { get; } = KanaScoringHelpers.ToNormalizedHiragana(form.Text, convertLongVowelMark: false);

    private HashSet<string>? _cachedReadingPos;
    public HashSet<string> CachedReadingPos => _cachedReadingPos ??= ReadingPosHelper.GetPosForReading(Word, ReadingIndex);

    /// Reading-restricted POS when available, else the word-level POS — the set callers should
    /// check for grammatical class. Mirrors the inline `CachedReadingPos.Count > 0 ? … : Word.PartsOfSpeech`.
    public IEnumerable<string> EffectivePos => CachedReadingPos.Count > 0 ? CachedReadingPos : Word.PartsOfSpeech;

    private string? _rubyReading;
    private bool _rubyReadingResolved;
    internal string? RubyReading
    {
        get
        {
            if (!_rubyReadingResolved)
            {
                _rubyReading = RubyReadingPriors.Current?.GetKanaReading(Word, ReadingIndex);
                _rubyReadingResolved = true;
            }
            return _rubyReading;
        }
    }

    public FormScoreTrace ScoreTrace { get; private set; }
    public bool IsPosIncompatibleDirectSurface { get; set; }

    public int TotalScore => ScoreTrace.TotalScore;
    public int WordScore => ScoreTrace.WordScore;
    public int EntryPriorityScore => ScoreTrace.EntryPriorityScore;
    public int FormPriorityScore => ScoreTrace.FormPriorityScore;
    public int FormFlagScore => ScoreTrace.FormFlagScore;
    public int SurfaceMatchScore => ScoreTrace.SurfaceMatchScore;
    public int ScriptScore => ScoreTrace.ScriptScore;
    public int ReadingMatchScore => ScoreTrace.ReadingMatchScore;
    public int PosAffinityScore => ScoreTrace.PosAffinityScore;
    public int RubyPriorsScore => ScoreTrace.RubyPriorsScore;

    public void SetScoreTrace(FormScoreTrace scoreTrace) => ScoreTrace = scoreTrace;
}

internal readonly record struct FormScoringContext(
    string Surface,
    string? DictionaryForm,
    string? NormalizedForm,
    bool IsNameContext,
    string? SudachiReading,
    bool IsKanaSurface,
    string SurfaceHiragana,
    string SurfaceHiraganaLoose,
    string? DictionaryFormHiragana,
    string? NormalizedFormHiragana,
    PartOfSpeech SudachiPOS = PartOfSpeech.Unknown,
    bool IsArchaicSentence = false,
    bool IsSentenceInitial = false,
    bool IsSentenceFinal = false,
    bool IsSudachiPossibleDependant = false)
{
    public static FormScoringContext Create(
        string surface,
        string? dictionaryForm,
        string? normalizedForm,
        bool isNameContext,
        string? sudachiReading,
        bool isArchaicSentence = false,
        bool isSentenceInitial = false,
        bool isSentenceFinal = false,
        PartOfSpeech sudachiPOS = PartOfSpeech.Unknown,
        bool isSudachiPossibleDependant = false)
    {
        var surfaceHiragana = KanaScoringHelpers.ToNormalizedHiragana(surface, convertLongVowelMark: false);
        var surfaceHiraganaLoose = KanaScoringHelpers.ToNormalizedHiragana(surface, convertLongVowelMark: true);
        var dictionaryFormHiragana = !string.IsNullOrEmpty(dictionaryForm)
            ? KanaScoringHelpers.ToNormalizedHiragana(dictionaryForm, convertLongVowelMark: false)
            : null;
        var normalizedFormHiragana = !string.IsNullOrEmpty(normalizedForm)
            ? KanaScoringHelpers.ToNormalizedHiragana(normalizedForm, convertLongVowelMark: false)
            : null;

        return new FormScoringContext(
            surface,
            dictionaryForm,
            normalizedForm,
            isNameContext,
            sudachiReading,
            WanaKana.IsKana(surface),
            surfaceHiragana,
            surfaceHiraganaLoose,
            dictionaryFormHiragana,
            normalizedFormHiragana,
            sudachiPOS,
            isArchaicSentence,
            isSentenceInitial,
            isSentenceFinal,
            isSudachiPossibleDependant);
    }
}

internal sealed record CandidateSelectionResult(
    FormCandidate? Best,
    int? MarginToSecond)
{
    public bool IsLowConfidence    => MarginToSecond.HasValue && MarginToSecond.Value < ScoringPolicy.LowConfidenceThreshold;
    public bool IsMediumConfidence => MarginToSecond.HasValue && MarginToSecond.Value is >= ScoringPolicy.LowConfidenceThreshold and < ScoringPolicy.HighConfidenceThreshold;
    public bool IsHighConfidence   => MarginToSecond.HasValue && MarginToSecond.Value >= ScoringPolicy.HighConfidenceThreshold;
}

internal readonly record struct FormScoreTrace(
    int WordScore,
    int EntryPriorityScore,
    int FormPriorityScore,
    int FormFlagScore,
    int SurfaceMatchScore,
    int ScriptScore,
    int ReadingMatchScore,
    int PosAffinityScore,
    bool IdentityPenaltyApplied,
    int RubyPriorsScore = 0)
{
    public int TotalScore =>
        WordScore + EntryPriorityScore + FormPriorityScore + FormFlagScore + SurfaceMatchScore + ScriptScore +
        ReadingMatchScore + PosAffinityScore + RubyPriorsScore;
}
