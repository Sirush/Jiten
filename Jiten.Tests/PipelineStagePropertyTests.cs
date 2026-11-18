using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Parser;
using Jiten.Parser.Diagnostics;

namespace Jiten.Tests;

public class PipelineStagePropertyTests
{
    private static readonly PartOfSpeech[] RandomPosPool =
    [
        PartOfSpeech.Noun,
        PartOfSpeech.Verb,
        PartOfSpeech.IAdjective,
        PartOfSpeech.Adverb,
        PartOfSpeech.Particle,
        PartOfSpeech.Auxiliary,
        PartOfSpeech.Prefix,
        PartOfSpeech.Suffix,
        PartOfSpeech.Expression
    ];

    [Fact]
    public void Pipeline_ShouldBeDeterministic_ForRandomTokenStreams()
    {
        var analyser = new MorphologicalAnalyser();
        var random = new Random(1337);

        for (int sample = 0; sample < 150; sample++)
        {
            var input = GenerateRandomTokenStream(random, random.Next(1, 12));

            var first = analyser.RunPipelineForTesting(Clone(input));
            var second = analyser.RunPipelineForTesting(Clone(input));

            first.Select(t => t.Text).Should().Equal(second.Select(t => t.Text), $"sample #{sample} should be deterministic");
            first.Should().OnlyContain(t => t != null && !string.IsNullOrEmpty(t.Text));
        }
    }

    [Fact]
    public void Pipeline_ShouldMatchWholeDocumentRun_WhenSplitIntoBlocks()
    {
        var analyser = new MorphologicalAnalyser();
        var random = new Random(2026);
        var input = new List<WordInfo>();
        string[] functionWords = ["って", "た", "て", "だ", "ん", "の", "は", "も", "です", "ます", "ない", "さ", "っ", "する", "いる", "たん", "らん", "お", "ご"];
        string[] enders = ["。", "！", "？"];
        var posPool = Enum.GetValues<PartOfSpeech>();
        for (int sentence = 0; sentence < 20000; sentence++)
        {
            bool quoted = random.Next(3) == 0;
            if (quoted)
                input.Add(Token("「", PartOfSpeech.SupplementarySymbol));
            int length = random.Next(1, 12);
            for (int i = 0; i < length; i++)
            {
                var text = random.Next(3) == 0
                    ? functionWords[random.Next(functionWords.Length)]
                    : GenerateRandomSurface(random, random.Next(1, 4));
                var pos = random.Next(4) == 0 ? posPool[random.Next(posPool.Length)] : RandomPosPool[random.Next(RandomPosPool.Length)];
                input.Add(Token(text, pos));
            }
            input.Add(Token(enders[random.Next(enders.Length)], PartOfSpeech.SupplementarySymbol, section1: PartOfSpeechSection.FullStop));
            if (quoted)
                input.Add(Token("」", PartOfSpeech.SupplementarySymbol));
        }

        var blocked = analyser.RunPipelineForTesting(Clone(input));
        var whole = analyser.RunPipelineForTesting(Clone(input), new ParserDiagnostics());

        var blockedSentences = SplitAtEnders(blocked);
        var wholeSentences = SplitAtEnders(whole);
        var mismatches = new List<string>();
        for (int i = 0; i < Math.Min(blockedSentences.Count, wholeSentences.Count) && mismatches.Count < 5; i++)
        {
            if (blockedSentences[i] != wholeSentences[i])
                mismatches.Add($"blocked: {blockedSentences[i]} / whole: {wholeSentences[i]} / after: {(i > 0 ? wholeSentences[i - 1] : "")}");
        }

        mismatches.Should().BeEmpty();
        blockedSentences.Should().HaveSameCount(wholeSentences);
    }

    [Theory]
    [InlineData("。")]
    [InlineData("！")]
    [InlineData("、")]
    [InlineData("」")]
    public void Pipeline_ShouldNotMergeASuffixOntoPunctuation(string punctuation)
    {
        var analyser = new MorphologicalAnalyser();
        var input = new List<WordInfo>
        {
            Token("今日", PartOfSpeech.Noun),
            Token(punctuation, PartOfSpeech.SupplementarySymbol),
            Token("さ", PartOfSpeech.Suffix),
            Token("明日", PartOfSpeech.Noun)
        };

        var output = analyser.RunPipelineForTesting(input);

        output.Select(t => t.Text).Should().Contain(punctuation);
        output.Should().NotContain(t => t.Text.Length > 1 && t.Text.StartsWith(punctuation));
    }

    private static List<string> SplitAtEnders(List<WordInfo> tokens)
    {
        var sentences = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var t in tokens)
        {
            current.Append(t.Text).Append('[').Append(t.PartOfSpeech).Append(' ').Append(t.DictionaryForm).Append("] ");
            if (t.Text is "。" or "！" or "？")
            {
                sentences.Add(current.ToString());
                current.Clear();
            }
        }

