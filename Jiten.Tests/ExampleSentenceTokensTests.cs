using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser;

namespace Jiten.Tests;

public class ExampleSentenceTokensTests
{
    [Fact]
    public void EncodeDecodeRoundTripsEveryField()
    {
        SentenceToken[] tokens =
        [
            new(1358280, 0, 0, 2, IsTarget: false, IsFunctionWord: false),
            new(2028930, 3, 2, 1, IsTarget: false, IsFunctionWord: true),
            new(9999999, 255, 200, 63, IsTarget: true, IsFunctionWord: true),
        ];

        var bytes = ExampleSentenceTokens.Encode(tokens);

        bytes.Should().HaveCount(1 + 6 * tokens.Length);
        ExampleSentenceTokens.Decode(bytes).Should().Equal(tokens);
    }

    [Fact]
    public void PartialRowsRoundTripAndAreFlagged()
    {
        SentenceToken[] tokens = [new(1358280, 0, 3, 2, IsTarget: true, IsFunctionWord: false)];

        var partial = ExampleSentenceTokens.Encode(tokens, partial: true);

        partial[0].Should().Be(ExampleSentenceTokens.FormatVersion | ExampleSentenceTokens.PartialFlag);
        ExampleSentenceTokens.IsPartial(partial).Should().BeTrue();
        ExampleSentenceTokens.IsPartial(ExampleSentenceTokens.Encode(tokens)).Should().BeFalse();
        ExampleSentenceTokens.Decode(partial).Should().Equal(tokens);
    }

    [Fact]
    public void ConversionHexLayoutDecodes()
    {
        // The migration builds partial rows in SQL as hex: header 81, then ReadingIndex, WordId low to high byte, Position, Length | 0x80
        var bytes = Convert.FromHexString("81" + "02" + "C8B814" + "05" + "82");

        ExampleSentenceTokens.Decode(bytes).Should().Equal(new SentenceToken(0x14B8C8, 2, 5, 2, IsTarget: true, IsFunctionWord: false));
    }

