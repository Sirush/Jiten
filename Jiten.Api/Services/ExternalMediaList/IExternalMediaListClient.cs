using Jiten.Core.Data;

namespace Jiten.Api.Services.ExternalMediaList;

public enum ExternalListProvider
{
    Anilist,
    Vndb,
}

public enum DatePrecision
{
    Day,
    Month,
    Year,
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
    int? RepeatCount = null,
    DatePrecision StartedPrecision = DatePrecision.Day,
    DatePrecision CompletedPrecision = DatePrecision.Day);

public record ExternalListFetchResult(List<ExternalListEntry> Entries, string? Error)
{
    public static ExternalListFetchResult Fail(string error) => new([], error);
}

public interface IExternalMediaListClient
{
    Task<ExternalListFetchResult> FetchListAsync(ExternalListProvider provider, string username, CancellationToken ct = default);
}
