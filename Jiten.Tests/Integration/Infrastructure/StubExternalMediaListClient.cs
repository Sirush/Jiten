using Jiten.Api.Services.ExternalMediaList;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>Canned external list responses; set <see cref="Result"/> per test. Singleton, so reset it in InitializeAsync.</summary>
public class StubExternalMediaListClient : IExternalMediaListClient
{
    public ExternalListFetchResult Result { get; set; } = new([], null);

    public List<(ExternalListProvider Provider, string Username)> Calls { get; } = new();

    public Task<ExternalListFetchResult> FetchListAsync(ExternalListProvider provider, string username,
                                                        CancellationToken ct = default)
    {
        Calls.Add((provider, username));
        return Task.FromResult(Result);
    }
}
