using System.Diagnostics;
using Jiten.Parser.Diagnostics;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    private IReadOnlyList<TokenStage>? _tokenStages;

    private IReadOnlyList<TokenStage> GetTokenStages() => _tokenStages ??= BuildTokenStages();

    private static TokenStage Stage(
        TokenStageGroup group,
        Func<List<WordInfo>, List<WordInfo>> process,
        TokenFeatures requires = TokenFeatures.None) =>
        new(process.Method.Name, group, process, requires);

    private IReadOnlyList<TokenStage> BuildTokenStages() =>
    [
        Stage(TokenStageGroup.Split, SplitOovGarbageTokens, TokenFeatures.OovGarbage),
        Stage(TokenStageGroup.Split, SplitCompoundAuxiliaryVerbs),
        Stage(TokenStageGroup.Split, SplitUnresolvableCompoundVerbs),
        Stage(TokenStageGroup.Split, SplitTatteParticle, TokenFeatures.TextTatte),
        Stage(TokenStageGroup.Split, SplitTanSuffix, TokenFeatures.TextTanSuffix),
        Stage(TokenStageGroup.Split, SplitTawakeNoun, TokenFeatures.TextTawake),

        Stage(TokenStageGroup.Repair, RepairHasaNoun, TokenFeatures.TextHasa),
        Stage(TokenStageGroup.Repair, RepairNTokenisation),
        Stage(TokenStageGroup.Repair, RepairVowelElongation),
        Stage(TokenStageGroup.Repair, ProcessSpecialCases),
        Stage(TokenStageGroup.Repair, RepairColloquialNegativeNee, TokenFeatures.Interjection),
        Stage(TokenStageGroup.Repair, RepairColloquialRanNai, TokenFeatures.TextRan),
        Stage(TokenStageGroup.Repair, RepairQuotativeTte, TokenFeatures.EndsWithTsu),

        Stage(TokenStageGroup.Repair, RecombineHiraganaTokens),

        Stage(TokenStageGroup.Combine, CombinePrefixes, TokenFeatures.Prefix),
        Stage(TokenStageGroup.Combine, CombineInflections, TokenFeatures.InflectableBase),
        Stage(TokenStageGroup.Combine, CombineAmounts, TokenFeatures.NumericAmount),
        Stage(TokenStageGroup.Combine, CombineTte, TokenFeatures.EndsWithTsu),
        Stage(TokenStageGroup.Combine, CombineAuxiliaryVerbStem, TokenFeatures.AuxVerbStem),
        Stage(TokenStageGroup.Combine, CombineSuffix, TokenFeatures.Suffix),
        Stage(TokenStageGroup.Cleanup, ReclassifyOrphanedSuffixes, TokenFeatures.Suffix),
        Stage(TokenStageGroup.Combine, CombineConjunctiveParticle, TokenFeatures.ConjParticle),
        Stage(TokenStageGroup.Combine, CombineAuxiliary),
        Stage(TokenStageGroup.Combine, CombineToNaru),
        Stage(TokenStageGroup.Repair, RepairFusedInterjectionParticle, TokenFeatures.Interjection),
        Stage(TokenStageGroup.Repair, RepairOrphanedAuxiliary),
        Stage(TokenStageGroup.Combine, CombineAdverbialParticle, TokenFeatures.AdvParticle),
        Stage(TokenStageGroup.Combine, CombineVerbDependant),
        Stage(TokenStageGroup.Combine, CombineParticles),
        Stage(TokenStageGroup.Combine, CombineFinal),
        Stage(TokenStageGroup.Repair, RepairTankaToTaNKa, TokenFeatures.TextTanka),

        Stage(TokenStageGroup.Cleanup, FilterMisparse),
        Stage(TokenStageGroup.Disambiguation, FixReadingAmbiguity),
    ];

    private List<WordInfo> RunPipeline(List<WordInfo> wordInfos, ParserDiagnostics? diagnostics,
                                      BenchmarkTimings? timings = null)
    {
        _pipelineDeconjCache = new Dictionary<string, IReadOnlyList<DeconjugationForm>>(StringComparer.Ordinal);
        _pipelineDeconjCacheAlt = _pipelineDeconjCache.GetAlternateLookup<ReadOnlySpan<char>>();
        var features = TokenFeatureScanner.Scan(wordInfos);
        Stopwatch? sw = timings != null ? Stopwatch.StartNew() : null;

        foreach (var stage in GetTokenStages())
        {
            if (stage.RequiredFeatures != TokenFeatures.None &&
                (features & stage.RequiredFeatures) == TokenFeatures.None)
            {
                diagnostics?.RecordSkippedStage(stage);
                continue;
            }

            sw?.Restart();
            var prev = wordInfos;
            wordInfos = TrackStage(stage, wordInfos, diagnostics);

            if (Environment.GetEnvironmentVariable("JITEN_STAGE_DEBUG") is { Length: > 0 })
                Console.WriteLine($"[stage] {stage.Name}: {string.Join("|", wordInfos.Select(w => w.Text))}");

            if (!ReferenceEquals(prev, wordInfos))
                features = TokenFeatureScanner.Scan(wordInfos);

            if (sw != null)
            {
                var elapsed = sw.Elapsed.TotalMilliseconds;
                timings!.PipelineStageMs.AddOrUpdate(stage.Name, elapsed, (_, existing) => existing + elapsed);
            }
        }

        _pipelineDeconjCache = null;
        return wordInfos;
    }

    internal IReadOnlyList<string> GetPipelineStageNamesForTesting() =>
        GetTokenStages().Select(s => s.Name).ToList();

    internal List<WordInfo> ApplyStageForTesting(string stageName, List<WordInfo> input, ParserDiagnostics? diagnostics = null)
    {
        var stage = GetTokenStages().First(s => s.Name == stageName);
        return TrackStage(stage, input, diagnostics);
    }

    internal List<WordInfo> RunPipelineForTesting(List<WordInfo> input, ParserDiagnostics? diagnostics = null) =>
        RunPipeline(input, diagnostics);
}
