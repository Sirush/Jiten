using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.MetadataEdits;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.MetadataEdits;

public enum MetadataTrustLevel
{
    New = 0,
    Established = 1,
    Trusted = 2,
    Editor = 3
}

/// <summary>Who someone is for gating: identity, roles and admin flags, without the edit record a trust level needs.</summary>
public sealed record MetadataViewer(
    string UserId,
    string? DisplayName,
    bool EmailConfirmed,
    DateTime CreatedAt,
    IReadOnlyList<string> Roles,
    bool Banned,
    bool AdminTrusted,
    DateTime? BlockClearedAt)
{
    public bool IsAdmin => Roles.Contains(nameof(UserRole.Administrator));
    public bool IsEditor => IsAdmin || (!Banned && Roles.Contains(nameof(UserRole.Editor)));
    public bool IsReviewer => !Banned && Roles.Contains(nameof(UserRole.Reviewer));
    public bool CanReview => IsEditor || IsReviewer;
}

public sealed record MetadataContributorRecord(
    MetadataViewer Viewer,
    MetadataTrustLevel Level,
    bool Blocked,
    int CountedEdits,
    int Accepted,
    int Reverted,
    MetadataCheckStats CheckStats,
    string LevelReason)
{
    public string UserId => Viewer.UserId;
    public string? DisplayName => Viewer.DisplayName;
    public DateTime CreatedAt => Viewer.CreatedAt;
    public IReadOnlyList<string> Roles => Viewer.Roles;
    public bool Banned => Viewer.Banned;
    public bool AdminTrusted => Viewer.AdminTrusted;
    public bool IsEditor => Level == MetadataTrustLevel.Editor;
    public bool IsReviewer => Viewer.IsReviewer;

    public string LevelName => Level switch
    {
        MetadataTrustLevel.Editor => "editor",
        MetadataTrustLevel.Trusted => "trusted",
        MetadataTrustLevel.Established => "established",
        _ => "new"
    };
}

/// <summary>How a contributor's checks compared with the final outcome; Rejected includes reverted edits.</summary>
public sealed record MetadataCheckStats(int ConfirmedApproved, int ConfirmedRejected, int DoubtedApproved, int DoubtedRejected)
{
    public int Matched => ConfirmedApproved + DoubtedRejected;
    public int Missed => ConfirmedRejected + DoubtedApproved;
}

/// <summary>Computes contributor trust from the edit record; levels are never stored, so thresholds can change without a backfill.</summary>
public sealed class MetadataTrustService(JitenDbContext db, UserDbContext userDb, IJitenPlusService jitenPlus)
{
    public const int AutoBlockReverts = 5;

    /// <summary>One revert is forgiven per this many counted edits inside a level's no-revert window.</summary>
    public const int CountedEditsPerForgivenRevert = 10;
    public static readonly TimeSpan AutoBlockWindow = TimeSpan.FromDays(30);
    public const int CheckCreditsPerCountedEdit = 3;
    public const int CheckCreditPerAuthorPer30Days = 3;
    public const int ContradictingChecksBeforeCreditStops = 3;

    private readonly Dictionary<string, MetadataViewer?> _viewers = new();
    private readonly Dictionary<string, MetadataContributorRecord?> _records = new();
    private MetadataEditSettings? _settings;

