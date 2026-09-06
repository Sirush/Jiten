using FluentAssertions;
using Jiten.Core.Data.YouTube;
using Jiten.Core.YouTube;

namespace Jiten.Tests;

public class YouTubeSyncTests
{
    private static YouTubeVideo NoSubs(DateTimeOffset uploadedAt, DateTimeOffset lastChecked) => new()
    {
        VideoId = "abc",
        Status = YouTubeVideoStatus.NoManualSubs,
        UploadedAt = uploadedAt,
        LastCheckedAt = lastChecked
    };

    [Fact]
    public void Recheck_YoungVideo_AfterInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var video = NoSubs(now.AddDays(-30), now.AddDays(-8));

        YouTubeSchedule.ShouldRecheck(video, now).Should().BeTrue();
    }

    [Fact]
    public void Recheck_YoungVideo_CheckedRecently_Waits()
    {
        var now = DateTimeOffset.UtcNow;
        var video = NoSubs(now.AddDays(-30), now.AddDays(-2));

        YouTubeSchedule.ShouldRecheck(video, now).Should().BeFalse();
    }

    [Fact]
    public void Recheck_OldVideo_Settles()
    {
        var now = DateTimeOffset.UtcNow;
        var video = NoSubs(now.AddDays(-120), now.AddDays(-60));

        YouTubeSchedule.ShouldRecheck(video, now).Should().BeFalse();
    }

    [Fact]
    public void Recheck_OnlyAppliesToNoManualSubs()
    {
        var now = DateTimeOffset.UtcNow;
        var video = NoSubs(now.AddDays(-30), now.AddDays(-8));
        video.Status = YouTubeVideoStatus.FilteredOut;

        YouTubeSchedule.ShouldRecheck(video, now).Should().BeFalse();
    }

    [Fact]
    public void NextCheck_QuietSource_Monthly()
    {
        var active = YouTubeSchedule.NextCheck(DateTimeOffset.UtcNow.AddDays(-3));
        var quiet = YouTubeSchedule.NextCheck(DateTimeOffset.UtcNow.AddDays(-200));

        (active - DateTimeOffset.UtcNow).TotalDays.Should().BeApproximately(7, 0.1);
        (quiet - DateTimeOffset.UtcNow).TotalDays.Should().BeApproximately(30, 0.1);
    }

    [Fact]
    public void FailureBackoff_CapsAtAMonth()
    {
        var first = YouTubeSchedule.NextCheckAfterFailure(1);
        var tenth = YouTubeSchedule.NextCheckAfterFailure(10);

        (first - DateTimeOffset.UtcNow).TotalDays.Should().BeApproximately(1, 0.1);
        (tenth - DateTimeOffset.UtcNow).TotalDays.Should().BeApproximately(30, 0.1);
    }

    [Fact]
    public async Task FeedReader_ParsesAtomEntries()
    {
        const string feed = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns="http://www.w3.org/2005/Atom">
              <title>Nihongo-Learning</title>
              <entry>
                <id>yt:video:Jh2C7JlWGKU</id>
                <yt:videoId>Jh2C7JlWGKU</yt:videoId>
                <title>Comprehensible Japanese Beginner - My Morning Routine</title>
                <published>2026-01-09T16:01:43+00:00</published>
              </entry>
              <entry>
                <id>yt:video:ooQNETO4dK0</id>
                <yt:videoId>ooQNETO4dK0</yt:videoId>
                <title>Level Up Your Japanese Chat Skills!</title>
                <published>2026-01-10T16:09:34+00:00</published>
              </entry>
            </feed>
            """;

        var handler = new StubHandler(feed);
        var reader = new YouTubeFeedReader(new HttpClient(handler));

        var entries = await reader.ReadAsync(YouTubeSourceKind.Channel, "UC6Xtu6v_op552SsOr5_jWrg");

        handler.RequestedUrl.Should().Be("https://www.youtube.com/feeds/videos.xml?channel_id=UC6Xtu6v_op552SsOr5_jWrg");
        entries.Should().HaveCount(2);
        entries[1].VideoId.Should().Be("ooQNETO4dK0");
        entries[1].Published.Should().Be(new DateTimeOffset(2026, 1, 10, 16, 9, 34, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("https://i.ytimg.com/vi/Jh2C7JlWGKU/maxresdefault.jpg", true)]
    [InlineData("https://yt3.googleusercontent.com/abc=s0", true)]
    [InlineData("https://yt3.ggpht.com/abc", true)]
    [InlineData("http://i.ytimg.com/vi/x/hqdefault.jpg", false)]
    [InlineData("https://ytimg.com.evil.example/x.jpg", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData(null, false)]
    public void ImageDownloads_OnlyFromYouTubeHosts(string? url, bool allowed)
    {
        YtDlpClient.IsYouTubeImageUrl(url).Should().Be(allowed);
    }

    private class StubHandler(string body) : HttpMessageHandler
    {
        public string? RequestedUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/atom+xml")
            });
        }
    }
}
