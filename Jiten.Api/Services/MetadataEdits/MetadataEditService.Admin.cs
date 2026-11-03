using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.MetadataEdits;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.MetadataEdits;

public sealed partial class MetadataEditService
{
    public async Task<MetadataEditSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var s = await trust.GetSettingsAsync(ct);
        return new MetadataEditSettingsDto
               {
                   EstablishedMinAccountDays = s.EstablishedMinAccountDays, EstablishedCountedEdits = s.EstablishedCountedEdits,
                   EstablishedNoRevertDays = s.EstablishedNoRevertDays, TrustedCountedEdits = s.TrustedCountedEdits,
                   TrustedNoRevertDays = s.TrustedNoRevertDays, EstablishedAutoApplyMaxTier = s.EstablishedAutoApplyMaxTier,
                   TrustedAutoApplyMaxTier = s.TrustedAutoApplyMaxTier
               };
    }

    public async Task<ServiceResult<MetadataEditSettingsDto>> UpdateSettingsAsync(MetadataEditSettingsDto dto, CancellationToken ct)
    {
        int[] counts = [dto.EstablishedMinAccountDays, dto.EstablishedCountedEdits, dto.EstablishedNoRevertDays, dto.TrustedCountedEdits, dto.TrustedNoRevertDays];
        if (counts.Any(v => v is < 0 or > 10000))
            return ServiceResult<MetadataEditSettingsDto>.Fail(400, "Thresholds must be between 0 and 10000.");
        if (dto.EstablishedAutoApplyMaxTier is < -1 or > 2 || dto.TrustedAutoApplyMaxTier is < -1 or > 2)
            return ServiceResult<MetadataEditSettingsDto>.Fail(400, "Auto-apply tiers go from -1 (none) to 2 (high).");

        var row = await db.MetadataEditSettings.FirstOrDefaultAsync(s => s.Id == MetadataEditSettings.SingletonId, ct);
        if (row == null)
        {
            row = new MetadataEditSettings();
            db.MetadataEditSettings.Add(row);
        }

        row.EstablishedMinAccountDays = dto.EstablishedMinAccountDays;
        row.EstablishedCountedEdits = dto.EstablishedCountedEdits;
        row.EstablishedNoRevertDays = dto.EstablishedNoRevertDays;
        row.TrustedCountedEdits = dto.TrustedCountedEdits;
        row.TrustedNoRevertDays = dto.TrustedNoRevertDays;
        row.EstablishedAutoApplyMaxTier = dto.EstablishedAutoApplyMaxTier;
        row.TrustedAutoApplyMaxTier = dto.TrustedAutoApplyMaxTier;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ServiceResult<MetadataEditSettingsDto>.Ok(dto);
    }

    public async Task<ContributorPageDto> ContributorsAsync(string? search, string? level, string? status, string? sort, int offset, int limit,
                                                          CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        List<string> ids;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            ids = await userDb.Users.AsNoTracking()
                              .Where(u => (u.DisplayName != null && u.DisplayName.ToLower().Contains(term)) || (u.Email != null && u.Email.ToLower().Contains(term)))
                              .OrderBy(u => u.DisplayName).Select(u => u.Id).Take(200).ToListAsync(ct);
        }
        else
        {
            var editors = await db.MetadataEdits.AsNoTracking().Where(e => e.UserId != null && e.Actor == MetadataEditActor.User)
                                  .Select(e => e.UserId!).Distinct().ToListAsync(ct);
            var checkers = await db.MetadataEditChecks.AsNoTracking().Select(c => c.UserId).Distinct().ToListAsync(ct);
            var roleNames = new[] { nameof(UserRole.Editor), nameof(UserRole.Reviewer) };
            var roleHolders = await (from ur in userDb.UserRoles
                                     join r in userDb.Roles on ur.RoleId equals r.Id
                                     where roleNames.Contains(r.Name!)
                                     select ur.UserId).ToListAsync(ct);
            var flagged = await db.MetadataContributors.AsNoTracking().Select(c => c.UserId).ToListAsync(ct);
            ids = editors.Concat(checkers).Concat(roleHolders).Concat(flagged).Distinct().ToList();
        }

        var emails = await userDb.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Email })
                                 .ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        var checkCounts = await db.MetadataEditChecks.AsNoTracking().Where(c => ids.Contains(c.UserId))
                                  .GroupBy(c => c.UserId).Select(g => new { g.Key, Count = g.Count() })
                                  .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        IEnumerable<MetadataContributorRecord> filtered = (await trust.GetManyAsync(ids, ct)).Values;
        if (!string.IsNullOrEmpty(level))
            filtered = filtered.Where(r => r.LevelName == level);
        filtered = status switch
        {
            "blocked" => filtered.Where(r => r.Blocked),
            "banned" => filtered.Where(r => r.Banned),
            "adminTrusted" => filtered.Where(r => r.AdminTrusted),
            "reviewers" => filtered.Where(r => r.IsReviewer || r.Roles.Contains(nameof(UserRole.Editor))),
            "reverted" => filtered.Where(r => r.Reverted > 0),
            _ => filtered
        };

        int Checks(MetadataContributorRecord r) => checkCounts.GetValueOrDefault(r.UserId);
        var ordered = sort switch
        {
            "accepted" => filtered.OrderByDescending(r => r.Accepted).ThenByDescending(r => r.CountedEdits),
            "reverted" => filtered.OrderByDescending(r => r.Reverted).ThenByDescending(r => r.Accepted),
            "checks" => filtered.OrderByDescending(Checks).ThenByDescending(r => r.CheckStats.Matched),
            "missed" => filtered.OrderByDescending(r => r.CheckStats.Missed).ThenByDescending(Checks),
            "level" => filtered.OrderByDescending(r => r.Level).ThenByDescending(r => r.CountedEdits),
            "name" => filtered.OrderBy(r => r.DisplayName ?? "\uffff", StringComparer.OrdinalIgnoreCase),
            "newest" => filtered.OrderByDescending(r => r.CreatedAt),
            _ => filtered.OrderByDescending(r => r.CountedEdits).ThenByDescending(r => r.Accepted)
        };
        var page = ordered.ThenBy(r => r.DisplayName).ToList();

        var items = page.Skip(offset).Take(limit)
                           .Select(r => new ContributorDto(r.UserId, r.DisplayName, emails.GetValueOrDefault(r.UserId), r.Roles.ToList(), r.LevelName,
                                                           r.LevelReason, r.CountedEdits, r.Accepted, r.Reverted, checkCounts.GetValueOrDefault(r.UserId),
                                                           r.AdminTrusted, r.Blocked, r.Banned, r.CheckStats.ConfirmedApproved,
                                                           r.CheckStats.ConfirmedRejected, r.CheckStats.DoubtedApproved,
                                                           r.CheckStats.DoubtedRejected))
                           .ToList();
        return new ContributorPageDto(page.Count, items);
    }

    public async Task<ServiceResult<object>> UpdateContributorAsync(string userId, UpdateContributorRequest request, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user == null)
            return ServiceResult<object>.Fail(404, "This user doesn't exist.");

        if (request.AdminTrusted.HasValue || request.ClearBlock == true || request.Banned.HasValue)
        {
            var row = await db.MetadataContributors.FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (row == null)
            {
                row = new MetadataContributor { UserId = userId };
                db.MetadataContributors.Add(row);
            }

            if (request.AdminTrusted.HasValue)
                row.AdminTrusted = request.AdminTrusted.Value;
            if (request.ClearBlock == true)
                row.BlockClearedAt = DateTime.UtcNow;
            if (request.Banned.HasValue)
                row.BannedAt = request.Banned.Value ? row.BannedAt ?? DateTime.UtcNow : null;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (request.Role != null)
        {
            if (request.Role is not ("none" or "reviewer" or "editor"))
                return ServiceResult<object>.Fail(400, "Role must be none, reviewer or editor.");
            var roles = await userManager.GetRolesAsync(user);
            foreach (var role in new[] { nameof(UserRole.Editor), nameof(UserRole.Reviewer) })
                if (roles.Contains(role))
                    await userManager.RemoveFromRoleAsync(user, role);
            if (request.Role == "editor")
                await userManager.AddToRoleAsync(user, nameof(UserRole.Editor));
            else if (request.Role == "reviewer")
                await userManager.AddToRoleAsync(user, nameof(UserRole.Reviewer));
        }

        return ServiceResult<object>.Ok(new { });
    }

    public async Task<MetadataEditMetricsDto> MetricsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-28);
        var monthAgo = now.AddDays(-30);
        var rows = await db.MetadataEdits.AsNoTracking()
                           .Where(e => e.Actor == MetadataEditActor.User && e.RevertsEditId == null &&
                                       (e.Status == MetadataEditStatus.Pending || e.Status == MetadataEditStatus.Applied || e.CreatedAt >= since ||
                                        e.ReviewedAt >= since))
                           .Select(e => new { e.SubmissionId, e.Status, e.CreatedAt, e.ReviewedAt, e.ReviewedByUserId, e.UserId })
                           .ToListAsync(ct);

        var pending = rows.Where(e => e.Status == MetadataEditStatus.Pending).ToList();
        var decided = rows.Where(e => e.ReviewedAt >= monthAgo && e.ReviewedByUserId != null && e.ReviewedByUserId != e.UserId &&
                                      e.Status is MetadataEditStatus.Approved or MetadataEditStatus.Rejected or MetadataEditStatus.Reverted)
                          .ToList();
        var waits = decided.Where(e => e.Status != MetadataEditStatus.Reverted)
                           .Select(e => (e.ReviewedAt!.Value - e.CreatedAt).TotalHours).Order().ToList();
        double? median = waits.Count == 0 ? null : waits.Count % 2 == 1 ? waits[waits.Count / 2] : (waits[waits.Count / 2 - 1] + waits[waits.Count / 2]) / 2;

        var weeks = Enumerable.Range(0, 4)
                              .Select(i =>
                              {
                                  var start = now.Date.AddDays(-7 * (i + 1) + 1);
                                  var end = start.AddDays(7);
                                  return new MetadataEditWeekDto(
                                      start,
                                      rows.Where(e => e.CreatedAt >= start && e.CreatedAt < end).Select(e => e.SubmissionId).Distinct().Count(),
                                      rows.Count(e => e.ReviewedAt >= start && e.ReviewedAt < end && e.ReviewedByUserId != null && e.ReviewedByUserId != e.UserId),
                                      rows.Count(e => e.CreatedAt >= start && e.CreatedAt < end && e.Status == MetadataEditStatus.Applied));
                              })
                              .ToList();

        var recent = rows.Where(e => e.CreatedAt >= monthAgo).ToList();
        return new MetadataEditMetricsDto(
            pending.Count,
            pending.Count == 0 ? null : (now - pending.Min(e => e.CreatedAt)).TotalHours,
            rows.Count(e => e.Status == MetadataEditStatus.Applied),
            median,
            decided.Count,
            decided.Count(e => e.Status == MetadataEditStatus.Approved),
            decided.Count(e => e.Status == MetadataEditStatus.Rejected),
            decided.Count(e => e.Status == MetadataEditStatus.Reverted),
            rows.Count(e => e.Status == MetadataEditStatus.Withdrawn && e.ReviewedAt >= monthAgo),
            recent.Select(e => e.SubmissionId).Distinct().Count(),
            recent.Count(e => e.Status == MetadataEditStatus.Applied || (e.Status == MetadataEditStatus.Approved && e.ReviewedByUserId == e.UserId)),
            weeks);
    }

    public async Task<ServiceResult<RevertAllResultDto>> RevertAllAsync(string userId, string adminId, CancellationToken ct)
    {
        var edits = await db.MetadataEdits.AsNoTracking()
                            .Where(e => e.UserId == userId && e.Actor == MetadataEditActor.User && e.RevertsEditId == null &&
                                        (e.Status == MetadataEditStatus.Applied || e.Status == MetadataEditStatus.Approved))
                            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                            .Select(e => new { e.Id, e.DeckId }).ToListAsync(ct);

        int reverted = 0, partial = 0;
        var touched = new Dictionary<int, MetadataField>();
        foreach (var edit in edits)
        {
            var result = await RevertAsync(edit.Id, adminId, ct, inBatch: true);
            if (!result.IsOk)
                continue;
            reverted++;
            if (result.Value!.SkippedFields.Count > 0)
                partial++;
            touched[edit.DeckId] = touched.GetValueOrDefault(edit.DeckId) | MetadataFields.Of(result.Value.RevertedFields.Select(f => (MetadataField)f));
        }

        foreach (var (deckId, fields) in touched)
            await AfterApplyAsync(deckId, fields & ~MetadataField.Relationships, ct);
        if (touched.Values.Any(f => f.HasFlag(MetadataField.Relationships)))
            await franchiseSync.RunAsync(ct);

        var pending = await db.MetadataEdits.Where(e => e.UserId == userId && e.Status == MetadataEditStatus.Pending).ToListAsync(ct);
        foreach (var edit in pending)
        {
            edit.Status = MetadataEditStatus.Rejected;
            edit.ReviewedAt = DateTime.UtcNow;
            edit.ReviewedByUserId = adminId;
            edit.ReviewNote = "Removed during a cleanup.";
        }
        await db.SaveChangesAsync(ct);

        return ServiceResult<RevertAllResultDto>.Ok(new RevertAllResultDto(reverted, partial));
    }
}
