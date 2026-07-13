namespace Jiten.Api.Jobs;

/// <summary>
/// One single-worker queue per site. Imports and syncs for a site serialise on the same queue, so the
/// politeness rate holds however many novels are queued; different sites still run concurrently.
/// </summary>
public static class WebNovelQueues
{
    public const string Syosetu = "webnovel-syosetu";

    /// <summary>
    /// Metadata refreshes are a single API call, so they get their own queue rather than waiting out the
    /// hours-long import sitting in front of them
    /// </summary>
    public const string SyosetuMetadata = "webnovel-syosetu-metadata";

    public static readonly string[] All = [Syosetu, SyosetuMetadata];
}
