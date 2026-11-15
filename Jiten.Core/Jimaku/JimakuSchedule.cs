namespace Jiten.Core.Jimaku;

public static class JimakuSchedule
{
    /// <summary>Daily while the show airs; weekly once it finished but the ingested count still disagrees</summary>
    public static DateTimeOffset NextCheck(bool finishedAiring) =>
        DateTimeOffset.UtcNow.AddDays(finishedAiring ? 7 : 1);

    public static DateTimeOffset NextCheckAfterFailure(int consecutiveFailures) =>
        DateTimeOffset.UtcNow.AddDays(Math.Min(30, Math.Pow(2, Math.Max(0, consecutiveFailures - 1))));

    public static bool IsComplete(bool finishedAiring, int? expectedEpisodes, int imported, int unresolved) =>
        finishedAiring && unresolved == 0 && expectedEpisodes is { } expected && imported >= expected;
}