    public async Task<MetadataEditSettings> GetSettingsAsync(CancellationToken ct = default) =>
        _settings ??= await db.MetadataEditSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == MetadataEditSettings.SingletonId, ct)
                      ?? new MetadataEditSettings();

    /// <summary>Highest risk tier this contributor may apply without review; -1 means everything is queued.</summary>
    public async Task<int> AutoApplyMaxTierAsync(MetadataContributorRecord record, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        return record.Level switch
        {
            MetadataTrustLevel.Editor => (int)MetadataRiskTier.High,
            MetadataTrustLevel.Trusted => settings.TrustedAutoApplyMaxTier,
            MetadataTrustLevel.Established => settings.EstablishedAutoApplyMaxTier,
            _ => -1
        };
    }

    public async Task<MetadataViewer?> GetViewerAsync(string userId, CancellationToken ct = default) =>
        (await GetViewersAsync([userId], ct)).GetValueOrDefault(userId);

    public async Task<Dictionary<string, MetadataViewer>> GetViewersAsync(IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        var missing = ids.Where(id => !_viewers.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var users = await userDb.Users.AsNoTracking()
                                    .Where(u => missing.Contains(u.Id))
                                    .Select(u => new { u.Id, u.DisplayName, u.EmailConfirmed, u.CreatedAt })
                                    .ToListAsync(ct);
            var roles = (await (from ur in userDb.UserRoles
                                join r in userDb.Roles on ur.RoleId equals r.Id
                                where missing.Contains(ur.UserId)
                                select new { ur.UserId, Name = r.Name! }).ToListAsync(ct))
                        .ToLookup(r => r.UserId, r => r.Name);
            var overrides = await db.MetadataContributors.AsNoTracking().Where(c => missing.Contains(c.UserId)).ToDictionaryAsync(c => c.UserId, ct);

            foreach (var id in missing)
                _viewers[id] = null;
            foreach (var u in users)
            {
                var o = overrides.GetValueOrDefault(u.Id);
                _viewers[u.Id] = new MetadataViewer(u.Id, u.DisplayName, u.EmailConfirmed, u.CreatedAt, roles[u.Id].ToList(), o?.BannedAt != null,
                                                    o?.AdminTrusted == true, o?.BlockClearedAt);
            }
        }

        return ids.Where(id => _viewers[id] != null).ToDictionary(id => id, id => _viewers[id]!);
    }

    public async Task<MetadataContributorRecord?> GetAsync(string userId, CancellationToken ct = default) =>
        (await GetManyAsync([userId], ct)).GetValueOrDefault(userId);

    /// <summary>Loads each source once for the whole batch, so contributor lists never query per person.</summary>
    public async Task<Dictionary<string, MetadataContributorRecord>> GetManyAsync(IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        var missing = ids.Where(id => !_records.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var viewers = await GetViewersAsync(missing, ct);
            var edits = (await db.MetadataEdits.AsNoTracking()
                                 .Where(e => missing.Contains(e.UserId!) && e.Actor == MetadataEditActor.User && e.RevertsEditId == null)
                                 .Select(e => new EditRow(e.UserId!, e.SubmissionId, e.DeckId, e.CreatedAt, e.Status, e.ReviewedAt, e.ReviewedByUserId))
                                 .ToListAsync(ct))
                        .ToLookup(e => e.UserId);
            var checks = await CheckRowsAsync(missing, ct);

            foreach (var id in missing)
                _records[id] = viewers.TryGetValue(id, out var viewer) ? await BuildAsync(viewer, edits[id].ToList(), checks[id].ToList(), ct) : null;
        }

        return ids.Where(id => _records[id] != null).ToDictionary(id => id, id => _records[id]!);
    }

    private sealed record EditRow(string UserId, Guid SubmissionId, int DeckId, DateTime CreatedAt, MetadataEditStatus Status, DateTime? ReviewedAt,
                                  string? ReviewedByUserId);

    private sealed record CheckRow(string UserId, MetadataEditVerdict Verdict, DateTime CreatedAt, string? CheckIp, string? EditIp, string? AuthorId,
                                   MetadataEditStatus Status);

    private async Task<MetadataContributorRecord> BuildAsync(MetadataViewer viewer, List<EditRow> edits, List<CheckRow> checks, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var now = DateTime.UtcNow;
        var userId = viewer.UserId;

        var counted = edits.Where(e => e.Status == MetadataEditStatus.Approved && e.ReviewedByUserId != null && e.ReviewedByUserId != userId)
                           .GroupBy(e => e.SubmissionId)
                           .Select(g => (g.First().DeckId, g.Min(e => e.CreatedAt).Date))
                           .Distinct()
                           .Count();

        var accepted = edits.Where(e => e.Status is MetadataEditStatus.Approved or MetadataEditStatus.Applied)
                            .Select(e => e.SubmissionId).Distinct().Count();
        var revertTimes = edits.Where(e => e.Status == MetadataEditStatus.Reverted)
                               .GroupBy(e => e.SubmissionId)
                               .Select(g => g.Max(e => e.ReviewedAt ?? e.CreatedAt))
                               .ToList();

        var blockFloor = viewer.BlockClearedAt ?? DateTime.MinValue;
        var blocked = revertTimes.Count(t => t > now - AutoBlockWindow && t > blockFloor) >= AutoBlockReverts;

        var (checkCredits, checkStats) = CheckCredits(checks, now);

        var baseOk = viewer.EmailConfirmed && viewer.CreatedAt <= now.AddDays(-settings.EstablishedMinAccountDays);
        int WithChecks(int threshold) => counted + Math.Min(checkCredits / CheckCreditsPerCountedEdit, threshold / 2);
        bool FewRevertsWithin(int days, int threshold) =>
            revertTimes.Count(t => t > now.AddDays(-days)) <= WithChecks(threshold) / CountedEditsPerForgivenRevert;

        MetadataTrustLevel level;
        string reason;
        if (viewer.IsEditor)
            (level, reason) = (MetadataTrustLevel.Editor, viewer.IsAdmin ? "Admin" : "Editor role");
        else if (viewer.Banned)
            (level, reason) = (MetadataTrustLevel.New, "Banned");
        else if (blocked)
            (level, reason) = (MetadataTrustLevel.New, $"Blocked: {AutoBlockReverts}+ reverts in {AutoBlockWindow.Days} days");
        else if (viewer.IsReviewer)
            (level, reason) = (MetadataTrustLevel.Trusted, "Reviewer role");
        else if (viewer.AdminTrusted)
            (level, reason) = (MetadataTrustLevel.Trusted, "Trusted by admin");
        else if (await jitenPlus.GetTierAsync(userId, ct) == JitenPlusTier.Full)
            (level, reason) = (MetadataTrustLevel.Trusted, "Jiten+ Full");
        else if (baseOk && WithChecks(settings.TrustedCountedEdits) >= settings.TrustedCountedEdits &&
                 FewRevertsWithin(settings.TrustedNoRevertDays, settings.TrustedCountedEdits))
            (level, reason) = (MetadataTrustLevel.Trusted, "Edit record");
        else if (baseOk && WithChecks(settings.EstablishedCountedEdits) >= settings.EstablishedCountedEdits &&
                 FewRevertsWithin(settings.EstablishedNoRevertDays, settings.EstablishedCountedEdits))
            (level, reason) = (MetadataTrustLevel.Established, "Edit record");
        else
            (level, reason) = (MetadataTrustLevel.New,
                               !viewer.EmailConfirmed ? "Email not confirmed" : !baseOk ? "Account too new" : "Not enough counted edits");

        return new MetadataContributorRecord(viewer, level, blocked, counted, accepted, revertTimes.Count, checkStats, reason);
    }

    /// <summary>Each check with the outcome it is judged against; a field-level doubt counts as rejected when an editor left those fields out.</summary>
    private async Task<ILookup<string, CheckRow>> CheckRowsAsync(List<string> userIds, CancellationToken ct)
    {
        var rows = await db.MetadataEditChecks.AsNoTracking()
                           .Where(c => userIds.Contains(c.UserId))
                           .Select(c => new
                                        {
                                            c.UserId, c.Verdict, c.Fields, c.CreatedAt, CheckIp = c.IpAddress, EditIp = c.Edit!.IpAddress,
                                            c.Edit.Status, AuthorId = c.Edit.UserId, c.Edit.SubmissionId
                                        })
                           .ToListAsync(ct);

        var doubtedSubmissions = rows.Where(c => c.Verdict == MetadataEditVerdict.Doubt && c.Fields != MetadataField.None)
                                     .Select(c => c.SubmissionId).Distinct().ToList();
        var leftOut = doubtedSubmissions.Count == 0
            ? new Dictionary<Guid, MetadataField>()
            : (await db.MetadataEdits.AsNoTracking()
                       .Where(e => doubtedSubmissions.Contains(e.SubmissionId) && e.Status == MetadataEditStatus.Rejected)
                       .Select(e => new { e.SubmissionId, e.Fields })
                       .ToListAsync(ct))
              .GroupBy(e => e.SubmissionId)
              .ToDictionary(g => g.Key, g => MetadataFields.Of(g.Select(e => e.Fields)));

        return rows.Select(c => new CheckRow(c.UserId, c.Verdict, c.CreatedAt, c.CheckIp, c.EditIp, c.AuthorId,
                                             c.Verdict == MetadataEditVerdict.Doubt && c.Status == MetadataEditStatus.Approved &&
                                             c.Fields != MetadataField.None && (leftOut.GetValueOrDefault(c.SubmissionId) & c.Fields) != MetadataField.None
                                                 ? MetadataEditStatus.Rejected
                                                 : c.Status))
                   .ToLookup(c => c.UserId);
    }

    /// <summary>Matching checks capped per author per 30 days, minus contradicting ones.</summary>
    private static (int Credits, MetadataCheckStats Stats) CheckCredits(List<CheckRow> checks, DateTime now)
    {
        var resolved = checks.Where(c => c.Status is MetadataEditStatus.Approved or MetadataEditStatus.Rejected or MetadataEditStatus.Reverted)
                             .Where(c => c.CheckIp == null || c.EditIp == null || c.CheckIp != c.EditIp)
                             .Select(c => new
                                          {
                                              c.CreatedAt, c.AuthorId,
                                              Matches = c.Verdict == MetadataEditVerdict.Confirm
                                                  ? c.Status == MetadataEditStatus.Approved
                                                  : c.Status is MetadataEditStatus.Rejected or MetadataEditStatus.Reverted
                                          })
                             .ToList();

        bool IsRejected(MetadataEditStatus s) => s is MetadataEditStatus.Rejected or MetadataEditStatus.Reverted;
        var stats = new MetadataCheckStats(
            checks.Count(c => c.Verdict == MetadataEditVerdict.Confirm && c.Status == MetadataEditStatus.Approved),
            checks.Count(c => c.Verdict == MetadataEditVerdict.Confirm && IsRejected(c.Status)),
            checks.Count(c => c.Verdict == MetadataEditVerdict.Doubt && c.Status == MetadataEditStatus.Approved),
            checks.Count(c => c.Verdict == MetadataEditVerdict.Doubt && IsRejected(c.Status)));

        var contradictingRecently = resolved.Count(c => !c.Matches && c.CreatedAt > now.AddDays(-90));
        if (contradictingRecently >= ContradictingChecksBeforeCreditStops)
            return (0, stats);

        var good = resolved.Where(c => c.Matches)
                           .GroupBy(c => (c.AuthorId, Bucket: (int)((now - c.CreatedAt).TotalDays / 30)))
                           .Sum(g => Math.Min(g.Count(), CheckCreditPerAuthorPer30Days));
        var bad = resolved.Count(c => !c.Matches);
        return (Math.Max(0, good - bad), stats);
    }
}
