using Jiten.Core.Data;

namespace Jiten.Api.Services.ExternalMediaList;

public enum ExternalListProvider
{
    Anilist,
    Vndb,
}

public record ExternalListEntry(
    string ExternalId,
    string Title,
    string Url,
    string ExternalStatus,
    DeckStatus MappedStatus,
    DateOnly? FinishedAt,
    int? Progress = null,
    DateOnly? StartedOn = null,
    DateOnly? CompletedOn = null,
    int? RepeatCount = null);

public record ExternalListFetchResult(List<ExternalListEntry> Entries, string? Error)
{
    public static ExternalListFetchResult Fail(string error) => new([], error);
}

public interface IExternalMediaListClient
{
    Task<ExternalListFetchResult> FetchListAsync(ExternalListProvider provider, string username, CancellationToken ct = default);
}
