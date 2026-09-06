using FluentAssertions;
using Jiten.Core.Data.YouTube;
using Jiten.Core.YouTube;
using Xunit;

namespace Jiten.Tests;

public class YouTubeSubtitleCleanerTests
{
    private const string VttWithReadings = """
        WEBVTT
        Kind: captions
        Language: ja

        00:00:00.000 --> 00:00:08.060
        起きる、朝ご飯を食べる、歯を磨く、顔を洗う、服を着替える
        おきる、あさごはんをたべる、はをみがく、かおをあらう、ふくをきがえる

        00:00:08.060 --> 00:00:15.860
        皆さんは朝起きたら何をしますか
        みなさんは　あさおきたら　なにをしますか

        00:00:24.060 --> 00:00:27.310
        おはようございます

        00:00:27.310 --> 00:00:32.510
        <c.colorE5E5E5>朝起きたらすることを、日本語で紹介します</c>
        Let me introduce my morning routine in Japanese

        """;

    [Fact]
    public void DropsKanaReadingLines_KeepsKanjiLines()
    {
        var result = YouTubeSubtitleCleaner.Clean(VttWithReadings);

        result.Cues.Should().HaveCount(4);
        result.Cues[0].Lines.Should().Equal("起きる、朝ご飯を食べる、歯を磨く、顔を洗う、服を着替える");
        result.Cues[1].Lines.Should().Equal("皆さんは朝起きたら何をしますか");
        result.DroppedReadingLines.Should().Be(2);
    }

    [Fact]
    public void KeepsKanaOnlyCueThatIsSpeech()
    {
        var result = YouTubeSubtitleCleaner.Clean(VttWithReadings);

        result.Cues[2].Lines.Should().Equal("おはようございます");
    }

    [Fact]
    public void StripsTagsAndTranslationLines()
    {
        var result = YouTubeSubtitleCleaner.Clean(VttWithReadings);

        result.Cues[3].Lines.Should().Equal("朝起きたらすることを、日本語で紹介します");
        result.DroppedLatinLines.Should().Be(1);
    }

    [Fact]
    public void KanaSecondLineThatIsNotAReading_IsKept()
    {
        var result = YouTubeSubtitleCleaner.Clean("""
            1
            00:00:01,000 --> 00:00:03,000
            了解しました
            じゃあ行こうか

            """);

        result.Cues.Single().Lines.Should().Equal("了解しました", "じゃあ行こうか");
        result.DroppedReadingLines.Should().Be(0);
    }

    [Fact]
    public void ToSrt_RoundTripsTimestamps()
    {
        var result = YouTubeSubtitleCleaner.Clean(VttWithReadings);
        var srt = YouTubeSubtitleCleaner.ToSrt(result.Cues);

        srt.Should().StartWith("1\n00:00:00,000 --> 00:00:08,060\n起きる");
        YouTubeSubtitleCleaner.ParseCues(srt).Should().HaveCount(4);
    }

    [Fact]
    public void DensityGuard_RejectsSparseTracks()
    {
        var sparse = YouTubeSubtitleCleaner.Clean("""
            1
            00:00:01,000 --> 00:00:03,000
            タイトル

            """);

        YouTubeContentPolicy.CheckDensity(sparse, runtimeSeconds: 600).Should().StartWith("density:");

        var dense = YouTubeSubtitleCleaner.Clean(string.Concat(Enumerable.Range(0, 40).Select(i =>
            $"{i + 1}\n00:00:{i:00},000 --> 00:00:{i:00},900\n今日は天気がいいので散歩に行きました\n\n")));

        YouTubeContentPolicy.CheckDensity(dense, runtimeSeconds: 60).Should().BeNull();
    }

    [Fact]
    public void RuntimeBounds_SkipShortsAndMarathons()
    {
        var filters = new YouTubeSourceFilters(null, null, MinRuntimeSeconds: 600, MaxRuntimeSeconds: 7200);

        YouTubeContentPolicy.CheckRuntime(59, filters).Should().StartWith("runtime:");
        YouTubeContentPolicy.CheckRuntime(1800, filters).Should().BeNull();
        YouTubeContentPolicy.CheckRuntime(10000, filters).Should().StartWith("runtime:");
        YouTubeContentPolicy.CheckRuntime(null, filters).Should().BeNull();
        YouTubeContentPolicy.CheckRuntime(59, YouTubeSourceFilters.None).Should().BeNull();
    }

    [Theory]
    [InlineData("https://www.youtube.com/@nihongo-learning7582", YouTubeSourceKind.Channel, null)]
    [InlineData("https://www.youtube.com/channel/UC6Xtu6v_op552SsOr5_jWrg/videos", YouTubeSourceKind.Channel, "UC6Xtu6v_op552SsOr5_jWrg")]
    [InlineData("UC6Xtu6v_op552SsOr5_jWrg", YouTubeSourceKind.Channel, "UC6Xtu6v_op552SsOr5_jWrg")]
    [InlineData("https://www.youtube.com/playlist?list=PLxxxxxxxxxxxxxxxxxxxx", YouTubeSourceKind.Playlist, "PLxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PLxxxxxxxxxxxxxxxxxxxx", YouTubeSourceKind.Playlist, "PLxxxxxxxxxxxxxxxxxxxx")]
    public void UrlParser_ResolvesSources(string input, YouTubeSourceKind expectedKind, string? expectedId)
    {
        YouTubeUrlParser.TryParse(input, out var kind, out var listingUrl, out var knownId).Should().BeTrue();
        kind.Should().Be(expectedKind);
        knownId.Should().Be(expectedId);
        listingUrl.Should().StartWith("https://www.youtube.com/");
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=Jh2C7JlWGKU")]
    [InlineData("https://youtu.be/Jh2C7JlWGKU")]
    [InlineData("not a url")]
    public void UrlParser_RejectsNonSources(string input)
    {
        YouTubeUrlParser.TryParse(input, out _, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=Jh2C7JlWGKU", "Jh2C7JlWGKU")]
    [InlineData("https://youtu.be/Jh2C7JlWGKU?t=12", "Jh2C7JlWGKU")]
    [InlineData("https://www.youtube.com/shorts/Jh2C7JlWGKU", "Jh2C7JlWGKU")]
    public void UrlParser_ExtractsVideoIds(string input, string expected)
    {
        YouTubeUrlParser.TryParseVideoId(input, out var id).Should().BeTrue();
        id.Should().Be(expected);
    }
}
