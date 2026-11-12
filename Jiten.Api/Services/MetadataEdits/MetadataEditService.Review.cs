using System.Text.Json;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services.MetadataEdits;

public sealed partial class MetadataEditService
{
    private static bool CanActOn(MetadataViewer reviewer, List<MetadataFieldChange> changes) =>
        reviewer.IsEditor || (reviewer.IsReviewer && MaxTier(changes) == MetadataRiskTier.Low);

    private sealed record Actionable(MetadataEdit Edit, List<MetadataFieldChange> Changes, ServiceResult<object>? Error);

    /// <summary>A pending edit the reviewer may decide on; never their own, and reviewers only get low-risk edits.</summary>
    private async Task<Actionable> LoadActionableAsync(long editId, string reviewerId, string verb, CancellationToken ct)
    {
        var reviewer = await trust.GetViewerAsync(reviewerId, ct);
        var edit = await db.MetadataEdits.FirstOrDefaultAsync(e => e.Id == editId, ct);
        if (edit == null)
            return new Actionable(null!, [], ServiceResult<object>.Fail(404, "This edit doesn't exist."));
        if (edit.Status != MetadataEditStatus.Pending)
            return new Actionable(null!, [], ServiceResult<object>.Fail(409, "This edit was already handled."));
        var changes = ParseChanges(edit.ChangesJson);
        if (reviewer == null || !CanActOn(reviewer, changes) || edit.UserId == reviewerId)
            return new Actionable(null!, [], ServiceResult<object>.Fail(403, $"You can't {verb} this edit."));
        return new Actionable(edit, changes, null);
    }

    private static (List<string> Before, List<string> After) NameLists(MetadataFieldChange change) =>
        (change.Before.Deserialize<List<string>>(DeckMetadataSnapshot.Json) ?? [], change.After.Deserialize<List<string>>(DeckMetadataSnapshot.Json) ?? []);

    private static (List<string> Before, List<string> Added) AddedNames(MetadataFieldChange change)
    {
        var (before, after) = NameLists(change);
        return (before, after.Where(n => !before.Contains(n)).ToList());
    }

    private static string DeckHistoryUrl(int deckId) => $"/decks/media/{deckId}/history";

    /// <summary>A reviewer can take back a rejection this long after making it; the author's notification is withdrawn with it.</summary>
    public static readonly TimeSpan RejectUndoWindow = TimeSpan.FromMinutes(2);

    /// <summary>Moves the status in one conditional UPDATE, so two reviewers or a double click can never both act on the same edit.</summary>
    private async Task<bool> ClaimAsync(long editId, MetadataEditStatus to, CancellationToken ct, params MetadataEditStatus[] from) =>
        await db.MetadataEdits.Where(e => e.Id == editId && from.Contains(e.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, to), ct) == 1;

    private static ServiceResult<T> AlreadyHandled<T>() => ServiceResult<T>.Fail(409, "This edit was already handled.");

    private async Task<string> DeckTitleAsync(int deckId, CancellationToken ct) =>
        await db.Decks.AsNoTracking().Where(d => d.DeckId == deckId).Select(d => d.OriginalTitle).FirstOrDefaultAsync(ct) ?? "a deck";

    /// <summary>Fields where the edit and a later change disagree; list fields only clash on a tag percentage or a single-URL link.</summary>
    public static List<ConflictDto> Conflicts(DeckMetadataSnapshot current, IEnumerable<MetadataFieldChange> changes) =>
        changes.Where(c => Merge(c.Field, c.Before, c.After, current.Value(c.Field), preferEdit: false).Clash)
               .Select(c => new ConflictDto((int)c.Field, c.Before, current.Value(c.Field), c.After))
               .ToList();

