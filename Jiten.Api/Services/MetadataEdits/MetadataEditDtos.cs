using System.Text.Json;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;

namespace Jiten.Api.Services.MetadataEdits;

public sealed class SubmitMetadataEditRequest : SubmitMetadataEditBase
{
    public List<string>? AddCharacterNames { get; init; }
    public List<string>? RemoveCharacterNames { get; init; }
    /// <summary>Joins this deck's franchise, or starts one with it when it has none.</summary>
    public int? FranchiseDeckId { get; init; }
    public List<string> Sources { get; init; } = [];
    public string? Note { get; init; }

    /// <summary>The values the form loaded for each field it sends; list changes replay onto the current lists and moved scalars come back as a 409.</summary>
    public SubmitMetadataEditBase? Base { get; init; }
}

public class SubmitMetadataEditBase
{
    public string? OriginalTitle { get; init; }
    public string? RomajiTitle { get; init; }
    public string? EnglishTitle { get; init; }
    public string? Description { get; init; }
    public string? ReleaseDate { get; init; }
    public MediaType? MediaType { get; init; }
    public List<string>? Aliases { get; init; }
    public List<int>? Genres { get; init; }
    public List<DeckMetadataTagPatch>? Tags { get; init; }
    public List<DeckMetadataLinkPatch>? Links { get; init; }
    public List<DeckMetadataRelationshipPatch>? Relationships { get; init; }
}

public sealed record FieldValueDto(int Field, JsonElement Current);

public sealed record SubmitMetadataEditResult(Guid SubmissionId, List<int> AppliedFields, List<int> PendingFields, List<long> EditIds);

public sealed class CheckNamesRequest
{
    public List<string>? Names { get; init; }
}

public sealed class MetadataEditCheckRequest
{
    public MetadataEditVerdict Verdict { get; init; }
    public MetadataEditDoubtReason? Reason { get; init; }
    public string? Note { get; init; }
    public string? SourceUrl { get; init; }

    /// <summary>Doubts only: which of the edit's fields look wrong; empty means all of them.</summary>
    public List<int>? Fields { get; init; }
}

public sealed class ConflictResolution
{
    public MetadataField Field { get; init; }

    /// <summary>"current" keeps the deck's value, "edit" applies the edit's value.</summary>
    public string Keep { get; init; } = "edit";
}

public sealed class ExcludedItems
{
    public MetadataField Field { get; init; }

    /// <summary>Keys in the form <see cref="MetadataEditService.ItemKey"/> builds.</summary>
    public List<string> Keys { get; init; } = [];
}

public sealed class ApproveMetadataEditRequest
{
    public List<ConflictResolution>? Resolutions { get; init; }

    /// <summary>Fields to leave out; they become a rejected part of the same submission with <see cref="ExcludeNote"/> as the reason.</summary>
    public List<int>? ExcludeFields { get; init; }

    /// <summary>Single items of list fields to leave out; they join the rejected part and the rest of the field is approved.</summary>
    public List<ExcludedItems>? ExcludeItems { get; init; }

    public string? ExcludeNote { get; init; }
}

public sealed class RejectMetadataEditRequest
{
    public string Note { get; init; } = "";
}

public sealed class UpdateContributorRequest
{
    public bool? AdminTrusted { get; init; }
    public bool? ClearBlock { get; init; }
    public bool? Banned { get; init; }

    /// <summary>"none", "reviewer" or "editor".</summary>
    public string? Role { get; init; }
}

public sealed class MetadataEditSettingsDto
{
    public int EstablishedMinAccountDays { get; init; }
    public int EstablishedCountedEdits { get; init; }
    public int EstablishedNoRevertDays { get; init; }
    public int TrustedCountedEdits { get; init; }
    public int TrustedNoRevertDays { get; init; }
    public int EstablishedAutoApplyMaxTier { get; init; }
    public int TrustedAutoApplyMaxTier { get; init; }
}

public sealed record FieldChangeDto(int Field, JsonElement Before, JsonElement After);

public sealed record EditableTagDto(int TagId, string Name, int Percentage);

public sealed record EditableLinkDto(int LinkType, string Url);

public sealed record EditableRelationshipDto(int SourceDeckId, int TargetDeckId, int RelationshipType, int OtherDeckId, string OtherDeckTitle, int OtherDeckMediaType);

public sealed record EditableViewerDto(string Role, bool HasDisplayName, bool EmailConfirmed, bool Banned);

