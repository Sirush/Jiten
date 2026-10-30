using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>With <c>IsPreview</c>, the owner asked to see the profile as <c>Viewer</c> would.</summary>
public sealed record ProfileContext(
    string UserId,
    string? DisplayName,
    DateTime CreatedAt,
    ProfileViewer Viewer,
    ProfilePrivacy Privacy,
    UserProfile Profile,
    bool IsPreview = false)
{
    public bool IsOwner => Viewer == ProfileViewer.Owner;
    public bool CanSee(ProfileSection section) => Privacy.CanSee(Viewer, section);
    public bool Shows(ProfileField field) => Privacy.ShowsField(Viewer, field);
}

public interface IProfileAccessService
{
    /// <summary>Null when the profile does not exist or the viewer may not open it; callers answer 404 either way.</summary>
    Task<ProfileContext?> ResolveAsync(string handle, CancellationToken ct = default);

    Task<ProfileContext?> ResolveByIdAsync(string userId, CancellationToken ct = default);

    /// <summary>The owner's preference rows with a status, privacy flag or score, by deck; the owner's own view also carries notes.</summary>
    Task<IReadOnlyDictionary<int, PreferenceRow>> GetPreferencesAsync(ProfileContext context, CancellationToken ct = default);

    /// <summary>Titles among <paramref name="deckIds"/> the viewer must not see: private ones, and adult ones when the owner hides them.</summary>
    Task<HashSet<int>> GetHiddenDeckIdsAsync(ProfileContext context, IReadOnlyCollection<int> deckIds, CancellationToken ct = default);

    Task<Dictionary<int, DisplayStatus>> GetVisibleDisplayStatusesAsync(ProfileContext context, CancellationToken ct = default);

    /// <summary>Visible display statuses, plus the hidden titles among them and among <paramref name="alsoCheck"/>, from one hidden-title lookup.</summary>
    Task<(Dictionary<int, DisplayStatus> Statuses, HashSet<int> Hidden)> GetVisibleDisplayStatusesAsync(
        ProfileContext context, IReadOnlyCollection<int> alsoCheck, CancellationToken ct = default);

    /// <summary>Stored scores among <paramref name="deckIds"/>; empty when the owner hides scores from this viewer.</summary>
    Task<Dictionary<int, short>> GetVisibleScoresAsync(ProfileContext context, IReadOnlyCollection<int> deckIds, CancellationToken ct = default);
}

