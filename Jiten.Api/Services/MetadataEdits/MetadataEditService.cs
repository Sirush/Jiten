using System.Text.Json;
using System.Text.RegularExpressions;
using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Core.Data.Providers;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services.MetadataEdits;

public sealed partial class MetadataEditService(
    JitenDbContext db,
    UserDbContext userDb,
    DeckMetadataService deckMetadata,
    MetadataTrustService trust,
    CharacterNameCheckService nameCheckService,
    NotificationService notifications,
    FranchiseSyncRunner franchiseSync,
    IBackgroundJobClient jobs,
    IMemoryCache cache,
    IIndexNowService indexNow,
    IHostEnvironment env,
    UserManager<User> userManager,
    ILogger<MetadataEditService> logger)
{
    public const int MaxSources = 5;
    public const int MaxAliases = 15;
    public const int MaxAliasLength = 200;
    public const int MaxTitleLength = 300;
    public const int MaxNoteLength = 500;
    public const int MaxAddedCharacterNames = 30;

    /// <summary>Upper bound on any submitted list before validation; real decks stay far below it.</summary>
    public const int MaxListItems = 200;
    public const int MinAccountDaysToCheck = 7;
    public const int NewHourlyLimit = 15;
    public const int NewDailyLimit = 30;
    public const int EstablishedHourlyLimit = 100;
    public const int EditorHourlyLimit = 500;
    public const string BannedMessage = "Your account can't suggest edits right now.";
    public static readonly TimeSpan RevertedFieldCooldown = TimeSpan.FromDays(7);
    public static readonly TimeSpan NameReparseDebounce = TimeSpan.FromHours(1);

    [GeneratedRegex(@"https?://|www\.", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlChars();

    public static List<MetadataFieldChange> ParseChanges(string json) =>
        JsonSerializer.Deserialize<List<MetadataFieldChange>>(json, DeckMetadataSnapshot.Json) ?? [];

    public static string SerializeChanges(IEnumerable<MetadataFieldChange> changes) =>
        JsonSerializer.Serialize(changes, DeckMetadataSnapshot.Json);

    public static List<string> ParseSources(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, DeckMetadataSnapshot.Json) ?? [];

    /// <summary>Links are low risk except a new free-form Web link, which is reviewed like a description.</summary>
    public static MetadataRiskTier EffectiveTier(MetadataFieldChange change)
    {
        if (change.Field == MetadataField.Links)
        {
            var beforeWeb = ListOf<SnapshotLink>(change.Before).Where(l => l.LinkType == LinkType.Web).Select(l => l.Url).ToHashSet();
            if (ListOf<SnapshotLink>(change.After).Any(l => l.LinkType == LinkType.Web && !beforeWeb.Contains(l.Url)))
                return MetadataRiskTier.High;
        }

        return MetadataFields.Tier(change.Field);
    }

    public static MetadataRiskTier MaxTier(IEnumerable<MetadataFieldChange> changes) =>
        changes.Select(EffectiveTier).DefaultIfEmpty(MetadataRiskTier.Low).Max();

    /// <summary>Turns a set of field values (an edit's after or before half) into a patch for <see cref="DeckMetadataService"/>.</summary>
    public static DeckMetadataPatch BuildPatch(IEnumerable<(MetadataField Field, JsonElement Value)> values)
    {
        string? originalTitle = null, romaji = null, english = null, description = null, releaseDate = null;
        MediaType? mediaType = null;
        List<string>? aliases = null, names = null;
        List<int>? genres = null;
        List<DeckMetadataTagPatch>? tags = null;
        List<DeckMetadataLinkPatch>? links = null;
        List<DeckMetadataRelationshipPatch>? relationships = null;
        DeckMetadataFranchisePatch? franchise = null;

        static string Str(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        foreach (var (field, value) in values)
        {
            switch (field)
            {
                case MetadataField.OriginalTitle: originalTitle = Str(value); break;
                case MetadataField.RomajiTitle: romaji = Str(value); break;
                case MetadataField.EnglishTitle: english = Str(value); break;
                case MetadataField.Description: description = Str(value); break;
                case MetadataField.ReleaseDate: releaseDate = Str(value); break;
                case MetadataField.MediaType: mediaType = (MediaType)value.GetInt32(); break;
                case MetadataField.Aliases: aliases = value.Deserialize<List<string>>(DeckMetadataSnapshot.Json) ?? []; break;
                case MetadataField.CharacterNames: names = value.Deserialize<List<string>>(DeckMetadataSnapshot.Json) ?? []; break;
                case MetadataField.Genres: genres = value.Deserialize<List<int>>(DeckMetadataSnapshot.Json) ?? []; break;
                case MetadataField.Tags:
                    tags = value.EnumerateArray()
                                .Select(t => new DeckMetadataTagPatch { TagId = t.GetProperty("tagId").GetInt32(), Percentage = t.GetProperty("percentage").GetInt32() })
                                .ToList();
                    break;
                case MetadataField.Links:
                    links = ListOf<SnapshotLink>(value).Select(l => new DeckMetadataLinkPatch { LinkType = l.LinkType, Url = l.Url }).ToList();
                    break;
                case MetadataField.Relationships:
                    relationships = value.EnumerateArray()
                                         .Select(r => new DeckMetadataRelationshipPatch
                                                      {
                                                          SourceDeckId = r.GetProperty("sourceDeckId").GetInt32(),
                                                          TargetDeckId = r.GetProperty("targetDeckId").GetInt32(),
                                                          RelationshipType = (DeckRelationshipType)r.GetProperty("relationshipType").GetInt32()
                                                      })
                                         .ToList();
                    break;
                case MetadataField.Franchise:
                    franchise = value.Deserialize<SnapshotFranchise>(DeckMetadataSnapshot.Json) is { } pick
                        ? new DeckMetadataFranchisePatch { FranchiseId = pick.FranchiseId, WithDeckId = pick.FranchiseId == null ? pick.WithDeckId : null }
                        : new DeckMetadataFranchisePatch();
                    break;
            }
        }

        return new DeckMetadataPatch
               {
                   OriginalTitle = originalTitle, RomajiTitle = romaji, EnglishTitle = english, Description = description,
                   ReleaseDate = releaseDate, MediaType = mediaType, Aliases = aliases, CharacterNames = names, Genres = genres,
                   Tags = tags, Links = links, Relationships = relationships, Franchise = franchise
               };
    }

    /// <summary>Applies field values to a tracked deck through the shared reconcile path; returns an error message or null.</summary>
    private async Task<string?> ApplyValuesAsync(Deck deck, IEnumerable<(MetadataField Field, JsonElement Value)> values, string? userId, CancellationToken ct,
                                                 bool recordRemovals = true) =>
        await deckMetadata.ApplyAsync(deck, BuildPatch(values), ct, userId, recordRemovals);

    /// <summary>Side effects that must follow a committed metadata write: franchise derivation, name reparse, search pings.</summary>
    private async Task AfterApplyAsync(int deckId, MetadataField fields, CancellationToken ct)
    {
        if ((fields & MetadataFields.FranchiseShaping) != 0)
            await franchiseSync.RunAsync(ct);

        if (fields.HasFlag(MetadataField.CharacterNames))
            ScheduleNameReparse(deckId);

        if ((fields & MetadataFields.Indexed) != 0 && env.IsProduction())
        {
            try
            {
                await indexNow.SubmitDeckAsync(deckId, ct);
                await BunnyCdnHelper.PurgeUrl($"https://jiten.moe/decks/media/{deckId}/");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Search ping after metadata edit failed for deck {DeckId}", deckId);
            }
        }

        await SupersedePendingAsync(deckId, ct);
    }

    /// <summary>At most one name-triggered reparse per deck per hour; a reparse reads names at run time, so a pending one covers later edits.</summary>
    private void ScheduleNameReparse(int deckId)
    {
        var key = $"metadata-edit:name-reparse:{deckId}";
        var now = DateTime.UtcNow;
        DateTime runAt;
        if (cache.TryGetValue<DateTime>(key, out var scheduledAt))
        {
            if (scheduledAt > now)
                return;
            runAt = scheduledAt + NameReparseDebounce;
        }
        else
            runAt = now.AddMinutes(2);

        jobs.Schedule<ReparseJob>(job => job.Reparse(deckId, false), runAt - now);
        cache.Set(key, runAt, runAt + NameReparseDebounce - now);
    }

    /// <summary>A pending edit whose every field already holds its proposed value has nothing left to do.</summary>
    private async Task SupersedePendingAsync(int deckId, CancellationToken ct, DeckMetadataSnapshot? current = null)
    {
        var pending = await db.MetadataEdits.Where(e => e.DeckId == deckId && e.Status == MetadataEditStatus.Pending).ToListAsync(ct);
        if (pending.Count == 0)
            return;

        current ??= await deckMetadata.LoadSnapshotAsync(deckId, ct);
        if (current == null)
            return;

        var changed = false;
        foreach (var edit in pending)
        {
            var changes = ParseChanges(edit.ChangesJson);
            if (changes.Count > 0 && changes.All(c => Merge(c.Field, c.Before, c.After, current.Value(c.Field), preferEdit: false) is { Changes: false, Clash: false }))
            {
                edit.Status = MetadataEditStatus.Superseded;
                edit.ReviewedAt = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>Logs a direct admin/dashboard write as an approved edit by that person.</summary>
    public async Task LogDirectEditAsync(int deckId, DeckMetadataSnapshot before, string? userId, CancellationToken ct = default)
    {
        var after = await deckMetadata.LoadSnapshotAsync(deckId, ct);
        if (after == null)
            return;
        var changes = DeckMetadataSnapshot.Diff(before, after);
        if (changes.Count == 0)
            return;

        db.MetadataEdits.Add(MetadataEditLog.Applied(deckId, changes, userId));
        await db.SaveChangesAsync(ct);
        await SupersedePendingAsync(deckId, ct, after);
    }

    public async Task<ServiceResult<EditableDeckDto>> GetEditableAsync(int deckId, string userId, CancellationToken ct)
    {
        var deck = await db.Decks.AsNoTracking()
                           .Where(d => d.DeckId == deckId)
                           .Select(d => new { d.DeckId, d.ParentDeckId, d.CoverName, d.MediaType, d.CharacterCount, d.ReleaseDate, d.FranchiseId })
                           .FirstOrDefaultAsync(ct);
        if (deck == null)
            return ServiceResult<EditableDeckDto>.Fail(404, "This deck doesn't exist.");
        if (deck.ParentDeckId != null)
            return ServiceResult<EditableDeckDto>.Fail(400, "Details are edited on the main deck.", new { parentDeckId = deck.ParentDeckId });

        var snapshot = (await deckMetadata.LoadSnapshotAsync(deckId, ct))!;
        var viewer = await trust.GetViewerAsync(userId, ct);
        var otherIds = snapshot.Relationships.Select(r => r.OtherDeckId).Distinct().ToList();
        var otherTypes = await db.Decks.AsNoTracking().Where(d => otherIds.Contains(d.DeckId))
                                 .Select(d => new { d.DeckId, d.MediaType }).ToDictionaryAsync(d => d.DeckId, d => (int)d.MediaType, ct);
        var historyCount = await db.MetadataEdits.CountAsync(e => e.DeckId == deckId && e.RevertsEditId == null, ct);
        var pending = await db.MetadataEdits.AsNoTracking()
                              .Where(e => e.DeckId == deckId && e.Status == MetadataEditStatus.Pending && e.Actor == MetadataEditActor.User &&
                                          e.RevertsEditId == null)
                              .OrderBy(e => e.CreatedAt)
                              .Select(e => new { e.Id, e.Fields, e.UserId, e.CreatedAt })
                              .ToListAsync(ct);

        return ServiceResult<EditableDeckDto>.Ok(new EditableDeckDto
        {
            DeckId = deckId,
            CoverName = deck.CoverName,
            MediaType = (int)deck.MediaType,
            CharacterCount = deck.CharacterCount,
            ReleaseYear = deck.ReleaseDate == default ? null : deck.ReleaseDate.Year,
            OriginalTitle = snapshot.OriginalTitle,
            RomajiTitle = snapshot.RomajiTitle,
            EnglishTitle = snapshot.EnglishTitle,
            Aliases = snapshot.Aliases,
            ReleaseDate = snapshot.ReleaseDate,
            Description = snapshot.Description,
            Genres = snapshot.Genres,
            Tags = snapshot.Tags.Select(t => new EditableTagDto(t.TagId, t.Name, t.Percentage)).OrderByDescending(t => t.Percentage).ToList(),
            Links = snapshot.Links.Select(l => new EditableLinkDto((int)l.LinkType, l.Url)).ToList(),
            Relationships = snapshot.Relationships
                                    .Select(r => new EditableRelationshipDto(r.SourceDeckId, r.TargetDeckId, (int)r.RelationshipType, r.OtherDeckId,
                                                                             r.OtherDeckTitle, otherTypes.GetValueOrDefault(r.OtherDeckId)))
                                    .ToList(),
            CharacterNames = snapshot.CharacterNames,
            Franchise = deck.FranchiseId is { } franchiseId ? await FranchiseRefAsync(franchiseId, ct) : null,
            HistoryCount = historyCount,
            Pending = pending.Select(p => new EditablePendingDto(p.Id, (int)p.Fields, p.UserId == userId, p.CreatedAt)).ToList(),
            Viewer = new EditableViewerDto(ViewerRole(viewer), !string.IsNullOrWhiteSpace(viewer?.DisplayName), viewer?.EmailConfirmed == true,
                                           viewer?.Banned == true)
        });
    }

    private async Task<FranchiseRefDto?> FranchiseRefAsync(int franchiseId, CancellationToken ct) =>
        await db.Franchises.AsNoTracking().Where(f => f.FranchiseId == franchiseId)
                .Select(f => new FranchiseRefDto(f.FranchiseId, f.OriginalTitle, f.RomajiTitle, f.EnglishTitle,
                                                 db.Decks.Count(d => d.FranchiseId == f.FranchiseId && d.ParentDeckId == null)))
                .FirstOrDefaultAsync(ct);

    public async Task<ServiceResult<FranchisePickDto>> FranchiseOfDeckAsync(int deckId, CancellationToken ct)
    {
        var deck = await db.Decks.AsNoTracking().Where(d => d.DeckId == deckId)
                           .Select(d => new { d.DeckId, d.FranchiseId, d.ParentDeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle })
                           .FirstOrDefaultAsync(ct);
        if (deck == null)
            return ServiceResult<FranchisePickDto>.Fail(404, "This deck doesn't exist.");
        if (deck.ParentDeckId != null)
            return ServiceResult<FranchisePickDto>.Fail(400, "Pick the main deck, not one of its parts.");
        if (deck.FranchiseId is { } id && await FranchiseRefAsync(id, ct) is { } franchise)
            return ServiceResult<FranchisePickDto>.Ok(new FranchisePickDto(franchise.FranchiseId, deck.DeckId, franchise.OriginalTitle,
                                                                           franchise.RomajiTitle, franchise.EnglishTitle, franchise.DeckCount));
        return ServiceResult<FranchisePickDto>.Ok(new FranchisePickDto(null, deck.DeckId, deck.OriginalTitle, deck.RomajiTitle, deck.EnglishTitle, 1));
    }

    /// <summary>A franchise the edit starts only gets its id when applied; the recorded change keeps that id so a revert finds it.</summary>
    private async Task RecordStartedFranchiseAsync(int deckId, List<MetadataFieldChange> applied, CancellationToken ct)
    {
        var change = applied.FirstOrDefault(c => c.Field == MetadataField.Franchise && IsUnresolvedFranchise(c.After));
        if (change == null)
            return;

        await db.SaveChangesAsync(ct);
        change.After = (await deckMetadata.LoadSnapshotAsync(deckId, ct))!.Value(MetadataField.Franchise);
    }

    public static string ViewerRole(MetadataViewer? viewer) =>
        viewer == null ? "user" : viewer.IsEditor ? "editor" : viewer.IsReviewer ? "reviewer" : "user";

    private static (int Status, string Message)? SubmitBlockedReason(MetadataViewer? viewer) => viewer switch
    {
        null => (401, "Sign in to suggest edits."),
        { Banned: true } => (403, BannedMessage),
        _ when string.IsNullOrWhiteSpace(viewer.DisplayName) => (400, "Set a display name before suggesting edits."),
        { EmailConfirmed: false } => (400, "Confirm your email address before suggesting edits."),
        _ => null
    };

    /// <summary>Contributors only learn whether a name is in the text; exact counts would let anyone probe the stored text.</summary>
    public async Task<ServiceResult<List<CharacterNameCheckDto>>> CheckNamesAsync(int deckId, string userId, List<string>? names, CancellationToken ct)
    {
        var viewer = await trust.GetViewerAsync(userId, ct);
        if (SubmitBlockedReason(viewer) is { } blocked)
            return ServiceResult<List<CharacterNameCheckDto>>.Fail(blocked.Status, blocked.Message);
        if (names is not { Count: > 0 })
            return ServiceResult<List<CharacterNameCheckDto>>.Ok([]);

        var existing = await db.DeckDictionaryEntries.AsNoTracking()
                               .Where(e => e.DeckId == deckId && e.EntryType == DeckDictionaryEntryType.Name)
                               .Select(e => e.Surface).ToListAsync(ct);
        var results = await nameCheckService.CheckManyAsync(deckId, existing, names.Take(10).ToList(), ct);
        return ServiceResult<List<CharacterNameCheckDto>>.Ok(results.Select(r => ToDto(r, viewer!.IsEditor)).ToList());
    }

    public static CharacterNameCheckDto ToDto(CharacterNameCheckResult r, bool showCounts) =>
        new(r.Input, r.Error, r.Surfaces.Select(s => ToDto(s, showCounts, null)).ToList());

    public static CharacterNameSurfaceDto ToDto(CharacterNameSurfaceCheck s, bool showCounts, List<string>? examples) =>
        new(s.Surface, showCounts ? s.Occurrences : null, s.Occurrences > 0, s.Status switch
        {
            CharacterNameSurfaceStatus.Ok => "ok",
            CharacterNameSurfaceStatus.Flagged => "flagged",
            CharacterNameSurfaceStatus.NotInText => "notInText",
            CharacterNameSurfaceStatus.Risky => "risky",
            _ => "duplicate"
        }, s.Detail, examples);

    public async Task<ServiceResult<SubmitMetadataEditResult>> SubmitAsync(int deckId, string userId, string? ip, SubmitMetadataEditRequest request,
                                                                           CancellationToken ct)
    {
        static ServiceResult<SubmitMetadataEditResult> Bad(string message) => ServiceResult<SubmitMetadataEditResult>.Fail(400, message);

        if (SubmitBlockedReason(await trust.GetViewerAsync(userId, ct)) is { } notAllowed)
            return ServiceResult<SubmitMetadataEditResult>.Fail(notAllowed.Status, notAllowed.Message);

        var deckInfo = await db.Decks.AsNoTracking().Where(d => d.DeckId == deckId)
                               .Select(d => new { d.ParentDeckId }).FirstOrDefaultAsync(ct);
        if (deckInfo == null)
            return ServiceResult<SubmitMetadataEditResult>.Fail(404, "This deck doesn't exist.");
        if (deckInfo.ParentDeckId != null)
            return ServiceResult<SubmitMetadataEditResult>.Fail(400, "Details are edited on the main deck.", new { parentDeckId = deckInfo.ParentDeckId });

        var record = (await trust.GetAsync(userId, ct))!;
        if (await RateLimitMessageAsync(userId, record, ct) is { } limit)
            return ServiceResult<SubmitMetadataEditResult>.Fail(429, limit);

        var before = (await deckMetadata.LoadSnapshotAsync(deckId, ct))!;
        var built = await BuildAfterAsync(deckId, before, request, ct);
        if (built.Error != null)
            return Bad(built.Error);
        if (built.Conflicts is { Count: > 0 } changedSince)
            return ServiceResult<SubmitMetadataEditResult>.Fail(409,
                $"Someone changed the {JoinLabels(changedSince.Select(c => c.Field))} while you were editing. Check the new value, then submit again to replace it.",
                new { changedFields = changedSince.Select(c => new FieldValueDto((int)c.Field, c.Current)).ToList() });
        var after = built.Value!;

        var changes = DeckMetadataSnapshot.Diff(before, after);
        if (changes.Count == 0)
            return Bad("Nothing changed.");

        var sources = (request.Sources ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Select(NormaliseUrl).Distinct().ToList();
        if (sources.Count > MaxSources)
            return Bad($"Add at most {MaxSources} sources.");
        if (sources.Any(s => s.Length > 500 || !IsHttpUrl(s)))
            return Bad("Sources need to be links starting with http:// or https://.");

        var note = request.Note?.Trim();
        if (note is { Length: > MaxNoteLength })
            return Bad($"Keep the note under {MaxNoteLength} characters.");
        var needsNote = changes.Where(c => c.Field is MetadataField.OriginalTitle or MetadataField.Description or MetadataField.MediaType).ToList();
        if (needsNote.Count > 0 && string.IsNullOrEmpty(note))
            return Bad($"Add a note explaining the change to {JoinLabels(needsNote.Select(c => c.Field))}.");

        if (!record.IsEditor)
        {
            var cooldownFrom = DateTime.UtcNow - RevertedFieldCooldown;
            var recentlyReverted = await (from e in db.MetadataEdits.AsNoTracking()
                                          join r in db.MetadataEdits.AsNoTracking() on e.Id equals r.RevertsEditId
                                          where e.UserId == userId && e.DeckId == deckId && e.Status == MetadataEditStatus.Reverted &&
                                                e.ReviewedAt > cooldownFrom
                                          select r.Fields).ToListAsync(ct);
            var blocked = MetadataFields.Of(recentlyReverted);
            var hit = changes.Where(c => blocked.HasFlag(c.Field)).Select(c => c.Field).ToList();
            if (hit.Count > 0)
                return Bad($"Your recent change to {JoinLabels(hit)} on this deck was reverted, so you can't edit it again for a few days.");
        }

        var maxTier = await trust.AutoApplyMaxTierAsync(record, ct);
        var live = changes.Where(c => (int)EffectiveTier(c) <= maxTier).ToList();
        var pending = changes.Where(c => (int)EffectiveTier(c) > maxTier).ToList();

        var submissionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var rows = new List<MetadataEdit>();
        MetadataEdit NewRow(List<MetadataFieldChange> part, MetadataEditStatus status) => new()
        {
            DeckId = deckId, Actor = MetadataEditActor.User, UserId = userId, SubmissionId = submissionId, Status = status,
            Fields = MetadataFields.Of(part), ChangesJson = SerializeChanges(part),
            SourcesJson = JsonSerializer.Serialize(sources, DeckMetadataSnapshot.Json), EditNote = string.IsNullOrEmpty(note) ? null : note,
            CreatedAt = now, IpAddress = ip
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (live.Count > 0)
        {
            var deck = (await deckMetadata.LoadForPatchAsync(deckId, ct))!;
            var error = await ApplyValuesAsync(deck, live.Select(c => (c.Field, c.After)), userId, ct);
            if (error != null)
                return Bad(error);
            await RecordStartedFranchiseAsync(deckId, live, ct);

            var row = NewRow(live, record.IsEditor ? MetadataEditStatus.Approved : MetadataEditStatus.Applied);
            row.AppliedAt = now;
            if (record.IsEditor)
            {
                row.ReviewedAt = now;
                row.ReviewedByUserId = userId;
            }
            rows.Add(row);
        }

        if (pending.Count > 0)
            rows.Add(NewRow(pending, MetadataEditStatus.Pending));

        db.MetadataEdits.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var liveFields = MetadataFields.Of(live);
        if (liveFields != MetadataField.None)
            await AfterApplyAsync(deckId, liveFields, ct);

        logger.LogInformation("Metadata edit submitted: Deck={DeckId} User={UserId} Live={Live} Pending={Pending}", deckId, userId, liveFields,
                              MetadataFields.Of(pending));

        return ServiceResult<SubmitMetadataEditResult>.Ok(new SubmitMetadataEditResult(
            submissionId, live.Select(c => (int)c.Field).ToList(), pending.Select(c => (int)c.Field).ToList(), rows.Select(r => r.Id).ToList()));
    }

    private async Task<string?> RateLimitMessageAsync(string userId, MetadataContributorRecord record, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);
        var recent = await db.MetadataEdits.AsNoTracking()
                             .Where(e => e.UserId == userId && e.Actor == MetadataEditActor.User && e.RevertsEditId == null && e.CreatedAt > dayAgo)
                             .GroupBy(e => e.SubmissionId)
                             .Select(g => g.Min(e => e.CreatedAt))
                             .ToListAsync(ct);

        var hourly = recent.Where(t => t > hourAgo).Order().ToList();
        var hourLimit = record.Level switch
        {
            MetadataTrustLevel.Editor => EditorHourlyLimit,
            MetadataTrustLevel.New => NewHourlyLimit,
            _ => EstablishedHourlyLimit
        };
        if (hourly.Count >= hourLimit)
            return WaitMessage(hourly[hourly.Count - hourLimit].AddHours(1) - now);
        if (record.Level == MetadataTrustLevel.New && recent.Count >= NewDailyLimit)
            return WaitMessage(recent.Order().ElementAt(recent.Count - NewDailyLimit).AddDays(1) - now);
        return null;
    }

    private static string WaitMessage(TimeSpan wait)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        var when = minutes < 120 ? $"{minutes} minutes" : $"{(int)Math.Ceiling(wait.TotalHours)} hours";
        return $"You've submitted a lot of edits recently. Try again in {when}.";
    }

    /// <summary>Adds https:// to a bare "vndb.org/v1" and strips tracking parameters.</summary>
    public static string NormaliseUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && !trimmed.Contains("://") && trimmed.Contains('.') && !trimmed.Contains(' '))
            trimmed = "https://" + trimmed;
        return LinkUrlCleaner.Clean(trimmed);
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.Host.Contains('.');

    public static string Label(MetadataField field) => field switch
    {
        MetadataField.OriginalTitle => "original title",
        MetadataField.RomajiTitle => "romaji title",
        MetadataField.EnglishTitle => "English title",
        MetadataField.Aliases => "aliases",
        MetadataField.ReleaseDate => "release date",
        MetadataField.Description => "description",
        MetadataField.MediaType => "media type",
        MetadataField.Genres => "genres",
        MetadataField.Tags => "tags",
        MetadataField.Links => "external links",
        MetadataField.Relationships => "related decks",
        MetadataField.CharacterNames => "character names",
        MetadataField.Franchise => "franchise",
        _ => field.ToString()
    };

    private static string JoinLabels(IEnumerable<MetadataField> fields)
    {
        var labels = fields.Select(Label).ToList();
        return labels.Count <= 1 ? string.Concat(labels) : string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels[^1];
    }

    private sealed record Built(DeckMetadataSnapshot? Value, string? Error, List<(MetadataField Field, JsonElement Current)>? Conflicts = null);

    /// <summary>Replays the form's change (what it loaded to what it sends) onto the current list, so an item someone added meanwhile survives.</summary>
    private static List<T> MergeOntoCurrent<T>(MetadataField field, object? loaded, List<T> requested, DeckMetadataSnapshot current) =>
        loaded == null
            ? requested
            : Merge(field, DeckMetadataSnapshot.ToElement(loaded), DeckMetadataSnapshot.ToElement(requested), current.Value(field), preferEdit: true)
              .Value.Deserialize<List<T>>(DeckMetadataSnapshot.Json) ?? [];

    /// <summary>Applies the request to a copy of the current state, enforcing the community-only limits the dashboard doesn't have.</summary>
    private async Task<Built> BuildAfterAsync(int deckId, DeckMetadataSnapshot before, SubmitMetadataEditRequest r, CancellationToken ct)
    {
        static Built Fail(string message) => new(null, message);
        static string Clean(string s) => ControlChars().Replace(s, "").Trim();

        var originalTitle = before.OriginalTitle;
        if (r.OriginalTitle != null)
        {
            originalTitle = Clean(r.OriginalTitle);
            if (originalTitle.Length == 0)
                return Fail("The original title can't be empty.");
            if (originalTitle.Length > MaxTitleLength)
                return Fail($"Keep titles under {MaxTitleLength} characters.");
        }

        string? Optional(string? value, string? current) => value == null ? current : Clean(value) is { Length: > 0 } v ? v : null;
        var romaji = Optional(r.RomajiTitle, before.RomajiTitle);
        var english = Optional(r.EnglishTitle, before.EnglishTitle);
        if ((romaji?.Length ?? 0) > MaxTitleLength || (english?.Length ?? 0) > MaxTitleLength)
            return Fail($"Keep titles under {MaxTitleLength} characters.");

        var description = r.Description == null ? before.Description : Clean(r.Description) is { Length: > 0 } d ? d : null;
        if ((description?.Length ?? 0) > 2000)
            return Fail("Keep the description under 2000 characters.");

        var releaseDate = before.ReleaseDate;
        if (r.ReleaseDate != null)
        {
            var parsed = DeckMetadataService.ParseDate(r.ReleaseDate);
            if (parsed == null)
                return Fail("Use a valid release date.");
            if (parsed.Value != default && (parsed.Value.Year < 1800 || parsed.Value > DateOnly.FromDateTime(DateTime.UtcNow).AddYears(5)))
                return Fail("That release date doesn't look right.");
            releaseDate = DeckMetadataSnapshot.FormatDate(parsed.Value);
        }

        var mediaType = before.MediaType;
        if (r.MediaType.HasValue)
        {
            if (!Enum.IsDefined(r.MediaType.Value))
                return Fail("Pick a media type from the list.");
            mediaType = r.MediaType.Value;
        }

        var aliases = before.Aliases;
        if (r.Aliases != null)
        {
            static List<string> CleanAliases(IEnumerable<string> values) =>
                values.Select(Clean).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var requested = CleanAliases(r.Aliases);
            if (requested.Count > MaxListItems)
                return Fail($"A deck can have at most {MaxAliases} aliases.");
            if (requested.Any(a => a.Length > MaxAliasLength))
                return Fail($"Keep aliases under {MaxAliasLength} characters.");
            if (requested.Any(a => UrlLike().IsMatch(a)))
                return Fail("Aliases can't contain links.");
            aliases = MergeOntoCurrent(MetadataField.Aliases, r.Base?.Aliases is { } loaded ? CleanAliases(loaded) : null, requested, before);
            if (AddsPastAliasCap(before.Aliases, aliases))
                return Fail($"A deck can have at most {MaxAliases} aliases. Remove one before adding another.");
        }

        var genres = before.Genres;
        if (r.Genres != null)
        {
            if (r.Genres.Count > MaxListItems || r.Genres.Any(g => !Enum.IsDefined(typeof(Genre), g)))
                return Fail("Pick genres from the list.");
            genres = MergeOntoCurrent(MetadataField.Genres, r.Base?.Genres?.Distinct().Order().ToList(), r.Genres.Distinct().Order().ToList(), before);
        }

        var tags = before.Tags;
        if (r.Tags != null)
        {
            if (r.Tags.Count > MaxListItems)
                return Fail("That's too many tags for one deck.");
            if (r.Tags.Any(t => t.Percentage is < 0 or > 100))
                return Fail("Tag percentages go from 0 to 100.");
            var ids = r.Tags.Select(t => t.TagId).Distinct().ToList();
            var names = await db.Tags.AsNoTracking().Where(t => ids.Contains(t.TagId)).ToDictionaryAsync(t => t.TagId, t => t.Name, ct);
            if (names.Count != ids.Count)
                return Fail("Pick tags from the list.");
            var requested = r.Tags.GroupBy(t => t.TagId).Select(g => new SnapshotTag(g.Key, names[g.Key], g.Last().Percentage)).OrderBy(t => t.TagId).ToList();
            var loaded = r.Base?.Tags?.GroupBy(t => t.TagId).Select(g => new SnapshotTag(g.Key, "", g.Last().Percentage)).OrderBy(t => t.TagId).ToList();
            tags = MergeOntoCurrent(MetadataField.Tags, loaded, requested, before);
        }

        var links = before.Links;
        if (r.Links != null)
        {
            if (r.Links.Count > MaxListItems)
                return Fail("That's too many links for one deck.");
            var cleaned = r.Links.Where(l => !string.IsNullOrWhiteSpace(l.Url)).Select(l => new SnapshotLink(l.LinkType, NormaliseUrl(l.Url))).Distinct().ToList();
            foreach (var link in cleaned)
            {
                if (!Enum.IsDefined(link.LinkType))
                    return Fail("Pick a link type from the list.");
                if (link.Url.Length > 2048 || !IsHttpUrl(link.Url))
                    return Fail("Links must start with http:// or https://.");
                if (link.LinkType != LinkType.Web && ExternalUrlParser.DetectLinkType(new Uri(link.Url)) != link.LinkType)
                    return Fail(link.LinkType == LinkType.GoogleBooks
                                    ? "Google Books links must point to a book page."
                                    : $"{LinkTypeLabel(link.LinkType)} links must point to {LinkHostLabel(link.LinkType)}.");
            }

            var duplicateType = cleaned.Where(l => l.LinkType != LinkType.Web).GroupBy(l => l.LinkType).FirstOrDefault(g => g.Count() > 1);
            if (duplicateType != null)
                return Fail($"Add only one {LinkTypeLabel(duplicateType.Key)} link.");
            var requested = cleaned.OrderBy(l => l.LinkType).ThenBy(l => l.Url, StringComparer.Ordinal).ToList();
            var loaded = r.Base?.Links?.Where(l => !string.IsNullOrWhiteSpace(l.Url)).Select(l => new SnapshotLink(l.LinkType, l.Url.Trim())).Distinct().ToList();
            links = MergeOntoCurrent(MetadataField.Links, loaded, requested, before);
        }

        var relationships = before.Relationships;
        if (r.Relationships != null)
        {
            if (r.Relationships.Count > MaxListItems)
                return Fail("That's too many related decks for one edit.");
            foreach (var edge in r.Relationships)
            {
                if (!DeckRelationship.IsPrimaryRelationship(edge.RelationshipType) || DeckRelationship.IsLegacyGroupType(edge.RelationshipType))
                    return Fail("Pick a relationship type from the list.");
                if (DeckRelationship.ValidateEdge(edge.SourceDeckId, edge.TargetDeckId, edge.RelationshipType) is { } edgeError)
                    return Fail(edgeError);
                if (edge.SourceDeckId != deckId && edge.TargetDeckId != deckId)
                    return Fail("Every relationship must include this deck.");
            }

            var otherIds = r.Relationships.Select(e => e.SourceDeckId == deckId ? e.TargetDeckId : e.SourceDeckId).Distinct().ToList();
            var others = await db.Decks.AsNoTracking().Where(d => otherIds.Contains(d.DeckId))
                                 .Select(d => new { d.DeckId, d.OriginalTitle, d.ParentDeckId }).ToDictionaryAsync(d => d.DeckId, ct);
            if (others.Count != otherIds.Count)
                return Fail("One of the related decks doesn't exist.");
            var requested = r.Relationships
                             .Select(e => (e.SourceDeckId, e.TargetDeckId, e.RelationshipType))
                             .Distinct()
                             .Select(e =>
                             {
                                 var other = e.SourceDeckId == deckId ? e.TargetDeckId : e.SourceDeckId;
                                 return new SnapshotRelationship(e.SourceDeckId, e.TargetDeckId, e.RelationshipType, other, others[other].OriginalTitle);
                             })
                             .ToList();
            var loaded = r.Base?.Relationships?
                          .Select(e => new SnapshotRelationship(e.SourceDeckId, e.TargetDeckId, e.RelationshipType,
                                                                e.SourceDeckId == deckId ? e.TargetDeckId : e.SourceDeckId, ""))
                          .ToList();
            relationships = MergeOntoCurrent(MetadataField.Relationships, loaded, requested, before)
                            .OrderBy(e => e.SourceDeckId).ThenBy(e => e.TargetDeckId).ThenBy(e => e.RelationshipType)
                            .ToList();
        }

        var characterNames = before.CharacterNames;
        if (r.AddCharacterNames is { Count: > 0 } || r.RemoveCharacterNames is { Count: > 0 })
        {
            var removed = (r.RemoveCharacterNames ?? []).Select(n => n.Trim()).ToHashSet(StringComparer.Ordinal);
            var existing = before.CharacterNames.Where(n => !removed.Contains(n)).ToList();

            var added = new List<string>();
            var toAdd = (r.AddCharacterNames ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();
            if (toAdd.Count > MaxAddedCharacterNames)
                return Fail($"Add at most {MaxAddedCharacterNames} names at once.");
            if (toAdd.Count > 0)
            {
                var results = await nameCheckService.CheckManyAsync(deckId, existing, toAdd, ct);
                if (results.FirstOrDefault(x => x.Error != null) is { } failed)
                    return Fail($"{failed.Input}: {failed.Error}");
                added = results.SelectMany(x => x.Kept).Distinct().Where(s => !existing.Contains(s)).ToList();
            }

            characterNames = existing.Concat(added).Distinct().Order(StringComparer.Ordinal).ToList();
        }

        var franchise = before.Franchise;
        if (r.FranchiseDeckId is { } franchiseDeckId)
        {
            var derived = await db.Decks.AsNoTracking().Where(d => d.DeckId == deckId).Select(d => d.FranchiseId).FirstAsync(ct);
            if (derived != null || before.Franchise != null)
                return Fail("This deck is already in a franchise.");
            if (franchiseDeckId == deckId)
                return Fail("Pick another deck from the franchise.");
            var pick = await FranchiseOfDeckAsync(franchiseDeckId, ct);
            if (!pick.IsOk)
                return Fail(pick.Error!);
            var p = pick.Value!;
            franchise = new SnapshotFranchise(p.FranchiseId, p.OriginalTitle, p.RomajiTitle, p.EnglishTitle, p.FranchiseId == null ? p.DeckId : null);
        }

        var after = new DeckMetadataSnapshot
                    {
                        OriginalTitle = originalTitle, RomajiTitle = romaji, EnglishTitle = english, Description = description,
                        ReleaseDate = releaseDate, MediaType = mediaType, Aliases = aliases, Genres = genres, Tags = tags, Links = links,
                        Relationships = relationships, CharacterNames = characterNames, Franchise = franchise
                    };
        return new Built(after, null, ScalarConflicts(before, after, r));
    }

    /// <summary>Removals are always allowed, so a deck imported with more than the cap can still be cleaned up; only additions are capped.</summary>
    public static bool AddsPastAliasCap(IReadOnlyCollection<string> current, IReadOnlyCollection<string> after) =>
        after.Count > MaxAliases && after.Any(a => !current.Contains(a));

    /// <summary>Scalars the form changed whose value moved after the form loaded; submitting would overwrite a change the contributor never saw.</summary>
    private static List<(MetadataField Field, JsonElement Current)> ScalarConflicts(DeckMetadataSnapshot current, DeckMetadataSnapshot after,
                                                                                   SubmitMetadataEditRequest r)
    {
        var conflicts = new List<(MetadataField, JsonElement)>();
        if (r.Base == null)
            return conflicts;

        static JsonElement Text(string value) => DeckMetadataSnapshot.ToElement(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        var loaded = new List<(MetadataField Field, bool Sent, JsonElement? Value)>
        {
            (MetadataField.OriginalTitle, r.OriginalTitle != null, r.Base.OriginalTitle == null ? null : Text(r.Base.OriginalTitle)),
            (MetadataField.RomajiTitle, r.RomajiTitle != null, r.Base.RomajiTitle == null ? null : Text(r.Base.RomajiTitle)),
            (MetadataField.EnglishTitle, r.EnglishTitle != null, r.Base.EnglishTitle == null ? null : Text(r.Base.EnglishTitle)),
            (MetadataField.Description, r.Description != null, r.Base.Description == null ? null : Text(r.Base.Description)),
            (MetadataField.ReleaseDate, r.ReleaseDate != null, r.Base.ReleaseDate == null ? null : Text(r.Base.ReleaseDate)),
            (MetadataField.MediaType, r.MediaType.HasValue, r.Base.MediaType.HasValue ? DeckMetadataSnapshot.ToElement((int)r.Base.MediaType.Value) : null)
        };

        foreach (var (field, sent, value) in loaded)
        {
            if (!sent || value == null)
                continue;
            var now = current.Value(field);
            if (!DeckMetadataSnapshot.KeyEquals(field, value.Value, now) && !DeckMetadataSnapshot.KeyEquals(field, after.Value(field), now))
                conflicts.Add((field, now));
        }

        return conflicts;
    }

    private static string LinkHostLabel(LinkType type) => type switch
    {
        LinkType.Vndb => "vndb.org",
        LinkType.Anilist => "anilist.co",
        LinkType.Mal => "myanimelist.net",
        LinkType.Tmdb => "themoviedb.org",
        LinkType.Imdb => "imdb.com",
        LinkType.Igdb => "igdb.com",
        LinkType.Syosetsu => "syosetu.com",
        LinkType.Kakuyomu => "kakuyomu.jp",
        LinkType.Bookmeter => "bookmeter.com",
        LinkType.Amazon => "amazon.co.jp",
        LinkType.YouTube => "youtube.com",
        _ => "its own site"
    };

    private static string LinkTypeLabel(LinkType type) => type switch
    {
        LinkType.Vndb => "VNDB",
        LinkType.Anilist => "AniList",
        LinkType.Mal => "MyAnimeList",
        LinkType.Tmdb => "TMDB",
        LinkType.Imdb => "IMDb",
        LinkType.Igdb => "IGDB",
        LinkType.GoogleBooks => "Google Books",
        LinkType.Syosetsu => "Syosetsu",
        LinkType.Kakuyomu => "Kakuyomu",
        LinkType.Bookmeter => "Bookmeter",
        LinkType.Amazon => "Amazon",
        LinkType.YouTube => "YouTube",
        _ => "Website"
    };
}