    /// <summary>On a list field, "keep current" only keeps the clashing items; the edit's other additions and removals still apply.</summary>
    public async Task<ServiceResult<object>> ApproveAsync(long editId, string reviewerId, ApproveMetadataEditRequest request, CancellationToken ct)
    {
        var (edit, changes, failure) = await LoadActionableAsync(editId, reviewerId, "approve", ct);
        if (failure != null)
            return failure;

        var excludedFlags = MetadataFields.Of((request.ExcludeFields ?? []).Select(f => (MetadataField)f));
        var leftOut = changes.Where(c => excludedFlags.HasFlag(c.Field)).ToList();
        changes = changes.Except(leftOut).ToList();

        var partlyLeftOut = new List<MetadataField>();
        foreach (var group in (request.ExcludeItems ?? []).GroupBy(g => g.Field))
        {
            var change = changes.FirstOrDefault(c => c.Field == group.Key);
            if (change == null || !IsListField(change.Field))
                continue;

            var keys = group.SelectMany(g => g.Keys).ToHashSet(StringComparer.Ordinal);
            var (kept, rejected) = SplitListChange(change.Field, change.Before, change.After, keys);
            if (DeckMetadataSnapshot.KeyEquals(change.Field, rejected, change.Before))
                continue;

            if (DeckMetadataSnapshot.KeyEquals(change.Field, kept, change.Before))
            {
                changes.Remove(change);
                leftOut.Add(change);
                continue;
            }

            leftOut.Add(new MetadataFieldChange { Field = change.Field, Before = change.Before, After = rejected });
            change.After = kept;
            partlyLeftOut.Add(change.Field);
        }

        var excludeNote = request.ExcludeNote?.Trim() ?? "";
        if (leftOut.Count > 0)
        {
            if (excludeNote.Length == 0)
                return ServiceResult<object>.Fail(400, "Say why the changes you left out weren't approved.");
            if (excludeNote.Length > MaxNoteLength)
                return ServiceResult<object>.Fail(400, $"Keep the reason under {MaxNoteLength} characters.");
            if (changes.Count == 0)
                return ServiceResult<object>.Fail(400, "Leave at least one change in, or reject the edit.");
        }

        var leftOutText = LeftOutPhrase(leftOut, partlyLeftOut);

        var (move, moveError) = await FranchiseMoveAsync(edit.DeckId, changes, ct);
        if (moveError != null)
            return ServiceResult<object>.Fail(409, moveError);

        var current = (await deckMetadata.LoadSnapshotAsync(edit.DeckId, ct))!;
        var conflicts = Conflicts(current, changes);
        var resolutions = (request.Resolutions ?? []).ToDictionary(r => r.Field, r => r.Keep);
        var unresolved = conflicts.Where(c => !resolutions.ContainsKey((MetadataField)c.Field)).ToList();
        if (unresolved.Count > 0)
            return ServiceResult<object>.Fail(409, "Some fields changed after this edit was written. Pick which value to keep.", new { conflicts = unresolved });

        var toApply = changes.Where(c => IsListField(c.Field) || !(resolutions.TryGetValue(c.Field, out var keep) && keep == "current"))
                             .Select(c => (c.Field, Merge: Merge(c.Field, c.Before, c.After, current.Value(c.Field),
                                                                 preferEdit: resolutions.GetValueOrDefault(c.Field) == "edit")))
                             .Where(m => m.Merge.Changes)
                             .ToList();

        var aliasMerge = toApply.FirstOrDefault(m => m.Field == MetadataField.Aliases).Merge;
        if (aliasMerge != null && AddsPastAliasCap(current.Aliases, aliasMerge.Value.Deserialize<List<string>>(DeckMetadataSnapshot.Json) ?? []))
            return ServiceResult<object>.Fail(400, $"This deck now has {current.Aliases.Count} aliases, so this edit would go over the limit of {MaxAliases}. Leave the aliases out or reject it.");

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await ClaimAsync(editId, MetadataEditStatus.Approved, ct, MetadataEditStatus.Pending))
            return AlreadyHandled<object>();

        edit.ReviewedAt = now;
        edit.ReviewedByUserId = reviewerId;
        edit.ChangesJson = SerializeChanges(changes);
        edit.Fields = MetadataFields.Of(changes);
        if (leftOut.Count > 0)
            db.MetadataEdits.Add(new MetadataEdit
                                 {
                                     DeckId = edit.DeckId, Actor = edit.Actor, UserId = edit.UserId, SubmissionId = edit.SubmissionId,
                                     Status = MetadataEditStatus.Rejected, Fields = MetadataFields.Of(leftOut),
                                     ChangesJson = SerializeChanges(leftOut), SourcesJson = edit.SourcesJson, EditNote = edit.EditNote,
                                     CreatedAt = edit.CreatedAt, ReviewedAt = now, ReviewedByUserId = reviewerId,
                                     ReviewNote = excludeNote, IpAddress = edit.IpAddress
                                 });

