using System.Diagnostics;
using Jiten.Parser.Diagnostics;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    internal static readonly bool StageDebug = Environment.GetEnvironmentVariable("JITEN_STAGE_DEBUG") is { Length: > 0 };

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
        Stage(TokenStageGroup.Repair, RepairQuotativeTte, TokenFeatures.EndsWithTsu),
        Stage(TokenStageGroup.Repair, RepairNounReadAsTeForm, TokenFeatures.EndsWithTsu),
        Stage(TokenStageGroup.Repair, RepairStolenUBeforeSsu),
        Stage(TokenStageGroup.Repair, RepairInterjectionStemShred, TokenFeatures.Interjection),
        Stage(TokenStageGroup.Repair, RepairNounTsukuCompound),
        Stage(TokenStageGroup.Repair, RecombinePartialKanaSpellings),
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
        Stage(TokenStageGroup.Cleanup, ApplyContextPins),
        Stage(TokenStageGroup.Disambiguation, ApplyTokenRewriteRulesReading),
        Stage(TokenStageGroup.Disambiguation, FixReadingAmbiguity),
    ];

    private const int PipelineBlockMinTokens = 256;

    /// <summary>Diagnostics record one entry per stage, so a diagnostics run is never split into blocks.</summary>
    private List<WordInfo> RunPipeline(List<WordInfo> wordInfos, ParserDiagnostics? diagnostics,
                                      BenchmarkTimings? timings = null)
    {
        _pipelineDeconjCache = new Dictionary<string, IReadOnlyList<DeconjugationForm>>(StringComparer.Ordinal);
        _pipelineDeconjCacheAlt = _pipelineDeconjCache.GetAlternateLookup<ReadOnlySpan<char>>();

        List<WordInfo> result;
        if (diagnostics != null || wordInfos.Count <= PipelineBlockMinTokens)
        {
            result = RunStages(wordInfos, diagnostics, timings);
        }
        else
        {
            result = new List<WordInfo>(wordInfos.Count);
            WordInfo? precedingEnder = null;
            for (int start = 0; start < wordInfos.Count;)
            {
                int end = FindBlockEnd(wordInfos, start);
                var block = new List<WordInfo>(end - start + 1);
                if (precedingEnder != null)
                    block.Add(precedingEnder);
                for (int i = start; i < end; i++)
                    block.Add(wordInfos[i]);

                var pristineEnder = end < wordInfos.Count ? new WordInfo(wordInfos[end - 1]) : null;
                AppendBlock(result, RunStages(block, null, timings), precedingEnder);
                precedingEnder = pristineEnder;
                start = end;
            }
        }

        _pipelineDeconjCache = null;
        return result;
    }

    /// <summary>A block runs behind a copy of the ender before it, so its first word keeps the left neighbour it has in a whole-document run.</summary>
    private static void AppendBlock(List<WordInfo> result, List<WordInfo> block, WordInfo? precedingEnder)
    {
        int skip = 0;
        if (precedingEnder != null)
        {
            if (block.Count > 0 && block[0].Text == precedingEnder.Text)
                skip = 1;
            else if (result.Count > 0 && result[^1].Text == precedingEnder.Text)
                result.RemoveAt(result.Count - 1);
        }

        for (int i = skip; i < block.Count; i++)
            result.Add(block[i]);
    }

    /// <summary>Blocks end only after 。！？ followed by a sentence start; reading context still crosses that point, so the cut must stay rare.</summary>
    private static int FindBlockEnd(List<WordInfo> tokens, int start)
    {
        int end = start + PipelineBlockMinTokens;
        while (end < tokens.Count && !(IsSentenceEnder(tokens[end - 1].Text) && StartsSentence(tokens[end].Text)))
            end++;
        return Math.Min(end, tokens.Count);
    }

    private static bool IsSentenceEnder(string text) => text is "。" or "！" or "？";

    private static bool StartsSentence(string text) =>
        text.Length > 0 && !IsSentenceEnder(text) && text is not ("」" or "』" or "）" or "〉" or "》");

    private List<WordInfo> RunStages(List<WordInfo> wordInfos, ParserDiagnostics? diagnostics, BenchmarkTimings? timings)
    {
        TokenFeatureScan? candidateScan = null;
        var features = TokenFeatureScanner.Scan(wordInfos);
        bool featuresStale = false;
        bool candidateScanDirty = true;
        Stopwatch? sw = timings != null ? Stopwatch.StartNew() : null;

        foreach (var stage in GetTokenStages())
        {
            if (stage.UsesCandidatePositions && candidateScanDirty)
            {
                candidateScan = TokenFeatureScanner.ScanWithCandidates(wordInfos);
                features = candidateScan.Features;
                candidateScanDirty = false;
                featuresStale = false;
            }

            if (stage.RequiredFeatures != TokenFeatures.None &&
                (features & stage.RequiredFeatures) == TokenFeatures.None)
            {
                // A stale set bit only costs a no-op run, but a skip must never trust a stale clear bit.
                if (featuresStale)
                {
                    features |= TokenFeatureScanner.ScanMissing(wordInfos, features);
                    featuresStale = false;
                    Debug.Assert((TokenFeatureScanner.Scan(wordInfos) & ~features) == TokenFeatures.None);
                }

                if ((features & stage.RequiredFeatures) == TokenFeatures.None)
                {
                    diagnostics?.RecordSkippedStage(stage);
                    continue;
                }
            }

            sw?.Restart();
            var prev = wordInfos;
            wordInfos = TrackStage(stage, wordInfos, diagnostics, candidateScan);

            if (StageDebug)
                Console.WriteLine($"[stage] {stage.Name}: {string.Join("|", wordInfos.Select(w => w.Text))}");

            if (!ReferenceEquals(prev, wordInfos))
            {
                featuresStale = true;
                candidateScanDirty = true;
            }
            else if (!stage.UsesCandidatePositions)
            {
                // In-place edits leave the list reference unchanged, so candidate positions must still be rescanned.
                candidateScanDirty = true;
            }

            if (sw != null)
            {
                var elapsed = sw.Elapsed.TotalMilliseconds;
                timings!.PipelineStageMs.AddOrUpdate(stage.Name, elapsed, (_, existing) => existing + elapsed);
            }
        }

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