public class ProfileAccessService(
    UserDbContext userContext,
    JitenDbContext jitenContext,
    ICurrentUserService userService,
    IHttpContextAccessor httpContextAccessor) : IProfileAccessService
{
    public const string ViewAsQueryKey = "viewAs";

    private (string UserId, bool WithNotes, Dictionary<int, PreferenceRow> Rows)? _preferences;

    private sealed record UserWithProfile(string Id, string? DisplayName, DateTime CreatedAt, string? NormalizedDisplayName, UserProfile? Profile);

    /// <summary>Profile links used to carry the login username; they keep resolving so old shared links still work, but a display name match wins.</summary>
    public async Task<ProfileContext?> ResolveAsync(string handle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(handle))
            return null;

        var key = DisplayNameValidator.ToKey(DisplayNameValidator.Normalize(handle));
        var userName = handle.ToUpperInvariant();
        var matches = await WithProfiles(userContext.Users.Where(u => u.NormalizedDisplayName == key || u.NormalizedUserName == userName))
                            .Take(2)
                            .ToListAsync(ct);
        var user = matches.FirstOrDefault(u => u.NormalizedDisplayName == key) ?? matches.FirstOrDefault();

        return user == null ? null : Build(user);
    }

    public async Task<ProfileContext?> ResolveByIdAsync(string userId, CancellationToken ct = default)
    {
        var user = await WithProfiles(userContext.Users.Where(u => u.Id == userId)).FirstOrDefaultAsync(ct);
        return user == null ? null : Build(user);
    }

    public async Task<IReadOnlyDictionary<int, PreferenceRow>> GetPreferencesAsync(ProfileContext context, CancellationToken ct = default)
    {
        if (_preferences is { } cached && cached.UserId == context.UserId && cached.WithNotes == context.IsOwner)
            return cached.Rows;

        var rows = (await MediaListStatusHelper.LoadPreferenceRowsAsync(userContext, context.UserId, withNotes: context.IsOwner, ct))
                   .ToDictionary(r => r.DeckId);
        _preferences = (context.UserId, context.IsOwner, rows);
        return rows;
    }

    public async Task<HashSet<int>> GetHiddenDeckIdsAsync(ProfileContext context, IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        if (context.IsOwner || deckIds.Count == 0)
            return [];

        var preferences = await GetPreferencesAsync(context, ct);
        var hidden = deckIds.Where(id => preferences.TryGetValue(id, out var row) && row.IsPrivate).ToHashSet();

        if (context.Privacy.HideAdultTitles)
        {
            var adult = await jitenContext.DeckGenres.AsNoTracking()
                                          .Where(g => deckIds.Contains(g.DeckId) && g.Genre == Genre.AdultOnly)
                                          .Select(g => g.DeckId)
                                          .ToListAsync(ct);
            hidden.UnionWith(adult);
        }

        return hidden;
    }

    public async Task<Dictionary<int, DisplayStatus>> GetVisibleDisplayStatusesAsync(ProfileContext context, CancellationToken ct = default) =>
        (await GetVisibleDisplayStatusesAsync(context, [], ct)).Statuses;

    public async Task<(Dictionary<int, DisplayStatus> Statuses, HashSet<int> Hidden)> GetVisibleDisplayStatusesAsync(
        ProfileContext context, IReadOnlyCollection<int> alsoCheck, CancellationToken ct = default)
    {
        var statuses = await MediaListStatusHelper.FoldDisplayStatusesAsync(jitenContext, (await GetPreferencesAsync(context, ct)).Values);
        var hidden = await GetHiddenDeckIdsAsync(context, statuses.Keys.Union(alsoCheck).ToList(), ct);
        foreach (var id in hidden)
            statuses.Remove(id);
        return (statuses, hidden);
    }

    public async Task<Dictionary<int, short>> GetVisibleScoresAsync(ProfileContext context, IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        if (!context.CanSee(ProfileSection.Scores) || deckIds.Count == 0)
            return [];

        var preferences = await GetPreferencesAsync(context, ct);
        return deckIds.Distinct()
                      .Where(id => preferences.TryGetValue(id, out var row) && row.Score != null)
                      .ToDictionary(id => id, id => preferences[id].Score!.Value);
    }

    private IQueryable<UserWithProfile> WithProfiles(IQueryable<User> users) =>
        (from u in users
         join p in userContext.UserProfiles on u.Id equals p.UserId into profiles
         from p in profiles.DefaultIfEmpty()
         select new UserWithProfile(u.Id, u.DisplayName, u.CreatedAt, u.NormalizedDisplayName, p))
        .AsNoTracking();

    /// <summary>A profile without a display name is never shown to anyone but its owner, since the old fallback was the login name.</summary>
    private ProfileContext? Build(UserWithProfile user)
    {
        var profile = user.Profile ?? new UserProfile { UserId = user.Id };
        var privacy = ProfilePrivacy.Parse(profile.PrivacyJson, profile.IsPublic, profile.IsMediaListPublic);

        var viewer = userService.UserId == user.Id ? ProfileViewer.Owner : ProfileViewer.Everyone;
        var preview = viewer == ProfileViewer.Owner ? RequestedPreview() : null;
        if (preview is { } previewViewer)
            viewer = previewViewer;

        if (viewer != ProfileViewer.Owner && (string.IsNullOrEmpty(user.DisplayName) || !privacy.IsVisibleTo(viewer)))
            return null;

        return new ProfileContext(user.Id, user.DisplayName, user.CreatedAt, viewer, privacy, profile, preview != null);
    }

    /// <summary>Only ever narrows what the owner sees; anyone else's request ignores it.</summary>
    private ProfileViewer? RequestedPreview()
    {
        var raw = httpContextAccessor.HttpContext?.Request.Query[ViewAsQueryKey].ToString();
        return ProfilePrivacy.TryParseEnum<ProfileViewer>(raw, out var viewer) && viewer != ProfileViewer.Owner ? viewer : null;
    }
}
