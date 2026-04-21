using System.Diagnostics;
using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser;
using Jiten.Parser.Data.Redis;
using Jiten.Parser.Resegmentation;
using Xunit;

namespace Jiten.Tests;

public class ResegmentationTests
{
    private static readonly IJmDictCache StubCache = new StubJmDictCache();

    // ─── BuildEdges ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildEdges_ReturnsLongestMatchFirst()
    {
        var lookups = Dict(("大学", [1]), ("大", [2]), ("学", [3]));
        var edges = ResegmentationScorer.BuildEdges("大学生", 0, lookups);

        edges.Should().NotBeEmpty();
        edges[0].Length.Should().Be(2, "longest match (大学) should come first");
        edges[0].WordIds.Should().Contain(1);
    }

    [Fact]
    public void BuildEdges_NormalizesToHiragana()
    {
        // Lookup keyed by hiragana, input is katakana
        var lookups = Dict(("がくせい", [99]));
        var edges = ResegmentationScorer.BuildEdges("ガクセイ", 0, lookups);

        edges.Should().ContainSingle(e => e.WordIds.Contains(99),
            "katakana input should be normalised to hiragana for lookup");
    }

    // ─── FindBestPath ─────────────────────────────────────────────────────────

    [Fact]
    public void FindBestPath_GreedySucceeds_TwoSegments()
    {
        // "大学" + "生" covers the span completely
        var lookups = Dict(("大学", [1]), ("生", [2]));
        var path = ResegmentationScorer.FindBestPath("大学生", lookups);

        path.Should().NotBeNull();
        path!.IsComplete("大学生".Length).Should().BeTrue();
        path.Segments.Should().HaveCount(2);
        path.Segments[0].Length.Should().Be(2);
        path.Segments[1].Length.Should().Be(1);
    }