    [Fact]
    public void EmptyTokenListStillMarksTheRowAsParsed()
    {
        var bytes = ExampleSentenceTokens.Encode([]);

        bytes.Should().Equal(ExampleSentenceTokens.FormatVersion);
        ExampleSentenceTokens.Decode(bytes).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1358280, (byte)0)]
    [InlineData(9999999, (byte)255)]
    [InlineData(0, (byte)7)]
    public void WordKeyMatchesTheLongKeyTruncatedAndRoundTrips(int wordId, byte readingIndex)
    {
        var key = ExampleSentenceTokens.WordKey(wordId, readingIndex);

        key.Should().Be(unchecked((int)(((long)wordId << 8) | readingIndex)));
        ExampleSentenceTokens.FromWordKey(key).Should().Be((wordId, readingIndex));
    }

    [Fact]
    public void WordKeysAreSortedAndDistinct()
    {
        SentenceToken[] tokens =
        [
            new(9999999, 0, 0, 1, false, false),
            new(1000005, 1, 1, 1, false, false),
            new(1000005, 1, 4, 1, false, false),
            new(1000005, 0, 6, 1, true, false),
        ];

        var keys = ExampleSentenceTokens.WordKeys(tokens, fineBucket: 4095);

        keys.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems().And.HaveCount(5);
        keys.Where(ExampleSentenceTokens.IsBucketKey).Should().Equal(
            ExampleSentenceTokens.CoarseBucketKey(63), ExampleSentenceTokens.FineBucketKey(4095));
        ExampleSentenceTokens.FineBucketOf(keys).Should().Be(4095);
    }

    [Fact]
    public void BucketKeysNeverCollideWithWordKeys()
    {
        var smallestWordKey = ExampleSentenceTokens.WordKey(1_000_000, 0);

        ExampleSentenceTokens.IsBucketKey(smallestWordKey).Should().BeFalse();
        ExampleSentenceTokens.FineBucketKey(ExampleSentenceTokens.FineBucketCount - 1).Should().BeLessThan(smallestWordKey);
        ExampleSentenceTokens.IsBucketKey(ExampleSentenceTokens.WordKey(9999999, 255)).Should().BeFalse();
    }

    [Fact]
    public void FindFormPrefersThePickOverAnEarlierOccurrence()
    {
        SentenceToken[] tokens =
        [
            new(7, 0, 0, 1, IsTarget: false, IsFunctionWord: false),
            new(7, 0, 5, 1, IsTarget: true, IsFunctionWord: false),
            new(7, 1, 8, 1, IsTarget: false, IsFunctionWord: false),
        ];

        ExampleSentenceTokens.FindForm(tokens, 7, 0)!.Value.Position.Should().Be(5);
        ExampleSentenceTokens.FindForm(tokens, 7, 1)!.Value.Position.Should().Be(8);
        ExampleSentenceTokens.FindForm(tokens, 8, 0).Should().BeNull();
    }

    [Theory]
    [InlineData(ExampleSentenceTokens.MaxWordId + 1, 0, 1)]
    [InlineData(1, 256, 1)]
    [InlineData(1, 0, 64)]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0, 1)]
    public void OutOfRangeTokensAreRejected(int wordId, int position, int length)
    {
        ExampleSentenceTokens.CanEncode(wordId, position, length).Should().BeFalse();
    }

    [Fact]
    public void UnknownVersionFailsLoudly()
    {
        var act = () => ExampleSentenceTokens.Decode(new byte[] { 2, 0, 0, 0, 0, 0, 1 });

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("食べた", "食[た]べる", new[] { "0:1:た" })]
    [InlineData("引き出した", "引[ひ]き出[だ]す", new[] { "0:1:ひ", "2:1:だ" })]
    [InlineData("お茶", "お茶[ちゃ]", new[] { "1:1:ちゃ" })]
    [InlineData("今日", "今日[きょう]", new[] { "0:2:きょう" })]
    [InlineData("一ヶ月", "一ヶ月[いっかげつ]", new[] { "0:3:いっかげつ" })]
    [InlineData("読ンだ", "読[よ]む", new[] { "0:1:よ" })]
    public void AlignFollowsTheSurfaceUntilInflectionStarts(string surface, string ruby, string[] expected)
    {
        var groups = SentenceFurigana.Align(surface, ruby).Select(g => $"{g.Offset}:{g.Length}:{g.Reading}");

        groups.Should().Equal(expected);
    }

    [Theory]
    [InlineData("たべた", "食[た]べる")]
    [InlineData("喰べた", "食[た]べる")]
    [InlineData("食べた", "たべる")]
    [InlineData("食べた", "")]
    public void AlignGivesNothingWhenTheSurfaceIsNotThisSpelling(string surface, string ruby)
    {
        SentenceFurigana.Align(surface, ruby).Should().BeEmpty();
    }

    [Fact]
    public void BuildPlacesGroupsAtSentenceOffsetsAndFallsBackToSiblingSpellings()
    {
        const string text = "昨日は猫を食べた";
        SentenceToken[] tokens =
        [
            new(1, 0, 0, 2, false, false),
            new(2, 0, 2, 1, false, true),
            new(3, 0, 3, 1, true, false),
            new(4, 1, 5, 3, false, false),
        ];
        var forms = new Dictionary<(int, short), JmDictWordForm>
        {
            [(1, 0)] = Form(1, 0, "昨日", "昨日[きのう]", JmDictFormType.KanjiForm),
            [(2, 0)] = Form(2, 0, "は", "は", JmDictFormType.KanaForm),
            [(3, 0)] = Form(3, 0, "猫", "猫[ねこ]", JmDictFormType.KanjiForm),
            [(4, 0)] = Form(4, 0, "食べる", "食[た]べる", JmDictFormType.KanjiForm),
            [(4, 1)] = Form(4, 1, "たべる", "たべる", JmDictFormType.KanaForm),
        };

        var groups = SentenceFurigana.Build(text, tokens, forms);

        groups.Should().Equal(
            new FuriganaGroup(0, 2, "きのう", 1, 0, false),
            new FuriganaGroup(3, 1, "ねこ", 3, 0, true),
            new FuriganaGroup(5, 1, "た", 4, 1, false));
    }

    [Fact]
    public void BuildMergesTouchingKanjiOfOneWordButKeepsOkuriganaSplits()
    {
        const string text = "忘却を引き出した";
        SentenceToken[] tokens = [new(1, 0, 0, 2, false, false), new(2, 0, 3, 5, true, false)];
        var forms = new Dictionary<(int, short), JmDictWordForm>
        {
            [(1, 0)] = Form(1, 0, "忘却", "忘[ぼう]却[きゃく]", JmDictFormType.KanjiForm),
            [(2, 0)] = Form(2, 0, "引き出す", "引[ひ]き出[だ]す", JmDictFormType.KanjiForm),
        };

        var groups = SentenceFurigana.Build(text, tokens, forms);

        groups.Should().Equal(
            new FuriganaGroup(0, 2, "ぼうきゃく", 1, 0, false),
            new FuriganaGroup(3, 1, "ひ", 2, 0, true),
            new FuriganaGroup(5, 1, "だ", 2, 0, true));
    }

    [Fact]
    public void CountUnknownSkipsTheTargetWordAndFunctionWords()
    {
        SentenceToken[] tokens =
        [
            new(1, 0, 0, 2, false, false),
            new(2, 0, 2, 1, false, true),
            new(3, 0, 3, 1, true, false),
            new(3, 1, 4, 1, false, false),
            new(5, 0, 5, 1, false, false),
            new(5, 0, 7, 1, false, false),
            new(6, 0, 8, 1, false, false),
        ];
        var known = new HashSet<(int, byte)> { (1, 0) };

        var unknown = SentenceComprehension.CountUnknown(tokens, targetWordId: 3, (w, r) => known.Contains((w, r)));

        unknown.Should().Be(2);
    }

    [Fact]
    public void LearningAndNewStatesAreNotKnown()
    {
        SentenceComprehension.IsKnown([KnownState.New]).Should().BeFalse();
        SentenceComprehension.IsKnown([KnownState.Due]).Should().BeFalse();
        SentenceComprehension.IsKnown([KnownState.Suspended]).Should().BeFalse();
        SentenceComprehension.IsKnown([KnownState.Due, KnownState.Young]).Should().BeTrue();
        SentenceComprehension.IsKnown([KnownState.Redundant]).Should().BeTrue();
        SentenceComprehension.IsKnown([KnownState.Blacklisted]).Should().BeTrue();
    }

    [Fact]
    public void ExtractorStoresEveryKeptTokenAndFlagsThePick()
    {
        var sentence = new SentenceInfo("昨日は猫が大きな魚を静かに食べていた。");
        sentence.Words.AddRange(
        [
            Token("昨日", PartOfSpeech.Noun, 0, (1001, 0)),
            Token("は", PartOfSpeech.Particle, 2, (2001, 0)),
            Token("猫", PartOfSpeech.Noun, 3, (1002, 0)),
            Token("が", PartOfSpeech.Particle, 4, (2002, 0)),
            Token("大きな", PartOfSpeech.Adnominal, 5, null),
            Token("魚", PartOfSpeech.Noun, 8, (1003, 0)),
            Token("を", PartOfSpeech.Particle, 9, (2003, 0)),
            Token("静かに", PartOfSpeech.NaAdjective, 10, (1004, 1)),
            Token("食べていた", PartOfSpeech.Verb, 13, (1005, 0)),
            Token("。", PartOfSpeech.SupplementarySymbol, 18, null),
        ]);

        DeckWord[] deckWords =
        [
            Deck(1002, 0, "猫"),
            Deck(1001, 0, "きのう"), Deck(2001, 0, "は_"), Deck(2002, 0, "が_"), Deck(2003, 0, "を_"),
            Deck(1004, 1, "しずか"), Deck(1005, 0, "たべる"),
        ];

        var extracted = ExampleSentenceExtractor.ExtractSentences([sentence], deckWords, [], []);

        extracted.Should().ContainSingle();
        var example = extracted[0];
        ExampleSentenceTokens.IsPartial(example.Tokens).Should().BeFalse();

        ExampleSentenceTokens.Decode(example.Tokens).Should().Equal(
            new SentenceToken(1001, 0, 0, 2, false, false),
            new SentenceToken(2001, 0, 2, 1, false, true),
            new SentenceToken(1002, 0, 3, 1, true, false),
            new SentenceToken(2002, 0, 4, 1, false, true),
            new SentenceToken(2003, 0, 9, 1, false, true),
            new SentenceToken(1004, 1, 10, 3, false, false),
            new SentenceToken(1005, 0, 13, 5, false, false));

        var fineBucket = ExampleSentenceTokens.FineBucketOf(example.WordKeys);
        fineBucket.Should().NotBeNull();
        example.WordKeys.Should().Equal(ExampleSentenceTokens.WordKeys(ExampleSentenceTokens.Decode(example.Tokens), fineBucket!.Value));
    }

    private static (WordInfo, int, int) Token(string text, PartOfSpeech pos, int position, (int, byte)? keptForm) =>
        (new WordInfo { Text = text, PartOfSpeech = pos, KeptForm = keptForm }, position, text.Length);

    private static DeckWord Deck(int wordId, byte readingIndex, string originalText) =>
        new()
        {
            WordId = wordId, ReadingIndex = readingIndex, OriginalText = originalText,
            PartsOfSpeech = [PartOfSpeech.Noun], SudachiPartOfSpeech = PartOfSpeech.Noun
        };

    private static JmDictWordForm Form(int wordId, short readingIndex, string text, string ruby, JmDictFormType type) =>
        new() { WordId = wordId, ReadingIndex = readingIndex, Text = text, RubyText = ruby, FormType = type };
}