        if (move != null)
        {
            var picked = (await deckMetadata.LoadForPatchAsync(move.DeckId, ct))!;
            var pickedError = await ApplyValuesAsync(picked, [(MetadataField.Franchise, move.Change.After)], edit.UserId, ct);
            if (pickedError != null)
                return ServiceResult<object>.Fail(400, pickedError);

            if (toApply.Count == 0)
            {
                edit.DeckId = move.DeckId;
                edit.ChangesJson = SerializeChanges([move.Change]);
                edit.Fields = MetadataField.Franchise;
                edit.Status = MetadataEditStatus.Approved;
                edit.AppliedAt = now;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                await AfterApplyAsync(move.DeckId, MetadataField.Franchise, ct);
                if (edit.UserId != null)
                    await NotifyApprovedAsync(edit.UserId, move.DeckId, leftOutText, excludeNote, ct);
                return ServiceResult<object>.Ok(new { status = (int)edit.Status });
            }

            db.MetadataEdits.Add(new MetadataEdit
                                 {
                                     DeckId = move.DeckId, Actor = edit.Actor, UserId = edit.UserId, SubmissionId = edit.SubmissionId,
                                     Status = MetadataEditStatus.Approved, Fields = MetadataField.Franchise, ChangesJson = SerializeChanges([move.Change]),
                                     SourcesJson = edit.SourcesJson, EditNote = edit.EditNote, CreatedAt = edit.CreatedAt, AppliedAt = now,
                                     ReviewedAt = now, ReviewedByUserId = reviewerId, IpAddress = edit.IpAddress
                                 });
        }

        if (toApply.Count == 0)
        {
            edit.Status = MetadataEditStatus.Superseded;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ServiceResult<object>.Ok(new { status = (int)edit.Status });
        }

        var deck = (await deckMetadata.LoadForPatchAsync(edit.DeckId, ct))!;
        var error = await ApplyValuesAsync(deck, toApply.Select(m => (m.Field, m.Merge.Value)), edit.UserId, ct);
        if (error != null)
            return ServiceResult<object>.Fail(400, error);

        edit.Status = MetadataEditStatus.Approved;
        edit.AppliedAt = now;
        await RecordStartedFranchiseAsync(edit.DeckId, changes, ct);
        edit.ChangesJson = SerializeChanges(changes);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await AfterApplyAsync(edit.DeckId, MetadataFields.Of(toApply.Select(m => m.Field)), ct);
        if (move != null)
            await AfterApplyAsync(move.DeckId, MetadataField.Franchise, ct);

        if (edit.UserId != null)
            await NotifyApprovedAsync(edit.UserId, edit.DeckId, leftOutText, excludeNote, ct);

