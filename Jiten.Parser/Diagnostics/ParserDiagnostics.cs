using System.Text.Json.Serialization;
using System.Threading;
using Jiten.Core.Data;
using Jiten.Parser.Grammar;
using Jiten.Parser.Scoring;

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
    public List<ParserEventEntry>? ParserEvents { get; private set; }

    private readonly object _eventLock = new();

    internal void LogDroppedToken(string text, PartOfSpeech pos, string reason)
    {
        DroppedTokens ??= [];
        DroppedTokens.Add(new DroppedTokenEntry(text, pos, reason));
    }

    /// Records Parser-level structural mutations (resegmentation splits, misparse filters, etc.)
    /// that happen after the morphological token stages and are otherwise invisible in diagnostics.
    internal void LogParserEvent(string source, string type, string[] inputTokens, string[]? outputTokens, string reason)
    {
        lock (_eventLock)
        {
            ParserEvents ??= [];
            ParserEvents.Add(new ParserEventEntry(source, type, inputTokens, outputTokens, reason));
        }
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

    internal void RecordSkippedStage(TokenStage stage)
    {
        TokenStages.Add(new TokenProcessingStage
        {
            StageName = stage.Name,
            StageGroup = stage.Group.ToString(),
            ElapsedMs = 0,
            Skipped = true
        });
    }

    public IEnumerable<WordResult> GetLowConfidenceResults(int threshold = 15) =>
        Results.Where(r => r is not null && r.MarginToSecond.HasValue && r.MarginToSecond.Value < threshold);
}

public sealed record ParserEventEntry(
    string Source,
    string Type, // "split", "remove", "variant-resolved"
    string[] InputTokens,
    string[]? OutputTokens,
    string Reason);

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

    /// Lattice segmentation margin (extra cost of cheapest competing path crossing a token
    /// boundary; 99999 = no competitor). Null when margins were not requested/supported.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Margin { get; set; }
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Skipped { get; set; }
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
        null => "single",
        _ when ScoringPolicy.IsHighConfidence(MarginToSecond) => "high",
        _ when ScoringPolicy.IsLowConfidence(MarginToSecond)  => "low",
        _ => "medium"
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
    public int PosAffinityScore { get; set; }
    public int RubyPriorsScore { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RubyPriorSupport { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RubyPriorLevel { get; set; }
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RubyContextBonus { get; set; }
}
