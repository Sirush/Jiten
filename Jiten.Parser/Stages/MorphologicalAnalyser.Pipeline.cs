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

    private static TokenStage CandidateStage(
        TokenStageGroup group,
        Func<List<WordInfo>, IReadOnlyList<int>, List<WordInfo>> process,
        TokenFeatures candidateFeature) =>
        new(process.Method.Name, group, static input => input, candidateFeature, process);

    private IReadOnlyList<TokenStage> BuildTokenStages() =>
    [
        Stage(TokenStageGroup.Split, SplitOovGarbageTokens, TokenFeatures.OovGarbage),
        Stage(TokenStageGroup.Split, SplitCompoundAuxiliaryVerbs),
        Stage(TokenStageGroup.Split, SplitUnresolvableCompoundVerbs),
        Stage(TokenStageGroup.Split, SplitUnresolvableRenyokeiNounCompounds),
        Stage(TokenStageGroup.Split, SplitUnresolvablePrefixedAdjectives),
        Stage(TokenStageGroup.Split, SplitTatteParticle, TokenFeatures.TextTatte),
        Stage(TokenStageGroup.Split, SplitUnattestedToAdverbs, TokenFeatures.AdverbEndsTo),
        Stage(TokenStageGroup.Split, SplitTanSuffix, TokenFeatures.TextTanSuffix),
        Stage(TokenStageGroup.Split, SplitTawakeNoun, TokenFeatures.TextTawake),
        Stage(TokenStageGroup.Split, SplitLexicalisedKaratte, TokenFeatures.TextKaratte),
        Stage(TokenStageGroup.Split, SplitDoushiteContraction),
        Stage(TokenStageGroup.Split, SplitEmphaticMoSuru),

        Stage(TokenStageGroup.Repair, RepairHasaNoun, TokenFeatures.TextHasa),
        Stage(TokenStageGroup.Repair, RepairNTokenisation),
        Stage(TokenStageGroup.Repair, RepairVowelElongation),
        Stage(TokenStageGroup.Repair, ApplyTokenRewriteRulesEarly),
        Stage(TokenStageGroup.Repair, ProcessSpecialCases),
        Stage(TokenStageGroup.Repair, RetokeniseOovBlobs, TokenFeatures.HiraganaOovBlob),
        Stage(TokenStageGroup.Repair, RepairColloquialNegativeNee, TokenFeatures.Interjection),
        Stage(TokenStageGroup.Repair, RepairColloquialRanNai, TokenFeatures.TextRan),
        Stage(TokenStageGroup.Repair, RepairIntensifierKaeru, TokenFeatures.VerbKaeru),
        Stage(TokenStageGroup.Repair, RepairQuotativeTte, TokenFeatures.EndsWithTsu),
        CandidateStage(TokenStageGroup.Repair, RepairGeminateSuffixTheft, TokenFeatures.GeminateSuffixShape),
        CandidateStage(TokenStageGroup.Repair, RepairKatakanaShreds, TokenFeatures.KatakanaRun),
        CandidateStage(TokenStageGroup.Repair, RepairCompoundBoundaryTheft, TokenFeatures.CompoundBoundaryShape),
        CandidateStage(TokenStageGroup.Repair, RepairKanjiVerbShred, TokenFeatures.SingleKanjiNoun),

        Stage(TokenStageGroup.Repair, RecombineHiraganaTokens),
        Stage(TokenStageGroup.Repair, ApplyTokenRewriteRulesLate),
        Stage(TokenStageGroup.Repair, RepairSakkiMoraTheft, TokenFeatures.TextSakki),
        Stage(TokenStageGroup.Repair, CollapseReduplicatedMimetic, TokenFeatures.KanaRepetition),
        Stage(TokenStageGroup.Repair, RepairClippedAdjective, TokenFeatures.EndsWithTsu),
        Stage(TokenStageGroup.Repair, RepairClassicalKiAdjective),

        Stage(TokenStageGroup.Combine, CombinePrefixes, TokenFeatures.Prefix),
        Stage(TokenStageGroup.Combine, CombineInflections, TokenFeatures.InflectableBase),
        Stage(TokenStageGroup.Combine, CombineCompletionAuxVerb, TokenFeatures.DictKiru),
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
        Stage(TokenStageGroup.Combine, CombineQuotativeToIu),
        Stage(TokenStageGroup.Combine, CombineFinal),
        Stage(TokenStageGroup.Split, SplitUnresolvableSuruCompounds),
        Stage(TokenStageGroup.Repair, RepairTankaToTaNKa, TokenFeatures.TextTanka),
        Stage(TokenStageGroup.Repair, RepairTteNani),

        Stage(TokenStageGroup.Cleanup, ApplyTokenRewriteRulesCleanup),
        Stage(TokenStageGroup.Cleanup, RepairDanTobashi, TokenFeatures.TextTobashi),
        Stage(TokenStageGroup.Cleanup, FilterMisparse),
        Stage(TokenStageGroup.Disambiguation, FixReadingAmbiguity),
    ];

    private List<WordInfo> RunPipeline(List<WordInfo> wordInfos, ParserDiagnostics? diagnostics,
                                      BenchmarkTimings? timings = null)
    {
        _pipelineDeconjCache = new Dictionary<string, IReadOnlyList<DeconjugationForm>>(StringComparer.Ordinal);
        _pipelineDeconjCacheAlt = _pipelineDeconjCache.GetAlternateLookup<ReadOnlySpan<char>>();
        TokenFeatureScan? candidateScan = null;
        var features = TokenFeatureScanner.Scan(wordInfos);
        bool candidateScanDirty = true;
        Stopwatch? sw = timings != null ? Stopwatch.StartNew() : null;

        foreach (var stage in GetTokenStages())
        {
            if (stage.UsesCandidatePositions && candidateScanDirty)
            {
                candidateScan = TokenFeatureScanner.ScanWithCandidates(wordInfos);
                features = candidateScan.Features;
                candidateScanDirty = false;
            }

            if (stage.RequiredFeatures != TokenFeatures.None &&
                (features & stage.RequiredFeatures) == TokenFeatures.None)
            {
                diagnostics?.RecordSkippedStage(stage);
                continue;
            }

            sw?.Restart();
            var prev = wordInfos;
            wordInfos = TrackStage(stage, wordInfos, diagnostics, candidateScan);

            if (Environment.GetEnvironmentVariable("JITEN_STAGE_DEBUG") is { Length: > 0 })
                Console.WriteLine($"[stage] {stage.Name}: {string.Join("|", wordInfos.Select(w => w.Text))}");

            if (!ReferenceEquals(prev, wordInfos))
            {
                features = TokenFeatureScanner.Scan(wordInfos);
                candidateScanDirty = true;
            }
            else if (!stage.UsesCandidatePositions)
            {
                // Earlier stages may edit tokens in place. Refresh once, immediately before the
                // structural candidate block, instead of trusting positions collected before it.
                candidateScanDirty = true;
            }

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
        return TrackStage(stage, input, diagnostics, TokenFeatureScanner.ScanWithCandidates(input));
    }

    internal List<WordInfo> RunPipelineForTesting(List<WordInfo> input, ParserDiagnostics? diagnostics = null) =>
        RunPipeline(input, diagnostics);
}
