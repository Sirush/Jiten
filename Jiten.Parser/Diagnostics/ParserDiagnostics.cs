using System.Text.Json.Serialization;
using System.Threading;
using Jiten.Core.Data;
using Jiten.Parser.Grammar;

namespace Jiten.Parser.Diagnostics;

/// <summary>
/// Root diagnostics container for parser analysis
/// </summary>
public class ParserDiagnostics
{
    public string InputText { get; set; } = string.Empty;
    public long TotalElapsedMs { get; set; }
    public SudachiDiagnostics? Sudachi { get; set; }
    public List<TokenProcessingStage> TokenStages { get; set; } = [];
    public List<WordResult> Results { get; set; } = [];
    public List<AdjacentScoringEntry> AdjacentScoring { get; set; } = [];
    public ParserRunSummary RunSummary { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TransitionViolationEntry>? TransitionViolations { get; private set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DroppedTokenEntry>? DroppedTokens { get; private set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResegmentationPathEntry>? ResegmentationPaths { get; private set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BeamSentenceAnalysis>? BeamSentences { get; private set; }

    internal void LogBeamSentence(BeamSentenceAnalysis analysis)
    {
        BeamSentences ??= [];
        BeamSentences.Add(analysis);
    }

    internal void LogDroppedToken(string text, PartOfSpeech pos, string reason)
    {
        DroppedTokens ??= [];
        DroppedTokens.Add(new DroppedTokenEntry(text, pos, reason));
    }

    internal void LogResegmentationPath(string spanText, int segmentCount, int gapChars,
                                        int gapCost, int score, bool accepted, string source)
    {
        ResegmentationPaths ??= [];
        ResegmentationPaths.Add(new ResegmentationPathEntry(
            spanText, segmentCount, gapChars, gapCost, score, accepted, source));
    }

    internal void LogTransitionViolation(string ruleId, in TokenWindow window)
    {
        TransitionViolations ??= [];
        TransitionViolations.Add(new TransitionViolationEntry(
            ruleId,
            window.Current.Text,
            window.Current.PartOfSpeech,
            window.Prev?.PartOfSpeech));
    }

    public IEnumerable<WordResult> GetLowConfidenceResults(int threshold = 15) =>
        Results.Where(r => r is not null && r.MarginToSecond.HasValue && r.MarginToSecond.Value < threshold);
}

public sealed record DroppedTokenEntry(
    string Text,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    PartOfSpeech PartOfSpeech,
    string Reason);

public sealed record TransitionViolationEntry(
    string RuleId,
    string TokenText,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    PartOfSpeech TokenPos,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    PartOfSpeech? PrevPos);

public sealed record ResegmentationPathEntry(
    string SpanText,
    int SegmentCount,
    int GapChars,
    int GapCost,
    int Score,
    bool Accepted,
    string Source);

public sealed record BeamSentenceAnalysis(
    string SentenceText,
    int SudachiBaselineScore,
    int BestBeamScore,
    int ThresholdUsed,
    bool BeamApplied,
    List<BeamPathEntry> TopPaths,
    BeamPathEntry? SudachiPath);

public sealed record BeamPathEntry(
    int Rank,
    int TotalScore,
    int NodeScoreSum,
    int AdjacencyBonusSum,
    int GapCost,
    int GapChars,
    List<BeamPathSegmentEntry> Segments);

public sealed record BeamPathSegmentEntry(
    int Start,
    int Len,
    string Surface,
    int? WordId,
    string? DictForm,
    int NodeScore,
    int FormTotal,
    int FreqBonus,
    int LengthBonus,
    int HintBonus,
    int KanaPenalty,
    int AdjacencyBonus,
    List<string>? ConjChain,
    bool IsGap);

/// <summary>
/// Lightweight counters for parse-run-level health and fallback behavior.
/// </summary>
public class ParserRunSummary
{
    private int _processSemaphoreTimeoutCount;
    private int _unresolvedTokenCount;

    public int ProcessSemaphoreTimeoutCount => _processSemaphoreTimeoutCount;
    public int UnresolvedTokenCount => _unresolvedTokenCount;

    public void IncrementProcessSemaphoreTimeoutCount() =>
        Interlocked.Increment(ref _processSemaphoreTimeoutCount);

    public void IncrementUnresolvedTokenCount() =>
        Interlocked.Increment(ref _unresolvedTokenCount);
}

/// <summary>
/// Diagnostics from Sudachi morphological analysis
/// </summary>
public class SudachiDiagnostics
{
    public double ElapsedMs { get; set; }
    public string RawOutput { get; set; } = string.Empty;
    public List<SudachiToken> Tokens { get; set; } = [];
}

/// <summary>
/// Individual token from Sudachi output
/// </summary>
public class SudachiToken
{
    public string Surface { get; set; } = string.Empty;
    public string PartOfSpeech { get; set; } = string.Empty;
    public string[] PosDetail { get; set; } = [];
    public string DictionaryForm { get; set; } = string.Empty;
    public string Reading { get; set; } = string.Empty;
    public string NormalizedForm { get; set; } = string.Empty;
}

/// <summary>
/// Records processing at a single stage of the token pipeline
/// </summary>
public class TokenProcessingStage
{
    public string StageName { get; set; } = string.Empty;
    public string StageGroup { get; set; } = string.Empty;
    public double ElapsedMs { get; set; }
    public int InputTokenCount { get; set; }
    public int OutputTokenCount { get; set; }
    public List<TokenModification> Modifications { get; set; } = [];
}

/// <summary>
/// Individual modification made during token processing
/// </summary>
public class TokenModification
{
    public string Type { get; set; } = string.Empty; // "merge", "split", "reclassify", "remove"
    public string[] InputTokens { get; set; } = [];
    public string? OutputToken { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Final word result after all processing
/// </summary>
public class WordResult
{
    public string Text { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PartOfSpeech PartOfSpeech { get; set; }

    public string? DictionaryForm { get; set; }
    public string? Reading { get; set; }
    public int? WordId { get; set; }
    public byte? ReadingIndex { get; set; }
    public List<FormCandidateDiagnostic> Candidates { get; set; } = [];
    public int? MarginToSecond { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfidenceLevel => MarginToSecond switch
    {
        null    => "single",
        >= 40   => "high",
        >= 15   => "medium",
        _       => "low"
    };
}

public class FormCandidateDiagnostic
{
    public int WordId { get; set; }
    public string FormText { get; set; } = string.Empty;
    public byte ReadingIndex { get; set; }
    public bool IsSelected { get; set; }
    public int TotalScore { get; set; }
    public int WordScore { get; set; }
    public int EntryPriorityScore { get; set; }
    public int FormPriorityScore { get; set; }
    public int FormFlagScore { get; set; }
    public int SurfaceMatchScore { get; set; }
    public int ScriptScore { get; set; }
    public int ReadingMatchScore { get; set; }
}

/// <summary>
/// Result from running the diagnostic test suite
/// </summary>
public class TestRunResult
{
    public int TotalTests { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public List<TestFailure> Failures { get; set; } = [];
}

/// <summary>
/// Individual test failure with full diagnostics
/// </summary>
public class TestFailure
{
    public string Input { get; set; } = string.Empty;
    public string[] Expected { get; set; } = [];
    public string[] Actual { get; set; } = [];
    public ParserDiagnostics? Diagnostics { get; set; }
    public FailureAnalysis? Analysis { get; set; }
}

/// <summary>
/// Analysis of why a test failed and suggested fix
/// </summary>
public class FailureAnalysis
{
    public string Type { get; set; } = string.Empty; // "OverSegmentation", "UnderSegmentation", "TokenMismatch"
    public string Description { get; set; } = string.Empty;
    public string? ProbableCause { get; set; }
    public string? SuggestedFix { get; set; }
}

public class FormTestRunResult
{
    public int TotalTests { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public List<FormTestFailure> Failures { get; set; } = [];
}

public class FormTestFailure
{
    public string Input { get; set; } = string.Empty;
    public string ExpectedToken { get; set; } = string.Empty;
    public int ExpectedWordId { get; set; }
    public byte ExpectedReadingIndex { get; set; }
    public int? ActualWordId { get; set; }
    public byte? ActualReadingIndex { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class AdjacentScoringEntry
{
    public int Position { get; set; }
    public string Surface { get; set; } = string.Empty;
    public AdjacentTokenInfo? LeftContext { get; set; }
    public AdjacentTokenInfo? RightContext { get; set; }
    public List<string> RulesMatched { get; set; } = [];
    public AdjacentCandidateInfo? FirstPassWinner { get; set; }
    public AdjacentCandidateInfo? AdjustedWinner { get; set; }
    public bool Changed { get; set; }
}

public class AdjacentTokenInfo
{
    public string Text { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PartOfSpeech Pos { get; set; }
}

public class AdjacentCandidateInfo
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public int Score { get; set; }
    public int ContextBonus { get; set; }
    public int AdjustedScore { get; set; }
}