    [Fact]
    public void FindBestPath_GreedyDeadEnd_DPFindsPath()
    {
        // Greedy from pos=0 would pick "あい" (len 2), then "う" is not found → dead end.
        // Non-greedy: "あ" (len 1) + "いう" (len 2) fully covers the span.
        var lookups = Dict(("あい", [1]), ("あ", [2]), ("いう", [3]));
        var path = ResegmentationScorer.FindBestPath("あいう", lookups);

        path.Should().NotBeNull();
        path!.IsComplete("あいう".Length).Should().BeTrue();
        // Both paths cover the span; DP must find at least one complete path
        path.Segments.Count.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void FindBestPath_NoValidDecomposition_ReturnsNull()
    {
        var lookups = new Dictionary<string, List<int>>();
        var path = ResegmentationScorer.FindBestPath("あいう", lookups);

        path.Should().BeNull();
    }

    [Fact]
    public void FindBestPath_PrefersFewerSegments()
    {
        // 3-segment path: "あ"(1)+"いう"(2)+"え"(3) vs 2-segment: "あい"(4)+"うえ"(5)
        var lookups = Dict(
            ("あ",   [1]),
            ("いう", [2]),
            ("え",   [3]),
            ("あい", [4]),
            ("うえ", [5]));

        var path = ResegmentationScorer.FindBestPath("あいうえ", lookups);

        path.Should().NotBeNull();
        path!.IsComplete("あいうえ".Length).Should().BeTrue();
        path.Segments.Should().HaveCount(2, "2-segment path should be preferred over 3-segment");
    }

    // ─── Gap penalty (lesson #4) ──────────────────────────────────────────────

    [Fact]
    public void SpanPath_GapCharsAndGapCost_DerivedFromEmptyWordIdSegments()
    {
        var path = new SpanPath([
            new SpanTokenCandidate(0, 2, [1]),
            new SpanTokenCandidate(2, 1, []),  // gap
            new SpanTokenCandidate(3, 2, [2]),
        ]);

        path.GapChars.Should().Be(1);
        path.GapCost.Should().Be(500, "gap cost is gapChars * UncoveredCharPenalty(=500)");
        path.Segments[1].IsGap.Should().BeTrue();
        path.Segments[0].IsGap.Should().BeFalse();
    }

    [Fact]
    public void FindBestPath_PrefersZeroGapPathWhenCompleteDictPathExists()
    {
        // Dictionary covers the whole span — no gap path should win over it.
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var path = ResegmentationScorer.FindBestPath("あいうえ", lookups, freqs);

        path.Should().NotBeNull();
        path!.IsComplete(4).Should().BeTrue();
        path.GapChars.Should().Be(0, "zero-gap dict path should always beat gap paths");
        path.Segments.Should().HaveCount(2);
    }

    [Fact]
    public void FindBestPath_ReturnsNullWhenOnlyAllGapPathsReachEnd()
    {
        // 2-char span, no lookups. All-gap path reaches the end but is filtered
        // because a path with zero content segments is equivalent to no path at all.
        var lookups = new Dictionary<string, List<int>>();
        var path = ResegmentationScorer.FindBestPath("あい", lookups);

        path.Should().BeNull("all-gap paths carry no information and are rejected");
    }

    [Fact]
    public void FindBestPath_CapsGapsAtMaxGapChars()
    {
        // 5-char span with empty lookups needs 5 gap chars to reach the end
        // but the per-path cap is 2, so no path reaches the final position → null.
        var lookups = new Dictionary<string, List<int>>();
        var path = ResegmentationScorer.FindBestPath("あいうえお", lookups);

        path.Should().BeNull("cap on uncovered chars must prevent degenerate all-gap paths");
    }

    [Fact]
    public void FindBestPath_AcceptsGapBetweenDictSegmentsWhenNoFullCoverageExists()
    {
        // "あい" (pos 0-1) and "えお" (pos 3-4) are in dict; "う" at pos 2 has no match.
        // Best path: dict-"あい" + gap("う") + dict-"えお" — 1 gap char.
        var lookups = Dict(("あい", [1]), ("えお", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var path = ResegmentationScorer.FindBestPath("あいうえお", lookups, freqs);

        path.Should().NotBeNull();
        path!.IsComplete(5).Should().BeTrue();
        path.GapChars.Should().Be(1);
        path.GapCost.Should().Be(500);
        path.Segments.Should().HaveCount(3);
        path.Segments[1].IsGap.Should().BeTrue();
        path.Segments[1].StartChar.Should().Be(2);
    }

    [Fact]
    public void ScorePath_SubtractsGapCostFromTotal()
    {
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var noGapPath = new SpanPath([
            new SpanTokenCandidate(0, 2, [1]),
            new SpanTokenCandidate(2, 2, [2]),
        ]);

        var gapPath = new SpanPath([
            new SpanTokenCandidate(0, 2, [1]),
            new SpanTokenCandidate(2, 1, []),          // gap
            new SpanTokenCandidate(3, 2, [2]),
        ]);

        int noGapScore = ResegmentationScorer.ScorePath(noGapPath, freqs);
        int gapScore   = ResegmentationScorer.ScorePath(gapPath, freqs);

        (noGapScore - gapScore).Should().Be(500,
            "equivalent path with one gap char must lose exactly UncoveredCharPenalty(=500)");
    }

    [Fact]
    public async Task ResegmentationEngine_DoesNotReplaceTokensViaGapPath()
    {
        // Only "あい" is in the dictionary; the rest of the span ("うえ") is unknown.
        // A gap path could reach the end but must never replace the user-visible token —
        // the empty-WordIds guard in ReplaceSpan prevents silent text loss.
        var lookups = Dict(("あい", [1]));
        var freqs = new Dictionary<int, int> { [1] = 100 };

        var word = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(1, "gap paths must never apply to user-visible tokens");
        sentence.Words[0].word.Text.Should().Be("あいうえ");
    }

    // ─── UncertaintyDetector ──────────────────────────────────────────────────

    [Fact]
    public void UncertaintyDetector_SkipsParticlesAndVerbs()
    {
        var lookups = new Dictionary<string, List<int>>();
        var sentence = MakeSentence(
            ("から", PartOfSpeech.Particle),
            ("食べる", PartOfSpeech.Verb),
            ("とても", PartOfSpeech.Auxiliary));

        var spans = UncertaintyDetector.FindSpans(sentence, lookups);

        spans.Should().BeEmpty("Particle, Verb and Auxiliary tokens must be skipped");
    }

    [Fact]
    public void UncertaintyDetector_SkipsPreMatchedAndLookupHits()
    {
        var lookups = Dict(("大学生", [42]));

        var preMatched = new WordInfo { Text = "学校生活", PartOfSpeech = PartOfSpeech.Noun, PreMatchedWordId = 99 };
        var inLookup   = new WordInfo { Text = "大学生",   PartOfSpeech = PartOfSpeech.Noun };
        var sentence   = new SentenceInfo("学校生活大学生");
        sentence.Words.Add((preMatched, 0, 4));
        sentence.Words.Add((inLookup,   4, 3));

        var spans = UncertaintyDetector.FindSpans(sentence, lookups);

        spans.Should().BeEmpty("pre-matched and lookup-hit tokens must be excluded");
    }

    // ─── ResegmentationEngine ─────────────────────────────────────────────────

    [Fact]
    public async Task ResegmentationEngine_ReplacesTokenWithHighFrequencyPath()
    {
        // WordIds 1 and 2 are high-frequency (rank ≤ 5000)
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word     = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(2);
        sentence.Words[0].word.Text.Should().Be("あい");
        sentence.Words[1].word.Text.Should().Be("うえ");
    }

    [Fact]
    public async Task ResegmentationEngine_RejectsLowFrequencyPath()
    {
        // WordIds have no frequency data → score too low
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int>();

        var word     = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(1, "path with no frequency data should be rejected");
    }

    [Fact]
    public async Task ResegmentationEngine_RejectsSingleCharHiraganaSegment()
    {
        // Path "あ" + "いう" — single-char hiragana segment should be rejected
        var lookups = Dict(("あ", [1]), ("いう", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word     = new WordInfo { Text = "あいう", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいう");
        sentence.Words.Add((word, 0, 3));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(1, "paths with single-char hiragana segments should be rejected");
    }

    [Fact]
    public async Task ResegmentationEngine_AllowsSingleCharKatakanaSegment()
    {
        // "だったら" + "おまえ" + "が" via katakana input — single-char katakana particle should be allowed
        // Lookups keyed by hiragana, input is katakana (BuildEdges converts katakana→hiragana for lookup)
        var lookups = Dict(("だったら", [1]), ("おまえ", [2]), ("が", [3]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200, [3] = 50 };

        var word     = new WordInfo { Text = "ダッタラオマエガ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("ダッタラオマエガ");
        sentence.Words.Add((word, 0, 8));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(3, "single-char katakana particle at end of all-katakana span should be allowed");
        sentence.Words[0].word.Text.Should().Be("ダッタラ");
        sentence.Words[1].word.Text.Should().Be("オマエ");
        sentence.Words[2].word.Text.Should().Be("ガ");
    }

    [Fact]
    public async Task ResegmentationEngine_AllowsSingleCharKanjiSegment()
    {
        // "大学" + "生" — single-char kanji is fine (生 is a common standalone word)
        var lookups = Dict(("大学", [1]), ("生", [2]));
        var freqs = new Dictionary<int, int> { [1] = 500, [2] = 300 };

        var word     = new WordInfo { Text = "大学生", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("大学生");
        sentence.Words.Add((word, 0, 3));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(2, "single-char kanji segments should be allowed");
        sentence.Words[0].word.Text.Should().Be("大学");
        sentence.Words[1].word.Text.Should().Be("生");
    }

    [Fact]
    public async Task ResegmentationEngine_RejectsTooManySegments()
    {
        // 4-char span split into 4 segments (count > length/2) should be rejected
        var lookups = Dict(("あ", [1]), ("い", [2]), ("う", [3]), ("え", [4]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200, [3] = 300, [4] = 400 };

        var word     = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(1, "too many segments relative to span length should be rejected");
    }

    // ─── ScorePath ──────────────────────────────────────────────────────────────

    [Fact]
    public void ScorePath_HighFrequencyTwoSegments_ScoresHigh()
    {
        // 2 segments, both ≥2 chars, both high frequency → should score well above threshold
        var path = new SpanPath([
            new SpanTokenCandidate(0, 2, [1]),
            new SpanTokenCandidate(2, 2, [2])
        ]);
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var score = ResegmentationScorer.ScorePath(path, freqs);

        score.Should().BeGreaterOrEqualTo(20, "two high-frequency segments should pass threshold");
    }

    [Fact]
    public void ScorePath_NoFrequencyData_ScoresLow()
    {
        // 2 segments, no frequency: -30 + 50 (coverage) = 20, below threshold of 25
        var path = new SpanPath([
            new SpanTokenCandidate(0, 2, [1]),
            new SpanTokenCandidate(2, 2, [2])
        ]);
        var freqs = new Dictionary<int, int>();

        var score = ResegmentationScorer.ScorePath(path, freqs);

        score.Should().BeLessThan(25, "path with no frequency data should fail threshold");
    }

    [Fact]
    public void ScorePath_ManySmallSegments_NoFrequency_ScoresVeryLow()
    {
        // 4 segments of 1 char each, no frequency: -60 + 0 = -60
        var path = new SpanPath([
            new SpanTokenCandidate(0, 1, [1]),
            new SpanTokenCandidate(1, 1, [2]),
            new SpanTokenCandidate(2, 1, [3]),
            new SpanTokenCandidate(3, 1, [4])
        ]);
        var freqs = new Dictionary<int, int>();

        var score = ResegmentationScorer.ScorePath(path, freqs);

        score.Should().BeLessThan(0, "many small segments with no frequency data should score negative");
    }

    // ─── ReplaceSpan field contracts ─────────────────────────────────────

    [Fact]
    public async Task ResegmentationEngine_ReplacedTokens_HavePreMatchedWordIdAndDictionaryFormEqualToText()
    {
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word     = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(2);
        sentence.Words[0].word.PreMatchedWordId.Should().Be(1, "best-frequency WordId for first segment must be pre-matched");
        sentence.Words[1].word.PreMatchedWordId.Should().Be(2, "best-frequency WordId for second segment must be pre-matched");
        foreach (var (w, _, _) in sentence.Words)
            w.DictionaryForm.Should().Be(w.Text, "DictionaryForm must equal the surface text for resegmented tokens");
    }

    // ─── Constrained candidate set (PreMatchedWordId) through adjacent-rescore ──

    [Fact]
    public async Task ResegmentationEngine_ResegmentedToken_PreMatchedIdPreservedOnSecondPass()
    {
        // First pass resegments "あいうえ" → "あい"(wid=1) + "うえ"(wid=2), setting PreMatchedWordId.
        // A second pass (simulating the adjacent-rescore phase) must not touch pre-matched tokens.
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word     = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);
        sentence.Words.Should().HaveCount(2);
        int wid0 = sentence.Words[0].word.PreMatchedWordId!.Value;
        int wid1 = sentence.Words[1].word.PreMatchedWordId!.Value;

        // Second pass — adjacent-rescore phase
        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(2, "second pass must not alter already-resegmented tokens");
        sentence.Words[0].word.PreMatchedWordId.Should().Be(wid0,
            "first token's constrained candidate set must be preserved through adjacent-rescore pass");
        sentence.Words[1].word.PreMatchedWordId.Should().Be(wid1,
            "second token's constrained candidate set must be preserved through adjacent-rescore pass");
    }

    [Fact]
    public async Task ConfidenceReseg_ResegmentedToken_CandidateSetConstrainedDuringAdjacentRescore()
    {
        // A resegmented token (PreMatchedWordId=1) has high-frequency split candidates:
        // "あ" (wid=99) + "い" (wid=88). Adjacent rescoring with a low margin must NOT
        // override the pre-matched constraint and re-segment the token.
        var lookups = Dict(("あ", [99]), ("い", [88]));   // "あい" absent from lookups
        var freqs = new Dictionary<int, int> { [99] = 1, [88] = 2 };

        var resegmented = new WordInfo { Text = "あい", PartOfSpeech = PartOfSpeech.Noun, PreMatchedWordId = 1 };
        var sentence = new SentenceInfo("あい");
        sentence.Words.Add((resegmented, 0, 2));

        var marginMap = new Dictionary<(int, int), int?> { [(0, 0)] = 5 };

        bool changed = await ResegmentationEngine.TryResegmentLowConfidenceTokens(
            [sentence], lookups, freqs, marginMap, StubCache);

        changed.Should().BeFalse("pre-matched token must not be re-segmented even when high-frequency alternatives exist");
        sentence.Words.Should().HaveCount(1, "constrained candidate set must remain inside the pre-matched word id");
        sentence.Words[0].word.PreMatchedWordId.Should().Be(1,
            "pre-matched word id must be unchanged through adjacent-rescore phase");
    }

    // ─── No-split safety ─────────────────────────────────────────────────

    [Fact]
    public async Task ResegmentationEngine_TokenFoundInLookups_IsNotResegmented()
    {
        // "あいうえ" is directly in lookups → UncertaintyDetector skips it → no split
        var lookups = Dict(("あいうえ", [99]), ("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200, [99] = 50 };

        var word     = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(1, "a token whose text is found in the lookups dict must not be resegmented");
    }

    [Fact]
    public async Task ResegmentationEngine_TokenWithDictionaryFormInLookups_IsNotResegmented()
    {
        // Text "あいうえ" is not in lookups, but DictionaryForm "あいうえる" is → skip
        var lookups = Dict(("あいうえる", [99]), ("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word     = new WordInfo { Text = "あいうえ", DictionaryForm = "あいうえる", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(1, "a token whose DictionaryForm is in the lookups dict must not be resegmented");
    }

    [Fact]
    public async Task ResegmentationEngine_AtMinAcceptScore_AcceptsPath()
    {
        // 2 segs: "あいう" (rank 25000 ≤ 30000 → +5) + "えお" (no freq → +0), both ≥ 2 chars (+50)
        // Score = -15*2 + 5 + 0 + 50 = 25 == MinAcceptScore → score < 25 is false → accepted
        var lookups = Dict(("あいう", [1]), ("えお", [2]));
        var freqs = new Dictionary<int, int> { [1] = 25000 }; // rank ≤ 30000

        var word     = new WordInfo { Text = "あいうえお", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえお");
        sentence.Words.Add((word, 0, 5));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, StubCache);

        sentence.Words.Should().HaveCount(2, "path scoring exactly at MinAcceptScore (25) must be accepted");
    }

    // ─── Performance guard ────────────────────────────────────────────────────

    [Fact]
    public void FindBestPath_LargeSpan_TerminatesQuickly()
    {
        // Dense 14-char span: every 1–3 char substring is in lookups
        var text    = "あいうえおかきくけこさしすせ";
        var lookups = new Dictionary<string, List<int>>();
        for (int start = 0; start < text.Length; start++)
            for (int len = 1; len <= 3 && start + len <= text.Length; len++)
                lookups[text.Substring(start, len)] = [start * 100 + len];

        var sw = Stopwatch.StartNew();
        ResegmentationScorer.FindBestPath(text, lookups);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "resegmentation of a 14-char span must not be exponential");
    }

    // ─── ConfidenceReseg ──────────────────────────────────────────────────────

    [Fact]
    public async Task ConfidenceReseg_LowMarginHighScoringPath_Resegments()
    {
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        var marginMap = new Dictionary<(int, int), int?> { [(0, 0)] = 5 };

        bool changed = await ResegmentationEngine.TryResegmentLowConfidenceTokens(
            [sentence], lookups, freqs, marginMap, StubCache);

        changed.Should().BeTrue();
        sentence.Words.Should().HaveCount(2);
        sentence.Words[0].word.Text.Should().Be("あい");
        sentence.Words[1].word.Text.Should().Be("うえ");
    }

    [Fact]
    public async Task ConfidenceReseg_LowMarginMediumScoringPath_DoesNotResegment()
    {
        // Score passes no-match threshold (25) but not confidence threshold (50)
        var lookups = Dict(("あいう", [1]), ("えお", [2]));
        var freqs = new Dictionary<int, int> { [1] = 25000 };

        var word = new WordInfo { Text = "あいうえお", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえお");
        sentence.Words.Add((word, 0, 5));

        var marginMap = new Dictionary<(int, int), int?> { [(0, 0)] = 5 };

        bool changed = await ResegmentationEngine.TryResegmentLowConfidenceTokens(
            [sentence], lookups, freqs, marginMap, StubCache);

        changed.Should().BeFalse("score between 25-49 should not pass confidence threshold");
        sentence.Words.Should().HaveCount(1);
    }

    [Fact]
    public async Task ConfidenceReseg_MarginAboveThreshold_Skipped()
    {
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        var marginMap = new Dictionary<(int, int), int?> { [(0, 0)] = 15 };

        bool changed = await ResegmentationEngine.TryResegmentLowConfidenceTokens(
            [sentence], lookups, freqs, marginMap, StubCache);

        changed.Should().BeFalse("margin >= 15 should not trigger resegmentation");
        sentence.Words.Should().HaveCount(1);
    }

    [Fact]
    public async Task ConfidenceReseg_PreMatchedWordId_Skipped()
    {
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun, PreMatchedWordId = 99 };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        var marginMap = new Dictionary<(int, int), int?> { [(0, 0)] = 5 };

        bool changed = await ResegmentationEngine.TryResegmentLowConfidenceTokens(
            [sentence], lookups, freqs, marginMap, StubCache);

        changed.Should().BeFalse("PreMatchedWordId tokens should be skipped");
        sentence.Words.Should().HaveCount(1);
    }

    [Fact]
    public async Task ConfidenceReseg_VerbPos_Skipped()
    {
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Verb };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        var marginMap = new Dictionary<(int, int), int?> { [(0, 0)] = 5 };

        bool changed = await ResegmentationEngine.TryResegmentLowConfidenceTokens(
            [sentence], lookups, freqs, marginMap, StubCache);

        changed.Should().BeFalse("Verb POS should be skipped");
        sentence.Words.Should().HaveCount(1);
    }

    [Fact]
    public async Task ConfidenceReseg_NullMargin_Skipped()
    {
        var lookups = Dict(("あい", [1]), ("うえ", [2]));
        var freqs = new Dictionary<int, int> { [1] = 100, [2] = 200 };

        var word = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var sentence = new SentenceInfo("あいうえ");
        sentence.Words.Add((word, 0, 4));

        var marginMap = new Dictionary<(int, int), int?> { [(0, 0)] = null };

        bool changed = await ResegmentationEngine.TryResegmentLowConfidenceTokens(
            [sentence], lookups, freqs, marginMap, StubCache);

        changed.Should().BeFalse("null margin (single candidate) should be skipped");
        sentence.Words.Should().HaveCount(1);
    }

    // ─── ScorePosTransitions ──────────────────────────────────────────────────

    [Fact]
    public void ScorePosTransitions_NounLikeLastBeforeParticle_GetsBonus()
    {
        var path = MakePath((0, 2, [1]), (2, 2, [2]));
        var pos  = new Dictionary<int, PartOfSpeech> { [1] = PartOfSpeech.Noun, [2] = PartOfSpeech.Noun };

        // lastPos=Noun + nextNeighbor=Particle → +20; allNounLike(2 segs) → +10
        ResegmentationScorer.ScorePosTransitions(path, pos, null, PartOfSpeech.Particle)
            .Should().Be(30);
    }

    [Fact]
    public void ScorePosTransitions_NumeralNeighborBeforeCounter_GetsBonus()
    {
        var path = MakePath((0, 3, [1]));
        var pos  = new Dictionary<int, PartOfSpeech> { [1] = PartOfSpeech.Counter };

        ResegmentationScorer.ScorePosTransitions(path, pos, PartOfSpeech.Numeral, null)
            .Should().Be(30);
    }

    [Fact]
    public void ScorePosTransitions_ParticleNeighborBeforeNonNounLike_GetsPenalty()
    {
        var path = MakePath((0, 2, [1]));
        var pos  = new Dictionary<int, PartOfSpeech> { [1] = PartOfSpeech.Verb };

        ResegmentationScorer.ScorePosTransitions(path, pos, PartOfSpeech.Particle, null)
            .Should().Be(-15);
    }

    [Fact]
    public void ScorePosTransitions_InternalParticleParticleTransition_GetsPenalty()
    {
        var path = MakePath((0, 2, [1]), (2, 2, [2]));
        var pos  = new Dictionary<int, PartOfSpeech> { [1] = PartOfSpeech.Particle, [2] = PartOfSpeech.Particle };

        ResegmentationScorer.ScorePosTransitions(path, pos, null, null)
            .Should().Be(-20);
    }

    [Fact]
    public void ScorePosTransitions_EmptyPosDict_ReturnsZero()
    {
        var path = MakePath((0, 2, [1]), (2, 2, [2]));
        var pos  = new Dictionary<int, PartOfSpeech>();

        ResegmentationScorer.ScorePosTransitions(path, pos, null, null)
            .Should().Be(0);
    }

    [Fact]
    public void ScorePosTransitions_FrequencyRankBreaksTie_PrefersHigherFreqWordPos()
    {
        // seg has two candidates: id=1(Particle, rank=1000) and id=2(Noun, rank=100)
        // Without freq ranking: first match in dict determines POS (arbitrary)
        // With freq ranking: id=2 (rank=100, more frequent) wins → POS=Noun
        var path = MakePath((0, 2, [1, 2]));
        var pos  = new Dictionary<int, PartOfSpeech> { [1] = PartOfSpeech.Particle, [2] = PartOfSpeech.Noun };
        var freq = new Dictionary<int, int> { [1] = 1000, [2] = 100 };

        // lastPos should resolve to Noun (id=2 wins) → IsNounLike → +20 with Particle neighbor
        ResegmentationScorer.ScorePosTransitions(path, pos, null, PartOfSpeech.Particle, freq)
            .Should().Be(20, "frequency-ranked POS resolution should pick the more frequent word (Noun) not the first in list (Particle)");
    }

    // ─── Engine: POS scoring with real cache data ─────────────────────────────

    [Fact]
    public async Task ResegmentationEngine_PosScoreSavesLowFreqPath_NounBeforeParticle()
    {
        // freqScore alone = -30 + 0 + 0 + 50 = 20 (below MinAcceptScore=25)
        // posScore = +20 (noun-like last before Particle) + +10 (allNounLike) = +30
        // combined = 50 ≥ 25 → accepted
        var lookups  = Dict(("あい", [1]), ("うえ", [2]));
        var freqs    = new Dictionary<int, int>();
        var cache    = new MockJmDictCache(new Dictionary<int, JmDictWord>
        {
            [1] = new JmDictWord { WordId = 1, PartsOfSpeech = ["n"] },
            [2] = new JmDictWord { WordId = 2, PartsOfSpeech = ["n"] },
        });

        var word     = new WordInfo { Text = "あいうえ", PartOfSpeech = PartOfSpeech.Noun };
        var particle = new WordInfo { Text = "が", PartOfSpeech = PartOfSpeech.Particle };
        var sentence = new SentenceInfo("あいうえが");
        sentence.Words.Add((word, 0, 4));
        sentence.Words.Add((particle, 4, 1));

        await ResegmentationEngine.TryImproveUncertainSpans([sentence], lookups, freqs, cache);

        sentence.Words.Should().HaveCount(3, "POS bonus from noun-before-particle should save a borderline-frequency path");
        sentence.Words[0].word.Text.Should().Be("あい");
        sentence.Words[1].word.Text.Should().Be("うえ");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static SpanPath MakePath(params (int start, int len, int[] ids)[] segments) =>
        new SpanPath(segments.Select(s => new SpanTokenCandidate(s.start, s.len, [..s.ids])).ToList());

    private static Dictionary<string, List<int>> Dict(params (string key, int[] ids)[] entries)
    {
        var d = new Dictionary<string, List<int>>();
        foreach (var (k, v) in entries)
            d[k] = new List<int>(v);
        return d;
    }

    private static SentenceInfo MakeSentence(params (string text, PartOfSpeech pos)[] words)
    {
        var combinedText = string.Concat(words.Select(w => w.text));
        var sentence = new SentenceInfo(combinedText);
        int pos = 0;
        foreach (var (text, p) in words)
        {
            sentence.Words.Add((new WordInfo { Text = text, PartOfSpeech = p }, pos, text.Length));
            pos += text.Length;
        }
        return sentence;
    }
}

file sealed class StubJmDictCache : IJmDictCache
{
    public Task<JmDictWord?> GetWordAsync(int wordId) => Task.FromResult<JmDictWord?>(null);
    public Task<Dictionary<int, JmDictWord>> GetWordsAsync(IEnumerable<int> wordIds)
        => Task.FromResult(new Dictionary<int, JmDictWord>());
    public Task<bool> SetWordAsync(int wordId, JmDictWord word) => Task.FromResult(true);
    public Task<bool> SetWordsAsync(Dictionary<int, JmDictWord> words) => Task.FromResult(true);
    public Task<bool> IsCacheInitializedAsync() => Task.FromResult(true);
    public Task SetCacheInitializedAsync() => Task.CompletedTask;
    public Task<Dictionary<int, List<string>>?> GetNonArchaicPosMapAsync()
        => Task.FromResult<Dictionary<int, List<string>>?>(null);
    public Task SetNonArchaicPosMapAsync(Dictionary<int, List<string>> map) => Task.CompletedTask;
    public Task PreloadWordsAsync(IEnumerable<int> wordIds) => Task.CompletedTask;
    public bool TryGetWordLocal(int wordId, out JmDictWord? word) { word = null; return false; }
    public JmDictWord?[]? GetWordArray() => null;
}

file sealed class MockJmDictCache(Dictionary<int, JmDictWord> words) : IJmDictCache
{
    public Task<JmDictWord?> GetWordAsync(int wordId)
        => Task.FromResult(words.GetValueOrDefault(wordId));
    public Task<Dictionary<int, JmDictWord>> GetWordsAsync(IEnumerable<int> wordIds)
        => Task.FromResult(wordIds.Where(words.ContainsKey).ToDictionary(id => id, id => words[id]));
    public Task<bool> SetWordAsync(int wordId, JmDictWord word) => Task.FromResult(true);
    public Task<bool> SetWordsAsync(Dictionary<int, JmDictWord> w) => Task.FromResult(true);
    public Task<bool> IsCacheInitializedAsync() => Task.FromResult(true);
    public Task SetCacheInitializedAsync() => Task.CompletedTask;
    public Task<Dictionary<int, List<string>>?> GetNonArchaicPosMapAsync()
        => Task.FromResult<Dictionary<int, List<string>>?>(null);
    public Task SetNonArchaicPosMapAsync(Dictionary<int, List<string>> map) => Task.CompletedTask;
    public Task PreloadWordsAsync(IEnumerable<int> wordIds) => Task.CompletedTask;
    public bool TryGetWordLocal(int wordId, out JmDictWord? word) { word = words.GetValueOrDefault(wordId); return word != null; }
    public JmDictWord?[]? GetWordArray() => null;
}
