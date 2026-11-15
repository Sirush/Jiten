namespace Jiten.Api.Jobs;

/// <summary>
/// Single-worker queue: two syncs of one show must never overlap, or both would ingest the same episode.
/// </summary>
public static class JimakuQueues
{
    public const string Sync = "jimaku";
}