public sealed record EditablePendingDto(long Id, int Fields, bool IsYours, DateTime CreatedAt);

public sealed class EditableDeckDto
{
    public int DeckId { get; init; }
    public string CoverName { get; init; } = "";
    public int MediaType { get; init; }
    public int CharacterCount { get; init; }
    public int? ReleaseYear { get; init; }
    public string OriginalTitle { get; init; } = "";
    public string? RomajiTitle { get; init; }
    public string? EnglishTitle { get; init; }
    public List<string> Aliases { get; init; } = [];
    public string? ReleaseDate { get; init; }
    public string? Description { get; init; }
    public List<int> Genres { get; init; } = [];
    public List<EditableTagDto> Tags { get; init; } = [];
    public List<EditableLinkDto> Links { get; init; } = [];
    public List<EditableRelationshipDto> Relationships { get; init; } = [];
    public List<string> CharacterNames { get; init; } = [];

    /// <summary>The deck's derived franchise, however it got there; only a deck without one can be added to a franchise.</summary>
    public FranchiseRefDto? Franchise { get; init; }

    public int HistoryCount { get; init; }
    public List<EditablePendingDto> Pending { get; init; } = [];
    public EditableViewerDto Viewer { get; init; } = null!;
}

/// <summary>Occurrences is only filled for reviewers and editors; Examples are short excerpts around the surface, on queue cards only.</summary>
public sealed record CharacterNameSurfaceDto(string Surface, int? Occurrences, bool Found, string Status, string? Detail, List<string>? Examples);

public sealed record CharacterNameCheckDto(string Input, string? Error, List<CharacterNameSurfaceDto> Surfaces);

public sealed record QueueDeckDto(string OriginalTitle, string? RomajiTitle, string? EnglishTitle, string CoverName, int MediaType, int? ReleaseYear);

public sealed record DisplayNameDto(string DisplayName);

public sealed record QueueAuthorDto(string DisplayName, bool IsYou);

public sealed record MyCheckDto(int Verdict, int? Reason, string? Note, int Fields);

public sealed record AuthorRecordDto(string Level, int Accepted, int Reverted);

public sealed record QueueCheckDto(string DisplayName, int Verdict, int? Reason, string? Note, string Level, int Accepted, DateTime CreatedAt, int Matched, int Missed,
                                   int Fields);

public sealed record ConflictDto(int Field, JsonElement EditBefore, JsonElement Current, JsonElement EditAfter);

/// <summary>A null id means the picked deck has no franchise yet, so the edit will start one with it; titles are then the deck's.</summary>
public sealed record FranchisePickDto(int? FranchiseId, int DeckId, string OriginalTitle, string? RomajiTitle, string? EnglishTitle, int DeckCount);

public sealed record FranchiseRefDto(int FranchiseId, string OriginalTitle, string? RomajiTitle, string? EnglishTitle, int DeckCount);

/// <summary>Into is null when the deck has no franchise yet and joins the others; otherwise they merge into it.</summary>
/// <summary><paramref name="AddedDeckTitle"/> is set when the edited deck joined <paramref name="Into"/> after the edit was made, so approving adds the picked deck instead.</summary>
public sealed record FranchiseEffectDto(List<FranchiseRefDto> Joining, FranchiseRefDto? Into, string? AddedDeckTitle = null);

/// <summary>A relationship as stored in an edit, plus the other deck's current media type and year, since titles repeat across a franchise; both are null once that deck is gone.</summary>
public sealed record RelationshipChangeValueDto(int SourceDeckId, int TargetDeckId, DeckRelationshipType RelationshipType, int OtherDeckId, string OtherDeckTitle,
                                                int? OtherDeckMediaType, int? OtherDeckReleaseYear);

