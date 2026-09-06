namespace Jiten.Api.Services;

public class YouTubeOptions
{
    /// <summary>
    /// Fetch subtitles from the server's own egress (proxied via YtDlp:Proxy). Off means the home CLI drains
    /// the Pending queue and the server only parses.
    /// </summary>
    public bool ServerFetch { get; set; }

    /// <summary>Videos fetched per drain run, so one huge backlog cannot monopolise the queue</summary>
    public int FetchBatchSize { get; set; } = 50;

    public int DelayMs { get; set; } = 1500;
}