        sentences.Add(current.ToString());
        return sentences;
    }

    [Fact]
    public void Pipeline_ShouldRecordAllStages_InDiagnostics()
    {
        var analyser = new MorphologicalAnalyser();
        var diagnostics = new ParserDiagnostics();
        var input = GenerateRandomTokenStream(new Random(42), 5);

        analyser.RunPipelineForTesting(input, diagnostics);

        diagnostics.TokenStages.Should().HaveCount(analyser.GetPipelineStageNamesForTesting().Count);
    }

    [Fact]
    public void StructuralRepairFeatures_ShouldRecognizeEachCandidateShape()
    {
        var features = TokenFeatureScanner.ScanWithCandidates(
        [
            Token("流しっ", PartOfSpeech.Noun),
            Token("ポン", PartOfSpeech.Noun),
            Token("路上", PartOfSpeech.Noun),
            Token("会", PartOfSpeech.CommonNoun)
        ]).Features;

        features.Should().HaveFlag(TokenFeatures.GeminateSuffixShape);
        features.Should().HaveFlag(TokenFeatures.KatakanaRun);
        features.Should().HaveFlag(TokenFeatures.CompoundBoundaryShape);
        features.Should().HaveFlag(TokenFeatures.SingleKanjiNoun);
    }

    [Fact]
    public void StructuralRepairStages_ShouldBeSkipped_WhenNoCandidateShapeExists()
    {
        var analyser = new MorphologicalAnalyser();
        var diagnostics = new ParserDiagnostics();

        analyser.RunPipelineForTesting([Token("ordinary", PartOfSpeech.Noun)], diagnostics);

        string[] stages =
        [
            "RepairGeminateSuffixTheft",
            "RepairKatakanaShreds",
            "RepairCompoundBoundaryTheft",
            "RepairKanjiVerbShred"
        ];
        diagnostics.TokenStages.Where(s => stages.Contains(s.StageName))
            .Should().OnlyContain(s => s.Skipped);
    }

    [Fact]
    public void Pipeline_ShouldPreservePunctuationBoundary_AsStandaloneToken()
    {
        var analyser = new MorphologicalAnalyser();
        var input = new List<WordInfo>
        {
            Token("今日", PartOfSpeech.Noun),
            Token("。", PartOfSpeech.SupplementarySymbol, section1: PartOfSpeechSection.FullStop),
            Token("明日", PartOfSpeech.Noun)
        };

        var output = analyser.RunPipelineForTesting(input);

        output.Select(t => t.Text).Should().Contain("。");
        output.Should().OnlyContain(t => t.Text == "。" || !t.Text.Contains('。'));
    }

    [Fact]
    public void Pipeline_ShouldPreserveRelativeOrder_ForStableUnchangedTokens()
    {
        var analyser = new MorphologicalAnalyser();
        var input = new List<WordInfo>
        {
            Token("甲", PartOfSpeech.Noun),
            Token("乙", PartOfSpeech.Noun),
            Token("丙", PartOfSpeech.Noun),
            Token("丁", PartOfSpeech.Noun)
        };

        var output = analyser.RunPipelineForTesting(input);

        var indices = new[] { "甲", "乙", "丙", "丁" }
            .Select(text => output.FindIndex(t => t.Text == text))
            .ToArray();

        indices.Should().OnlyContain(index => index >= 0);
        indices.Should().BeInAscendingOrder();
    }

    private static List<WordInfo> GenerateRandomTokenStream(Random random, int count)
    {
        var tokens = new List<WordInfo>(count);
        for (int i = 0; i < count; i++)
        {
            var text = GenerateRandomSurface(random, random.Next(1, 4));
            var partOfSpeech = RandomPosPool[random.Next(RandomPosPool.Length)];
            tokens.Add(Token(text, partOfSpeech));
        }

        return tokens;
    }

    private static string GenerateRandomSurface(Random random, int length)
    {
        const string chars = "あいうえおかきくけこさしすせそたちつてとなにぬねのまみむめも";
        var buffer = new char[length];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = chars[random.Next(chars.Length)];
        return new string(buffer);
    }

    private static List<WordInfo> Clone(List<WordInfo> source) =>
        source.Select(w => new WordInfo(w)).ToList();

    private static WordInfo Token(
        string text,
        PartOfSpeech partOfSpeech,
        string? dictionaryForm = null,
        string? normalizedForm = null,
        string? reading = null,
        PartOfSpeechSection section1 = PartOfSpeechSection.None,
        PartOfSpeechSection section2 = PartOfSpeechSection.None,
        PartOfSpeechSection section3 = PartOfSpeechSection.None) =>
        new()
        {
            Text = text,
            PartOfSpeech = partOfSpeech,
            DictionaryForm = dictionaryForm ?? text,
            NormalizedForm = normalizedForm ?? dictionaryForm ?? text,
            Reading = reading ?? text,
            PartOfSpeechSection1 = section1,
            PartOfSpeechSection2 = section2,
            PartOfSpeechSection3 = section3
        };
}
