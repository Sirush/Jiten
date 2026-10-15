using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class MediaRequestDto
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public MediaRequestKind Kind { get; set; }
    public MediaType MediaType { get; set; }
    public string? ExternalUrl { get; set; }
    public LinkType? ExternalLinkType { get; set; }
    public string? Description { get; set; }
    public MediaRequestStatus Status { get; set; }
    public string? AdminNote { get; set; }
    public int? TargetDeckId { get; set; }
    public string? TargetDeckTitle { get; set; }
    public int? FulfilledDeckId { get; set; }
    public string? FulfilledDeckTitle { get; set; }
    public int UpvoteCount { get; set; }
    public int BoostCount { get; set; }
    public int CommentCount { get; set; }
    public int UploadCount { get; set; }
    public bool HasUserUpvoted { get; set; }
    public bool HasUserBoosted { get; set; }
    public bool IsSubscribed { get; set; }
    public bool IsOwnRequest { get; set; }
    public string? RequesterName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? OriginalTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public MediaRequestMetadataStatus MetadataStatus { get; set; }
    public List<Genre> Genres { get; set; } = [];
    public List<TagWithPercentageDto> Tags { get; set; } = [];

    /// <summary>An older active request for the same media; absent once that request is closed.</summary>
    public int? PossibleDuplicateOfRequestId { get; set; }
    public string? PossibleDuplicateOfTitle { get; set; }
    public int? PossibleDuplicateDeckId { get; set; }
    public string? PossibleDuplicateDeckTitle { get; set; }

    public MediaRequestDto WithMetadata(MediaRequest request, RequestGenresAndTags genresAndTags)
    {
        OriginalTitle = request.OriginalTitle;
        RomajiTitle = request.RomajiTitle;
        EnglishTitle = request.EnglishTitle;
        ReleaseDate = request.ReleaseDate;
        MetadataStatus = request.MetadataStatus;
        Genres = genresAndTags.Genres.GetValueOrDefault(request.Id) ?? [];
        Tags = genresAndTags.Tags.GetValueOrDefault(request.Id) ?? [];

        if (request.PossibleDuplicateOfRequestId is { } requestId
            && genresAndTags.ActiveRequestTitles.TryGetValue(requestId, out var requestTitle))
        {
            PossibleDuplicateOfRequestId = requestId;
            PossibleDuplicateOfTitle = requestTitle;
        }

        if (request.PossibleDuplicateDeckId is { } deckId && genresAndTags.DeckTitles.TryGetValue(deckId, out var deckTitle))
        {
            PossibleDuplicateDeckId = deckId;
            PossibleDuplicateDeckTitle = deckTitle;
        }

        return this;
    }
}

public sealed record RequestGenresAndTags(
    Dictionary<int, List<Genre>> Genres,
    Dictionary<int, List<TagWithPercentageDto>> Tags,
    Dictionary<int, string> ActiveRequestTitles,
    Dictionary<int, string> DeckTitles);
