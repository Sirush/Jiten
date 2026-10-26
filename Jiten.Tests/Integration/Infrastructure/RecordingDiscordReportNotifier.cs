using System.Collections.Concurrent;
using Jiten.Api.Services;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>Records report pushes so no test posts to the real Discord webhook from sharedsettings.</summary>
public class RecordingDiscordReportNotifier : IDiscordReportNotifier
{
    public record SentReport(string ReporterId, string Title, string Description, string Username);

    private readonly ConcurrentQueue<SentReport> _sent = new();

    public IReadOnlyList<SentReport> Sent => _sent.ToArray();

    public void Clear() => _sent.Clear();

    public Task<bool> SendAsync(string reporterId, string title, string description, string username = "IssueReporter",
                                CancellationToken ct = default)
    {
        _sent.Enqueue(new SentReport(reporterId, title, description, username));
        return Task.FromResult(true);
    }
}
