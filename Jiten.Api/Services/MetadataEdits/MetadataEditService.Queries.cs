using System.Text.Json;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.MetadataEdits;

public sealed partial class MetadataEditService
{
    private const string UnnamedContributor = "A contributor";
    private const int MaxQueueScan = 500;

    private async Task<Dictionary<string, string>> DisplayNamesAsync(IEnumerable<string?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => id != null).Select(id => id!).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<string, string>();
        var rows = await userDb.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct);
        return rows.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? UnnamedContributor : u.DisplayName!);
    }

    public static string PublicStatus(MetadataEditStatus status) => status switch
    {
        MetadataEditStatus.Applied or MetadataEditStatus.Approved => "live",
        MetadataEditStatus.Pending => "pending",
        MetadataEditStatus.Rejected => "rejected",
        MetadataEditStatus.Reverted => "reverted",
        MetadataEditStatus.Withdrawn => "withdrawn",
        _ => "superseded"
    };

    /// <summary>Reverted edits keep their values and sources for the author and reviewers only, so reverted spam isn't shown again in the public logs.</summary>
    private static bool HidesContent(MetadataEditStatus status, string? authorId, string? viewerId, bool canReview) =>
        status == MetadataEditStatus.Reverted && !canReview && authorId != viewerId;

    private sealed record RelatedDeckInfo(int MediaType, int? ReleaseYear);

    private static List<SnapshotRelationship> RelationshipValues(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.Deserialize<List<SnapshotRelationship>>(DeckMetadataSnapshot.Json) ?? [] : [];

    private async Task<Dictionary<int, RelatedDeckInfo>> RelatedDecksAsync(IEnumerable<MetadataFieldChange> changes, CancellationToken ct)
    {
        var ids = changes.Where(c => c.Field == MetadataField.Relationships)
                         .SelectMany(c => RelationshipValues(c.Before).Concat(RelationshipValues(c.After)))
                         .Select(r => r.OtherDeckId).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, RelatedDeckInfo>();

        var rows = await db.Decks.AsNoTracking().Where(d => ids.Contains(d.DeckId)).Select(d => new { d.DeckId, d.MediaType, d.ReleaseDate }).ToListAsync(ct);
        return rows.ToDictionary(d => d.DeckId, d => new RelatedDeckInfo((int)d.MediaType, d.ReleaseDate == default ? null : d.ReleaseDate.Year));
    }

    private static JsonElement WithRelatedDeckInfo(JsonElement value, Dictionary<int, RelatedDeckInfo> related)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return value;

        var enriched = RelationshipValues(value).Select(r =>
        {
            var info = related.GetValueOrDefault(r.OtherDeckId);
            return new RelationshipChangeValueDto(r.SourceDeckId, r.TargetDeckId, r.RelationshipType, r.OtherDeckId, r.OtherDeckTitle, info?.MediaType, info?.ReleaseYear);
        });
        return JsonSerializer.SerializeToElement(enriched, DeckMetadataSnapshot.Json);
    }

    private static List<FieldChangeDto> ChangeDtos(IEnumerable<MetadataFieldChange> changes, Dictionary<int, RelatedDeckInfo> related) =>
        changes.Select(c => c.Field == MetadataField.Relationships
                            ? new FieldChangeDto((int)c.Field, WithRelatedDeckInfo(c.Before, related), WithRelatedDeckInfo(c.After, related))
                            : new FieldChangeDto((int)c.Field, c.Before, c.After))
               .ToList();

    private IQueryable<MetadataEdit> QueueQuery(MetadataEditStatus status, int? deckId)
    {
        var query = db.MetadataEdits.AsNoTracking().Where(e => e.Status == status && e.Actor == MetadataEditActor.User && e.RevertsEditId == null);
        return deckId.HasValue ? query.Where(e => e.DeckId == deckId.Value) : query;
    }

    /// <summary>Pages in SQL; a risk-tier filter needs the parsed changes, so it pages over the newest <see cref="MaxQueueScan"/> in memory.</summary>
    private async Task<(int Total, List<long> PageIds)> QueuePageAsync(MetadataEditStatus status, int? deckId, int? tier, string? sort, bool canReview,
                                                                       int offset, int limit, CancellationToken ct)
    {
        var query = QueueQuery(status, deckId);
        var ordered = sort switch
        {
            "confirmed" => query.OrderByDescending(e => e.Checks.Count(c => c.Verdict == MetadataEditVerdict.Confirm)).ThenByDescending(e => e.CreatedAt),
            "doubted" when canReview => query.OrderByDescending(e => e.Checks.Count(c => c.Verdict == MetadataEditVerdict.Doubt)).ThenByDescending(e => e.CreatedAt),
            _ => query.OrderByDescending(e => e.CreatedAt)
        };

        if (tier == null)
            return (await query.CountAsync(ct), limit == 0 ? [] : await ordered.Skip(offset).Take(limit).Select(e => e.Id).ToListAsync(ct));

        var scanned = await ordered.Take(MaxQueueScan).Select(e => new { e.Id, e.ChangesJson }).ToListAsync(ct);
        var matching = scanned.Where(s => (int)MaxTier(ParseChanges(s.ChangesJson)) == tier).Select(s => s.Id).ToList();
        return (matching.Count, matching.Skip(offset).Take(limit).ToList());
    }

    /// <summary>"unreviewed" lists live edits nobody has reviewed yet; only reviewers and editors get it, everyone else gets the pending queue.</summary>
    public async Task<QueueResponseDto> QueueAsync(string viewerId, string? state, string? sort, int? tier, int? deckId, int offset, int limit,
                                                   CancellationToken ct)
    {
        var viewer = await trust.GetViewerAsync(viewerId, ct);
        var canReview = viewer?.CanReview == true;
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(0, offset);
        var tierFilter = canReview ? tier : null;

        var unreviewed = canReview && state == "unreviewed";
        var wanted = unreviewed ? MetadataEditStatus.Applied : MetadataEditStatus.Pending;
        var (total, pageIds) = await QueuePageAsync(wanted, deckId, tierFilter, sort, canReview, offset, limit, ct);
        int? otherTotal = canReview
            ? (await QueuePageAsync(unreviewed ? MetadataEditStatus.Pending : MetadataEditStatus.Applied, deckId, tierFilter, null, true, 0, 0, ct)).Total
            : null;

        var edits = await db.MetadataEdits.AsNoTracking().Include(e => e.Checks).Where(e => pageIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var page = pageIds.Select(id => (Edit: edits[id], Changes: ParseChanges(edits[id].ChangesJson))).ToList();

        var deckIds = page.Select(p => p.Edit.DeckId).Distinct().ToList();
        var decks = await db.Decks.AsNoTracking().Where(d => deckIds.Contains(d.DeckId))
                            .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.MediaType, d.ReleaseDate, d.FranchiseId })
                            .ToDictionaryAsync(d => d.DeckId, ct);
        var checkerIds = page.SelectMany(p => p.Edit.Checks.Select(c => c.UserId)).ToList();
        var names = await DisplayNamesAsync(page.Select(p => p.Edit.UserId).Concat(checkerIds), ct);
        var snapshots = canReview && !unreviewed ? await deckMetadata.LoadSnapshotsAsync(deckIds, ct) : new();
        var related = await RelatedDecksAsync(page.SelectMany(p => p.Changes), ct);
        var records = canReview
            ? await trust.GetManyAsync(page.Where(p => p.Edit.UserId != null).Select(p => p.Edit.UserId!).Concat(checkerIds), ct)
            : new();

        var items = new List<QueueItemDto>();
        foreach (var (edit, changes) in page)
        {
            var deck = decks[edit.DeckId];
            var author = edit.UserId == null ? null : records.GetValueOrDefault(edit.UserId);
            var myCheck = edit.Checks.FirstOrDefault(c => c.UserId == viewerId);
            var blockedReason = CheckBlockedReason(viewer, edit);

            List<CharacterNameSurfaceDto>? nameResults = null;
            if (changes.FirstOrDefault(c => c.Field == MetadataField.CharacterNames) is { } nameChange && AddedNames(nameChange) is { Added.Count: > 0 } addedNames)
                nameResults = (await nameCheckService.CheckSurfacesAsync(edit.DeckId, addedNames.Before, addedNames.Added, ct))
                              .Select(r => ToDto(r.Check, canReview, r.Examples)).ToList();

            var checks = canReview
                ? edit.Checks.OrderBy(c => c.CreatedAt)
                      .Select(c =>
                      {
                          var checker = records.GetValueOrDefault(c.UserId);
                          return new QueueCheckDto(names.GetValueOrDefault(c.UserId, UnnamedContributor), (int)c.Verdict, (int?)c.Reason, c.Note,
                                                   checker?.LevelName ?? "new", checker?.Accepted ?? 0, c.CreatedAt,
                                                   checker?.CheckStats.Matched ?? 0, checker?.CheckStats.Missed ?? 0, (int)c.Fields);
                      })
                      .ToList()
                : null;

            items.Add(new QueueItemDto
            {
                Id = edit.Id,
                SubmissionId = edit.SubmissionId,
                DeckId = edit.DeckId,
                Deck = new QueueDeckDto(deck.OriginalTitle, deck.RomajiTitle, deck.EnglishTitle, deck.CoverName, (int)deck.MediaType,
                                        deck.ReleaseDate == default ? null : deck.ReleaseDate.Year),
                CreatedAt = edit.CreatedAt,
                Author = new QueueAuthorDto(edit.UserId == null ? "Jiten" : names.GetValueOrDefault(edit.UserId, UnnamedContributor), edit.UserId == viewerId),
                Fields = (int)edit.Fields,
                Changes = ChangeDtos(changes, related),
                Sources = ParseSources(edit.SourcesJson),
                Note = edit.EditNote,
                ConfirmCount = edit.Checks.Count(c => c.Verdict == MetadataEditVerdict.Confirm),
                MyCheck = myCheck == null ? null : new MyCheckDto((int)myCheck.Verdict, (int?)myCheck.Reason, myCheck.Note, (int)myCheck.Fields),
                CanCheck = blockedReason == null,
                CheckBlockedReason = blockedReason,
                CharacterNameChecks = nameResults,
                FranchiseEffect = await FranchiseEffectAsync(deck.FranchiseId, changes, ct),
                Tier = canReview ? (int)MaxTier(changes) : null,
                AuthorRecord = canReview && author != null ? new AuthorRecordDto(author.LevelName, author.Accepted, author.Reverted) : null,
                Checks = checks,
                DoubtCount = canReview ? edit.Checks.Count(c => c.Verdict == MetadataEditVerdict.Doubt) : null,
                Conflicts = snapshots.TryGetValue(edit.DeckId, out var snapshot) ? Conflicts(snapshot, changes) : null,
                CanApprove = !unreviewed && viewer != null && edit.UserId != viewerId && CanActOn(viewer, changes),
                Status = (int)edit.Status,
                CanMarkReviewed = unreviewed && edit.UserId != viewerId,
                CanRevert = unreviewed
            });
        }

        return new QueueResponseDto(ViewerRole(viewer), total, otherTotal, items);
    }

    /// <summary>The franchises an added story link or franchise pin would merge or extend.</summary>
    private async Task<FranchiseEffectDto?> FranchiseEffectAsync(int? franchiseId, List<MetadataFieldChange> changes, CancellationToken ct)
    {
        var otherFranchises = new List<int>();
        if (changes.FirstOrDefault(c => c.Field == MetadataField.Relationships) is { } change)
        {
            var before = RelationshipValues(change.Before);
            var added = RelationshipValues(change.After)
                        .Where(r => DeckRelationship.IsStoryLinkType(r.RelationshipType))
                        .Where(r => !before.Any(b => b.SourceDeckId == r.SourceDeckId && b.TargetDeckId == r.TargetDeckId && b.RelationshipType == r.RelationshipType))
                        .Select(r => r.OtherDeckId).Distinct().ToList();
            if (added.Count > 0)
                otherFranchises.AddRange(await db.Decks.AsNoTracking().Where(d => added.Contains(d.DeckId) && d.FranchiseId != null)
                                                 .Select(d => d.FranchiseId!.Value).Distinct().ToListAsync(ct));
        }

        string? addedDeckTitle = null;
        if (changes.FirstOrDefault(c => c.Field == MetadataField.Franchise) is { After.ValueKind: JsonValueKind.Object } pin)
        {
            if (!IsUnresolvedFranchise(pin.After))
                otherFranchises.Add(pin.After.GetProperty("franchiseId").GetInt32());
            else
            {
                var withDeckId = pin.After.GetProperty("withDeckId").GetInt32();
                var picked = await db.Decks.AsNoTracking().Where(d => d.DeckId == withDeckId)
                                     .Select(d => new { d.FranchiseId, d.OriginalTitle }).FirstOrDefaultAsync(ct);
                if (picked?.FranchiseId is { } pickedFranchise && franchiseId == null)
                    otherFranchises.Add(pickedFranchise);
                else if (picked is { FranchiseId: null } && franchiseId != null)
                    addedDeckTitle = picked.OriginalTitle;
            }
        }

        var involved = otherFranchises.Where(f => f != franchiseId).Distinct().ToList();
        if (involved.Count == 0 && addedDeckTitle == null)
            return null;

        var all = involved.Concat(franchiseId.HasValue ? [franchiseId.Value] : Array.Empty<int>()).Distinct().ToList();
        var franchises = await db.Set<Franchise>().AsNoTracking().Where(f => all.Contains(f.FranchiseId))
                                 .Select(f => new { f.FranchiseId, f.OriginalTitle, f.RomajiTitle, f.EnglishTitle })
                                 .ToDictionaryAsync(f => f.FranchiseId, ct);
        var sizes = await db.Decks.AsNoTracking().Where(d => d.FranchiseId != null && all.Contains(d.FranchiseId!.Value) && d.ParentDeckId == null)
                            .GroupBy(d => d.FranchiseId!.Value).Select(g => new { g.Key, Count = g.Count() })
                            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        FranchiseRefDto? Ref(int id) => franchises.TryGetValue(id, out var f)
            ? new FranchiseRefDto(id, f.OriginalTitle, f.RomajiTitle, f.EnglishTitle, sizes.GetValueOrDefault(id))
            : null;

        var joining = involved.Select(Ref).OfType<FranchiseRefDto>().ToList();
        var into = franchiseId.HasValue ? Ref(franchiseId.Value) : null;
        if (addedDeckTitle != null && into != null)
            return new FranchiseEffectDto(joining, into, addedDeckTitle);
        return joining.Count == 0 ? null : new FranchiseEffectDto(joining, into);
    }

    private async Task<List<HistoryItemDto>> HistoryItemsAsync(List<MetadataEdit> edits, MetadataViewer? viewer, bool withDeck, CancellationToken ct)
    {
        var canReview = viewer?.CanReview == true;
        var ids = edits.Select(e => e.Id).ToList();
        var pendingIds = edits.Where(e => e.Status == MetadataEditStatus.Pending).Select(e => e.Id).ToList();
        var confirms = pendingIds.Count == 0
            ? new Dictionary<long, int>()
            : await db.MetadataEditChecks.AsNoTracking()
                      .Where(c => pendingIds.Contains(c.MetadataEditId) && c.Verdict == MetadataEditVerdict.Confirm)
                      .GroupBy(c => c.MetadataEditId).Select(g => new { g.Key, Count = g.Count() })
                      .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var reverts = await db.MetadataEdits.AsNoTracking().Where(e => e.RevertsEditId != null && ids.Contains(e.RevertsEditId.Value))
                              .Select(e => new { e.RevertsEditId, e.Fields }).ToListAsync(ct);
        var names = await DisplayNamesAsync(edits.Select(e => e.UserId).Concat(edits.Select(e => e.ReviewedByUserId)), ct);

        Dictionary<int, HistoryDeckDto> decks = new();
        if (withDeck)
        {
            var deckIds = edits.Select(e => e.DeckId).Distinct().ToList();
            decks = await db.Decks.AsNoTracking().Where(d => deckIds.Contains(d.DeckId))
                            .Select(d => new HistoryDeckDto(d.DeckId, d.OriginalTitle, d.CoverName, (int)d.MediaType))
                            .ToDictionaryAsync(d => d.DeckId, ct);
        }

        var parsed = edits.ToDictionary(e => e.Id, e => ParseChanges(e.ChangesJson));
        var related = await RelatedDecksAsync(parsed.Values.SelectMany(c => c), ct);

        return edits.Select(e =>
        {
            var revertFields = reverts.FirstOrDefault(r => r.RevertsEditId == e.Id)?.Fields;
            var reverted = revertFields.HasValue ? MetadataFields.Split(revertFields.Value).Select(f => (int)f).ToList() : null;
            var skipped = revertFields.HasValue ? MetadataFields.Split(e.Fields & ~revertFields.Value).Select(f => (int)f).ToList() : null;
            var reviewerShown = canReview && e.ReviewedByUserId != null && e.ReviewedByUserId != e.UserId;
            var isYours = viewer != null && e.UserId == viewer.UserId;
            var hidden = HidesContent(e.Status, e.UserId, viewer?.UserId, canReview);
            return new HistoryItemDto
            {
                Id = e.Id,
                CreatedAt = e.CreatedAt,
                Actor = (int)e.Actor,
                Author = e.Actor == MetadataEditActor.User && e.UserId != null ? new DisplayNameDto(names.GetValueOrDefault(e.UserId, UnnamedContributor)) : null,
                Provider = e.Actor == MetadataEditActor.Provider ? (int?)e.Provider : null,
                PublicStatus = PublicStatus(e.Status),
                Status = canReview ? (int)e.Status : null,
                Changes = hidden ? [] : ChangeDtos(parsed[e.Id], related),
                Sources = hidden ? [] : ParseSources(e.SourcesJson),
                ConfirmCount = confirms.GetValueOrDefault(e.Id),
                ReviewNote = (canReview || isYours) && e.Status is MetadataEditStatus.Rejected or MetadataEditStatus.Reverted ? e.ReviewNote : null,
                RevertedFields = reverted,
                SkippedFields = skipped is { Count: > 0 } ? skipped : null,
                Reviewer = reviewerShown ? new DisplayNameDto(names.GetValueOrDefault(e.ReviewedByUserId!, UnnamedContributor)) : null,
                CanRevert = canReview && e.Status is MetadataEditStatus.Applied or MetadataEditStatus.Approved,
                IsYours = isYours,
                ContentHidden = hidden,
                Deck = withDeck ? decks.GetValueOrDefault(e.DeckId) : null
            };
        }).ToList();
    }

    public async Task<ServiceResult<DeckHistoryDto>> DeckHistoryAsync(int deckId, string? viewerId, int offset, int limit, CancellationToken ct)
    {
        var deck = await db.Decks.AsNoTracking().Where(d => d.DeckId == deckId)
                           .Select(d => new { d.CreationDate, Links = d.Links.Select(l => l.LinkType).ToList() }).FirstOrDefaultAsync(ct);
        if (deck == null)
            return ServiceResult<DeckHistoryDto>.Fail(404, "This deck doesn't exist.");

        var viewer = viewerId == null ? null : await trust.GetViewerAsync(viewerId, ct);
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var query = db.MetadataEdits.AsNoTracking().Where(e => e.DeckId == deckId && e.RevertsEditId == null);
        var total = await query.CountAsync(ct);
        var edits = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Skip(offset).Take(limit).ToListAsync(ct);

        var providers = deck.Links.Where(l => l != LinkType.Web).Distinct().Select(l => (int)l).ToList();
        var origin = new DeckHistoryOriginDto(deck.CreationDate, providers);
        return ServiceResult<DeckHistoryDto>.Ok(new DeckHistoryDto(ViewerRole(viewer), total, await HistoryItemsAsync(edits, viewer, false, ct), origin));
    }

    public async Task<ServiceResult<RecentPageDto>> RecentAsync(string? viewerId, string? status, int offset, int limit, CancellationToken ct)
    {
        var viewer = viewerId == null ? null : await trust.GetViewerAsync(viewerId, ct);
        var canReview = viewer?.CanReview == true;
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var query = db.MetadataEdits.AsNoTracking().Where(e => e.RevertsEditId == null && e.Actor == MetadataEditActor.User);
        query = status switch
        {
            "live" => query.Where(e => e.Status == MetadataEditStatus.Applied || e.Status == MetadataEditStatus.Approved),
            "pending" => query.Where(e => e.Status == MetadataEditStatus.Pending),
            "reverted" => query.Where(e => e.Status == MetadataEditStatus.Reverted),
            "unreviewed" when canReview => query.Where(e => e.Status == MetadataEditStatus.Applied),
            "unreviewed" => query.Where(_ => false),
            _ => query
        };

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Skip(offset).Take(limit)
                              .Select(e => new
                              {
                                  e.Id, e.CreatedAt, e.UserId, e.Status, e.Fields, e.DeckId, e.ChangesJson,
                                  ConfirmCount = e.Status == MetadataEditStatus.Pending ? e.Checks.Count(c => c.Verdict == MetadataEditVerdict.Confirm) : 0
                              })
                              .ToListAsync(ct);

        var names = await DisplayNamesAsync(rows.Select(r => r.UserId), ct);
        var deckIds = rows.Select(r => r.DeckId).Distinct().ToList();
        var decks = await db.Decks.AsNoTracking().Where(d => deckIds.Contains(d.DeckId))
                            .Select(d => new RecentDeckDto(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle))
                            .ToDictionaryAsync(d => d.DeckId, ct);

        var parsed = rows.ToDictionary(r => r.Id, r => ParseChanges(r.ChangesJson));
        var related = await RelatedDecksAsync(parsed.Values.SelectMany(c => c), ct);

        var items = rows.Select(r =>
        {
            var hidden = HidesContent(r.Status, r.UserId, viewerId, canReview);
            return new RecentItemDto(
                r.Id, r.CreatedAt,
                r.UserId != null ? names.GetValueOrDefault(r.UserId, UnnamedContributor) : null,
                PublicStatus(r.Status),
                canReview ? (int)r.Status : null,
                (int)r.Fields,
                r.ConfirmCount,
                decks.GetValueOrDefault(r.DeckId),
                hidden ? [] : ChangeDtos(parsed[r.Id], related),
                viewerId != null && r.UserId == viewerId,
                hidden);
        }).ToList();
        return ServiceResult<RecentPageDto>.Ok(new RecentPageDto(ViewerRole(viewer), total, items));
    }

    public async Task<HistoryPageDto> ByUserAsync(string userId, string? viewerId, int offset, int limit, CancellationToken ct)
    {
        var viewer = viewerId == null ? null : await trust.GetViewerAsync(viewerId, ct);
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var query = db.MetadataEdits.AsNoTracking()
                      .Where(e => e.UserId == userId && e.Actor == MetadataEditActor.User && e.RevertsEditId == null);
        var total = await query.CountAsync(ct);
        var edits = await query.OrderByDescending(e => e.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new HistoryPageDto(ViewerRole(viewer), total, await HistoryItemsAsync(edits, viewer, true, ct));
    }

    public async Task<ContributionStatsDto> ContributionsAsync(string userId, string? viewerId, CancellationToken ct)
    {
        var edits = await db.MetadataEdits.AsNoTracking()
                            .Where(e => e.UserId == userId && e.Actor == MetadataEditActor.User && e.RevertsEditId == null)
                            .Select(e => new { e.SubmissionId, e.DeckId, e.Status }).ToListAsync(ct);
        var accepted = edits.Where(e => e.Status is MetadataEditStatus.Applied or MetadataEditStatus.Approved).ToList();
        var checkedCount = await db.MetadataEditChecks.CountAsync(c => c.UserId == userId, ct);
        return new ContributionStatsDto(
            accepted.Select(e => e.SubmissionId).Distinct().Count(),
            accepted.Select(e => e.DeckId).Distinct().Count(),
            viewerId == userId ? edits.Count(e => e.Status == MetadataEditStatus.Pending) : null,
            checkedCount);
    }
}
