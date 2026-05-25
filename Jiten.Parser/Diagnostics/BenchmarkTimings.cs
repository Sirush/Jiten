using System.Collections.Concurrent;

namespace Jiten.Parser.Diagnostics;

public class BenchmarkTimings
{
    public double TextPreprocessMs { get; set; }
    public double SudachiFFIMs { get; set; }
    public double TokenParsingMs { get; set; }
    public double OffsetRecoveryMs { get; set; }
    public double PipelineMs { get; set; }
    public double SentenceSplitMs { get; set; }

    public double PreprocessingMs { get; set; }
    public double PrepNounCompoundsMs { get; set; }
    public double PrepCompoundsMs { get; set; }
    public double PrepExpressionsMs { get; set; }
    public double PrepGrammarMs { get; set; }
    public double PrepResegmentationMs { get; set; }
    public double PrepOtherMs { get; set; }
    public double DeconjugationLookupMs { get; set; }
    public double ResegmentationMs { get; set; }
    public double AdjacentScoringMs { get; set; }
    public double StatsBuildMs { get; set; }

    public ConcurrentDictionary<string, double> PipelineStageMs { get; } = new();

    public double MorphologicalAnalysisMs => TextPreprocessMs + SudachiFFIMs + TokenParsingMs + OffsetRecoveryMs + PipelineMs + SentenceSplitMs;

    public double TotalMs => MorphologicalAnalysisMs + PreprocessingMs + DeconjugationLookupMs
                             + ResegmentationMs + AdjacentScoringMs + StatsBuildMs;

    public void Accumulate(BenchmarkTimings other)
    {
        TextPreprocessMs += other.TextPreprocessMs;
        SudachiFFIMs += other.SudachiFFIMs;
        TokenParsingMs += other.TokenParsingMs;
        OffsetRecoveryMs += other.OffsetRecoveryMs;
        PipelineMs += other.PipelineMs;
        SentenceSplitMs += other.SentenceSplitMs;
        PreprocessingMs += other.PreprocessingMs;
        PrepNounCompoundsMs += other.PrepNounCompoundsMs;
        PrepCompoundsMs += other.PrepCompoundsMs;
        PrepExpressionsMs += other.PrepExpressionsMs;
        PrepGrammarMs += other.PrepGrammarMs;
        PrepResegmentationMs += other.PrepResegmentationMs;
        PrepOtherMs += other.PrepOtherMs;
        DeconjugationLookupMs += other.DeconjugationLookupMs;
        ResegmentationMs += other.ResegmentationMs;
        AdjacentScoringMs += other.AdjacentScoringMs;
        StatsBuildMs += other.StatsBuildMs;

        foreach (var (stage, ms) in other.PipelineStageMs)
            PipelineStageMs.AddOrUpdate(stage, ms, (_, existing) => existing + ms);
    }
}