        return ServiceResult<object>.Ok(new { status = (int)edit.Status });
    }

    private sealed record FranchiseMove(int DeckId, MetadataFieldChange Change);

    /// <summary>If the edited deck joined a franchise since the edit was made, the change moves to the picked deck so a revert takes that deck out.</summary>
    private async Task<(FranchiseMove? Move, string? Error)> FranchiseMoveAsync(int deckId, List<MetadataFieldChange> changes, CancellationToken ct)
    {
        var change = changes.FirstOrDefault(c => c.Field == MetadataField.Franchise && IsUnresolvedFranchise(c.After));
        if (change == null)
            return (null, null);

        var pickedId = change.After.GetProperty("withDeckId").GetInt32();
        var states = await db.Decks.AsNoTracking().Where(d => d.DeckId == deckId || d.DeckId == pickedId)
                             .ToDictionaryAsync(d => d.DeckId, d => d.FranchiseId, ct);
        if (states.GetValueOrDefault(deckId) is not { } joined)
            return (null, null);

        changes.Remove(change);
        if (!states.TryGetValue(pickedId, out var pickedFranchise))
            return (null, "The deck picked for the franchise no longer exists. Reject this edit.");
        if (pickedFranchise == joined)
            return (null, null);
        if (pickedFranchise != null)
            return (null, "Both decks joined different franchises since this edit was made, so approving it would merge them. Reject this edit.");

        var target = await db.Franchises.AsNoTracking().Where(f => f.FranchiseId == joined)
                             .Select(f => new SnapshotFranchise(f.FranchiseId, f.OriginalTitle, f.RomajiTitle, f.EnglishTitle, null))
                             .FirstAsync(ct);
        var before = (await deckMetadata.LoadSnapshotAsync(pickedId, ct))!.Value(MetadataField.Franchise);
        return (new FranchiseMove(pickedId, new MetadataFieldChange { Field = MetadataField.Franchise, Before = before, After = DeckMetadataSnapshot.ToElement(target) }), null);
    }

    private static string? LeftOutPhrase(List<MetadataFieldChange> leftOut, List<MetadataField> partlyLeftOut)
    {
        var whole = leftOut.Select(c => c.Field).Where(f => !partlyLeftOut.Contains(f)).ToList();
        var parts = new List<string>();
        if (whole.Count > 0)
            parts.Add("the " + JoinLabels(whole));
        if (partlyLeftOut.Count > 0)
            parts.Add("part of the " + JoinLabels(partlyLeftOut));
        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    private const string ApprovedGroupKey = "metadata-edit-approved";
    private const string ContributionsUrl = "/requests?tab=contributions";

    /// <summary>Partial approvals carry the reviewer's note, so they stand alone and reach every author; plain approvals fold together and stop once the author's edits go live without review.</summary>
    private async Task NotifyApprovedAsync(string authorId, int deckId, string? leftOutText, string? excludeNote, CancellationToken ct)
    {
        var title = await DeckTitleAsync(deckId, ct);
        var deckLink = DeckHistoryUrl(deckId);
        if (leftOutText != null)
        {
            await notifications.Notify(authorId, NotificationType.MetadataEditApproved, "Your edit was approved",
                                       $"Your edit to {title} is now live, except {leftOutText}: {excludeNote}", deckLink);
            return;
        }

        var author = await trust.GetAsync(authorId, ct);
        if (author != null && await trust.AutoApplyMaxTierAsync(author, ct) >= 0)
            return;

        await notifications.NotifyGrouped(authorId, NotificationType.MetadataEditApproved, ApprovedGroupKey,
                                          (count, previousLink) => count == 1
                                              ? new GroupedNotificationContent("Your edit was approved", $"Your edit to {title} is now live.", deckLink)
                                              : new GroupedNotificationContent("Your edits were approved", $"{count} of your edits are now live.",
                                                                               previousLink == deckLink ? deckLink : ContributionsUrl));
    }

    public async Task<ServiceResult<object>> RejectAsync(long editId, string reviewerId, RejectMetadataEditRequest request, CancellationToken ct)
    {
        var note = request.Note?.Trim() ?? "";
        if (note.Length == 0)
            return ServiceResult<object>.Fail(400, "Say why the edit was rejected.");
        if (note.Length > MaxNoteLength)
            return ServiceResult<object>.Fail(400, $"Keep the reason under {MaxNoteLength} characters.");

        var (edit, _, failure) = await LoadActionableAsync(editId, reviewerId, "reject", ct);
        if (failure != null)
            return failure;

        if (!await ClaimAsync(editId, MetadataEditStatus.Rejected, ct, MetadataEditStatus.Pending))
            return AlreadyHandled<object>();
        edit.Status = MetadataEditStatus.Rejected;
        edit.ReviewedAt = DateTime.UtcNow;
        edit.ReviewedByUserId = reviewerId;
        edit.ReviewNote = note;
        await db.SaveChangesAsync(ct);

        if (edit.UserId != null)
            await notifications.Notify(edit.UserId, NotificationType.MetadataEditRejected, "Your edit wasn't approved",
                                       $"Your edit to {await DeckTitleAsync(edit.DeckId, ct)} wasn't approved: {note}", DeckHistoryUrl(edit.DeckId));

        return ServiceResult<object>.Ok(new { status = (int)edit.Status });
    }

    /// <summary>Undo for a rejection: back to the queue as if it was never handled, and the author's notification is removed.</summary>
    public async Task<ServiceResult<object>> ReopenAsync(long editId, string reviewerId, CancellationToken ct)
    {
        var edit = await db.MetadataEdits.FirstOrDefaultAsync(e => e.Id == editId, ct);
        if (edit == null)
            return ServiceResult<object>.Fail(404, "This edit doesn't exist.");
        var rejectedAt = edit.ReviewedAt ?? DateTime.MinValue;
        if (edit.Status != MetadataEditStatus.Rejected || edit.ReviewedByUserId != reviewerId || rejectedAt < DateTime.UtcNow - RejectUndoWindow)
            return ServiceResult<object>.Fail(409, "This rejection can't be undone anymore.");
        if (!await ClaimAsync(editId, MetadataEditStatus.Pending, ct, MetadataEditStatus.Rejected))
            return AlreadyHandled<object>();

        edit.Status = MetadataEditStatus.Pending;
        edit.ReviewedAt = null;
        edit.ReviewedByUserId = null;
        edit.ReviewNote = null;
        await db.SaveChangesAsync(ct);

        if (edit.UserId != null)
        {
            var link = DeckHistoryUrl(edit.DeckId);
            await db.Notifications.Where(n => n.UserId == edit.UserId && n.Type == NotificationType.MetadataEditRejected && n.LinkUrl == link &&
                                              n.CreatedAt >= rejectedAt)
                    .ExecuteDeleteAsync(ct);
        }

        return ServiceResult<object>.Ok(new { status = (int)edit.Status });
    }

    /// <summary>Authors can take back their own edit while it waits; checks on it stop counting toward anyone's record.</summary>
    public async Task<ServiceResult<object>> WithdrawAsync(long editId, string userId, CancellationToken ct)
    {
        var edit = await db.MetadataEdits.AsNoTracking().FirstOrDefaultAsync(e => e.Id == editId, ct);
        if (edit == null)
            return ServiceResult<object>.Fail(404, "This edit doesn't exist.");
        if (edit.UserId != userId)
            return ServiceResult<object>.Fail(403, "You can only withdraw your own edits.");
        var claimed = await db.MetadataEdits.Where(e => e.Id == editId && e.Status == MetadataEditStatus.Pending)
                              .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, MetadataEditStatus.Withdrawn)
                                                        .SetProperty(e => e.ReviewedAt, DateTime.UtcNow), ct);
        return claimed == 1 ? ServiceResult<object>.Ok(new { status = (int)MetadataEditStatus.Withdrawn }) : AlreadyHandled<object>();
    }

    public async Task<ServiceResult<object>> ConfirmAppliedAsync(long editId, string reviewerId, CancellationToken ct)
    {
        var reviewer = await trust.GetViewerAsync(reviewerId, ct);
        if (reviewer is not { CanReview: true })
            return ServiceResult<object>.Fail(403, "Only reviewers and editors can do this.");
        var edit = await db.MetadataEdits.FirstOrDefaultAsync(e => e.Id == editId, ct);
        if (edit == null)
            return ServiceResult<object>.Fail(404, "This edit doesn't exist.");
        if (edit.Status != MetadataEditStatus.Applied)
            return ServiceResult<object>.Fail(409, "Only live, unreviewed edits can be confirmed.");
        if (edit.UserId == reviewerId)
            return ServiceResult<object>.Fail(403, "You can't review your own edit.");

        if (!await ClaimAsync(editId, MetadataEditStatus.Approved, ct, MetadataEditStatus.Applied))
            return AlreadyHandled<object>();
        edit.Status = MetadataEditStatus.Approved;
        edit.ReviewedAt = DateTime.UtcNow;
        edit.ReviewedByUserId = reviewerId;
        await db.SaveChangesAsync(ct);
        return ServiceResult<object>.Ok(new { status = (int)edit.Status });
    }

    /// <summary>In a batch the caller runs the after-apply side effects once per deck and the author isn't notified per edit.</summary>
    public async Task<ServiceResult<RevertResultDto>> RevertAsync(long editId, string actorId, CancellationToken ct, bool inBatch = false)
    {
        var actor = await trust.GetViewerAsync(actorId, ct);
        if (actor is not { CanReview: true })
            return ServiceResult<RevertResultDto>.Fail(403, "Only reviewers and editors can revert edits.");

        var edit = await db.MetadataEdits.FirstOrDefaultAsync(e => e.Id == editId, ct);
        if (edit == null)
            return ServiceResult<RevertResultDto>.Fail(404, "This edit doesn't exist.");
        if (edit.Status is not (MetadataEditStatus.Applied or MetadataEditStatus.Approved))
            return ServiceResult<RevertResultDto>.Fail(409, "Only live edits can be reverted.");

        var changes = ParseChanges(edit.ChangesJson);
        var current = (await deckMetadata.LoadSnapshotAsync(edit.DeckId, ct))!;
        var inverse = changes.Select(c => (Change: c, Merge: Merge(c.Field, c.After, c.Before, current.Value(c.Field), preferEdit: false))).ToList();
        var revertable = inverse.Where(i => i.Merge.Changes).Select(i => i.Change).ToList();
        var skipped = inverse.Where(i => !i.Merge.Changes).Select(i => (int)i.Change.Field).ToList();
        if (revertable.Count == 0)
            return ServiceResult<RevertResultDto>.Fail(409, "Every field in this edit was changed again since, so there's nothing to revert.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await ClaimAsync(editId, MetadataEditStatus.Reverted, ct, MetadataEditStatus.Applied, MetadataEditStatus.Approved))
            return AlreadyHandled<RevertResultDto>();

        var deck = (await deckMetadata.LoadForPatchAsync(edit.DeckId, ct))!;
        var restored = inverse.Where(i => i.Merge.Changes).ToDictionary(i => i.Change.Field, i => i.Merge.Value);
        var error = await ApplyValuesAsync(deck, restored.Select(r => (r.Key, r.Value)), actorId, ct, recordRemovals: false);
        if (error != null)
            return ServiceResult<RevertResultDto>.Fail(400, error);

        var now = DateTime.UtcNow;
        edit.Status = MetadataEditStatus.Reverted;
        edit.ReviewedAt = now;
        edit.ReviewedByUserId = actorId;
        edit.ReviewNote = skipped.Count == 0
            ? null
            : $"{Capitalise(JoinLabels(revertable.Select(c => c.Field)))} restored. {Capitalise(JoinLabels(skipped.Select(f => (MetadataField)f)))} left alone because it had been changed again since.";

        var revertChanges = revertable.Select(c => new MetadataFieldChange { Field = c.Field, Before = current.Value(c.Field), After = restored[c.Field] })
                                      .ToList();
        var revertRow = MetadataEditLog.Applied(edit.DeckId, revertChanges, actorId, now: now);
        revertRow.RevertsEditId = edit.Id;
        db.MetadataEdits.Add(revertRow);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (inBatch)
            return ServiceResult<RevertResultDto>.Ok(new RevertResultDto(revertable.Select(c => (int)c.Field).ToList(), skipped));
        await AfterApplyAsync(edit.DeckId, MetadataFields.Of(revertChanges), ct);

        if (edit.UserId != null && edit.UserId != actorId)
            await notifications.Notify(edit.UserId, NotificationType.MetadataEditReverted, "Your edit was reverted",
                                       $"Your edit to {await DeckTitleAsync(edit.DeckId, ct)} was reverted.", DeckHistoryUrl(edit.DeckId));

        return ServiceResult<RevertResultDto>.Ok(new RevertResultDto(revertable.Select(c => (int)c.Field).ToList(), skipped));
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public async Task<ServiceResult<object>> SetCheckAsync(long editId, string userId, string? ip, MetadataEditCheckRequest request, CancellationToken ct)
    {
        var edit = await db.MetadataEdits.Include(e => e.Checks).FirstOrDefaultAsync(e => e.Id == editId, ct);
        if (edit == null)
            return ServiceResult<object>.Fail(404, "This edit doesn't exist.");
        var viewer = await trust.GetViewerAsync(userId, ct);
        if (CheckBlockedReason(viewer, edit) is { } blocked)
            return ServiceResult<object>.Fail(400, blocked);

        if (!Enum.IsDefined(request.Verdict))
            return ServiceResult<object>.Fail(400, "Pick confirm or doesn't look right.");
        if (request.Verdict == MetadataEditVerdict.Doubt && (request.Reason == null || !Enum.IsDefined(request.Reason.Value)))
            return ServiceResult<object>.Fail(400, "Pick what looks wrong.");
        var note = request.Note?.Trim();
        if (request.Verdict == MetadataEditVerdict.Doubt && string.IsNullOrEmpty(note))
            return ServiceResult<object>.Fail(400, "Say what looks wrong so editors can check it.");
        if (note is { Length: > 300 })
            return ServiceResult<object>.Fail(400, "Keep the details under 300 characters.");
        var sourceUrl = request.SourceUrl?.Trim();
        if (!string.IsNullOrEmpty(sourceUrl) && (sourceUrl.Length > 500 || !IsHttpUrl(sourceUrl)))
            return ServiceResult<object>.Fail(400, "Sources need to be links starting with http:// or https://.");

        var existing = edit.Checks.FirstOrDefault(c => c.UserId == userId);
        if (existing == null)
        {
            existing = new MetadataEditCheck { MetadataEditId = editId, UserId = userId };
            edit.Checks.Add(existing);
        }

        var doubted = request.Verdict == MetadataEditVerdict.Doubt
            ? MetadataFields.Of((request.Fields ?? []).Select(f => (MetadataField)f)) & edit.Fields
            : MetadataField.None;
        existing.Verdict = request.Verdict;
        existing.Fields = doubted == edit.Fields ? MetadataField.None : doubted;
        existing.Reason = request.Verdict == MetadataEditVerdict.Doubt ? request.Reason : null;
        existing.Note = string.IsNullOrEmpty(note) ? null : note;
        existing.SourceUrl = string.IsNullOrEmpty(sourceUrl) ? null : sourceUrl;
        existing.CreatedAt = DateTime.UtcNow;
        existing.IpAddress = ip;

        if (request.Verdict == MetadataEditVerdict.Confirm && !string.IsNullOrEmpty(sourceUrl))
        {
            var sources = ParseSources(edit.SourcesJson);
            if (!sources.Contains(sourceUrl) && sources.Count < MaxSources)
            {
                sources.Add(sourceUrl);
                edit.SourcesJson = JsonSerializer.Serialize(sources, DeckMetadataSnapshot.Json);
            }
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<object>.Ok(new { confirmCount = edit.Checks.Count(c => c.Verdict == MetadataEditVerdict.Confirm) });
    }

    public async Task<ServiceResult<object>> RemoveCheckAsync(long editId, string userId, CancellationToken ct)
    {
        var check = await db.MetadataEditChecks.Include(c => c.Edit).FirstOrDefaultAsync(c => c.MetadataEditId == editId && c.UserId == userId, ct);
        if (check == null)
            return ServiceResult<object>.Fail(404, "You haven't checked this edit.");
        if (check.Edit!.Status != MetadataEditStatus.Pending)
            return ServiceResult<object>.Fail(409, "This edit was already handled.");
        db.MetadataEditChecks.Remove(check);
        await db.SaveChangesAsync(ct);
        return ServiceResult<object>.Ok(new { });
    }

    public static string? CheckBlockedReason(MetadataViewer? record, MetadataEdit edit)
    {
        if (record == null)
            return "Sign in to check edits.";
        if (record.Banned)
            return BannedMessage;
        if (edit.UserId == record.UserId)
            return "You can't check your own edit.";
        if (edit.Status != MetadataEditStatus.Pending)
            return "This edit was already handled.";
        if (string.IsNullOrWhiteSpace(record.DisplayName))
            return "Set a display name before checking edits.";
        if (!record.EmailConfirmed)
            return "Confirm your email address before checking edits.";
        if (record.CreatedAt > DateTime.UtcNow.AddDays(-MinAccountDaysToCheck))
            return "New accounts can check edits after their first week.";
        return null;
    }

    public async Task<ServiceResult<CharacterNamePreview>> NamePreviewAsync(long editId, string viewerId, CancellationToken ct)
    {
        var viewer = await trust.GetViewerAsync(viewerId, ct);
        if (viewer is not { CanReview: true })
            return ServiceResult<CharacterNamePreview>.Fail(403, "Only reviewers and editors can see the preview.");
        var edit = await db.MetadataEdits.AsNoTracking().FirstOrDefaultAsync(e => e.Id == editId, ct);
        var change = edit == null ? null : ParseChanges(edit.ChangesJson).FirstOrDefault(c => c.Field == MetadataField.CharacterNames);
        if (edit == null || change == null)
            return ServiceResult<CharacterNamePreview>.Fail(404, "This edit doesn't change character names.");

        var (_, added) = AddedNames(change);
        var key = $"metadata-edit:name-preview:{editId}";
        if (!cache.TryGetValue<CharacterNamePreview>(key, out var preview))
        {
            preview = await nameCheckService.PreviewAsync(edit.DeckId, added, ct);
            cache.Set(key, preview, TimeSpan.FromMinutes(30));
        }

        return ServiceResult<CharacterNamePreview>.Ok(preview!);
    }
}
