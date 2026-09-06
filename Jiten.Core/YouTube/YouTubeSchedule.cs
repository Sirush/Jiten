using Jiten.Core.Data.YouTube;

namespace Jiten.Core.YouTube;

public static class YouTubeSchedule
{
    /// <summary>Subbers lag uploads, so a rejected track is re-checked while the video is this young</summary>
    public static readonly TimeSpan RecheckWindow = TimeSpan.FromDays(90);
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromDays(7);

    /// <summary>Weekly while a source uploads; monthly once it has been quiet for three months</summary>
    public static DateTimeOffset NextCheck(DateTimeOffset? lastSourceUpdate, int? checkIntervalDays = null)
    {
        if (checkIntervalDays is > 0)
            return DateTimeOffset.UtcNow.AddDays(checkIntervalDays.Value);

        var quiet = lastSourceUpdate == null || DateTimeOffset.UtcNow - lastSourceUpdate.Value > TimeSpan.FromDays(90);
        return DateTimeOffset.UtcNow.AddDays(quiet ? 30 : 7);
    }

    public static DateTimeOffset NextCheck(YouTubeSource source) => NextCheck(source.LastSourceUpdate, source.CheckIntervalDays);

    public static DateTimeOffset NextCheckAfterFailure(int consecutiveFailures) =>
        DateTimeOffset.UtcNow.AddDays(Math.Min(30, Math.Pow(2, Math.Max(0, consecutiveFailures - 1))));

    public static bool ShouldRecheck(YouTubeVideo video, DateTimeOffset now)
    {
        if (video.Status != YouTubeVideoStatus.NoManualSubs)
            return false;
        if (video.UploadedAt == null || now - video.UploadedAt.Value > RecheckWindow)
            return false;
        return now - video.LastCheckedAt >= RecheckInterval;
    }
}