public sealed class QueueItemDto
{
    public long Id { get; init; }
    public Guid SubmissionId { get; init; }
    public int DeckId { get; init; }
    public QueueDeckDto Deck { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public QueueAuthorDto Author { get; init; } = null!;
    public int Fields { get; init; }
    public List<FieldChangeDto> Changes { get; init; } = [];
    public List<string> Sources { get; init; } = [];
    public string? Note { get; init; }
    public int ConfirmCount { get; init; }
    public MyCheckDto? MyCheck { get; init; }
    public bool CanCheck { get; init; }
    public string? CheckBlockedReason { get; init; }
    public List<CharacterNameSurfaceDto>? CharacterNameChecks { get; init; }
    public FranchiseEffectDto? FranchiseEffect { get; init; }
    public int? Tier { get; init; }
    public AuthorRecordDto? AuthorRecord { get; init; }
    public List<QueueCheckDto>? Checks { get; init; }
    public int? DoubtCount { get; init; }
    public List<ConflictDto>? Conflicts { get; init; }
    public bool CanApprove { get; init; }
    public int Status { get; init; }
    public bool CanMarkReviewed { get; init; }
    public bool CanRevert { get; init; }
}

/// <summary>OtherTotal is the count of the other reviewer tab (pending or unreviewed), null for people who can't review.</summary>
public sealed record QueueResponseDto(string ViewerRole, int Total, int? OtherTotal, List<QueueItemDto> Items);

public sealed record HistoryDeckDto(int DeckId, string Title, string CoverName, int MediaType);

public sealed class HistoryItemDto
{
    public long Id { get; init; }
    public DateTime CreatedAt { get; init; }
    public int Actor { get; init; }
    public DisplayNameDto? Author { get; init; }
    public int? Provider { get; init; }
    public string PublicStatus { get; init; } = "";
    public int? Status { get; init; }
    public List<FieldChangeDto> Changes { get; init; } = [];
    public List<string> Sources { get; init; } = [];
    public int ConfirmCount { get; init; }
    public string? ReviewNote { get; init; }
    public List<int>? RevertedFields { get; init; }
    public List<int>? SkippedFields { get; init; }
    public DisplayNameDto? Reviewer { get; init; }
    public bool CanRevert { get; init; }
    public bool IsYours { get; init; }

    /// <summary>A reverted edit's values and sources are withheld from everyone but its author and reviewers, so spam isn't republished.</summary>
    public bool ContentHidden { get; init; }

    public HistoryDeckDto? Deck { get; init; }
}

public sealed record DeckHistoryOriginDto(DateTimeOffset CreatedAt, List<int> Providers);

public sealed record DeckHistoryDto(string ViewerRole, int Total, List<HistoryItemDto> Items, DeckHistoryOriginDto Origin);

public sealed record HistoryPageDto(string ViewerRole, int Total, List<HistoryItemDto> Items);

public sealed record RecentDeckDto(int DeckId, string OriginalTitle, string? RomajiTitle, string? EnglishTitle);

public sealed record RecentItemDto(long Id, DateTime CreatedAt, string? Author, string PublicStatus, int? Status, int Fields, int ConfirmCount, RecentDeckDto? Deck,
                                   List<FieldChangeDto> Changes, bool IsYours, bool ContentHidden);

public sealed record RecentPageDto(string ViewerRole, int Total, List<RecentItemDto> Items);

public sealed record ContributionStatsDto(int Accepted, int DecksImproved, int? Pending, int Checked);

public sealed record RevertResultDto(List<int> RevertedFields, List<int> SkippedFields);

public sealed record RevertAllResultDto(int Reverted, int Partial);

public sealed record ContributorDto(
    string UserId, string? DisplayName, string? Email, List<string> Roles, string Level, string LevelReason, int Counted, int Accepted,
    int Reverted, int Checks, bool AdminTrusted, bool Blocked, bool Banned, int ChecksConfirmedApproved, int ChecksConfirmedRejected,
    int ChecksDoubtedApproved, int ChecksDoubtedRejected);

/// <summary>Review workload for the admin settings page; time to decision stands in for review minutes, which aren't measured.</summary>
public sealed record MetadataEditMetricsDto(
    int Pending, double? OldestPendingHours, int LiveUnreviewed, double? MedianHoursToDecision30d, int Decided30d, int Approved30d,
    int Rejected30d, int Reverted30d, int Withdrawn30d, int Submissions30d, int AutoApplied30d, List<MetadataEditWeekDto> Weeks);

public sealed record MetadataEditWeekDto(DateTime WeekStart, int Submissions, int Decisions, int AutoApplied);

public sealed record ContributorPageDto(int Total, List<ContributorDto> Items);

/// <summary>Service outcome with the HTTP status a controller should return.</summary>
public sealed record ServiceResult<T>(T? Value, int Status = 200, string? Error = null, object? Extra = null)
{
    public static ServiceResult<T> Ok(T value) => new(value);
    public static ServiceResult<T> Fail(int status, string error, object? extra = null) => new(default, status, error, extra);
    public bool IsOk => Status is >= 200 and < 300;
}
