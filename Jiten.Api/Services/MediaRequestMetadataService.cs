using System.Collections.Concurrent;
using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MetadataProviderHelper = Jiten.Core.MetadataProviderHelper;

namespace Jiten.Api.Services;

/// <summary>Keeps a request's provider metadata in step with its external link.</summary>
public class MediaRequestMetadataService(JitenDbContext context, IBackgroundJobClient backgroundJobs, IMemoryCache memoryCache)
{
    /// <summary>Link changes a requester can make per hour that queue a provider lookup; each one occupies a shared provider queue.</summary>
    public const int RequesterFetchesPerHour = 30;

    private const int MaxTitleLength = 500;
    private const int MaxStoredTitles = 30;

    private static readonly ConcurrentDictionary<string, object> BudgetLocks = new();
    private record FetchWindow(int Count, long WindowEndTicks);

    /// <summary>True when the link points at a different catalogue entry and a provider can read it, so setting it queues a lookup.</summary>
    public static bool WouldQueueFetch(string? previousUrl, string? newUrl) =>
        !SameLink(previousUrl, newUrl) && ExternalMetadataResolver.TryGetProvider(newUrl, out _);

    public bool TryConsumeRequesterBudget(string userId)
    {
        var budgetLock = BudgetLocks.GetOrAdd(userId, _ => new object());
        lock (budgetLock)
        {
            var now = DateTime.UtcNow;
            var key = $"request-metadata-fetches:{userId}";
            if (!memoryCache.TryGetValue(key, out FetchWindow? window) || window == null || now.Ticks >= window.WindowEndTicks)
                window = new FetchWindow(0, now.AddHours(1).Ticks);

            if (window.Count >= RequesterFetchesPerHour)
                return false;

            memoryCache.Set(key, window with { Count = window.Count + 1 }, TimeSpan.FromTicks(window.WindowEndTicks - now.Ticks));
            return true;
        }
    }

    /// <summary>
    /// Clears metadata that belonged to the previous link and sets the status for the new one. Call before saving; queue the
    /// lookup with <see cref="QueueFetch"/> once the request has an id.
    /// </summary>
    /// <returns>True when a lookup should be queued.</returns>
    public async Task<bool> ApplyLinkChangeAsync(MediaRequest request, string? previousUrl)
    {
        if (request.Id != 0 && SameLink(previousUrl, request.ExternalUrl))
            return false;

        if (request.Id != 0)
            await LoadMetadataCollections(context, request);

        ClearMetadata(request);

        if (string.IsNullOrWhiteSpace(request.ExternalUrl))
        {
            request.MetadataStatus = MediaRequestMetadataStatus.None;
            return false;
        }

        if (!ExternalMetadataResolver.TryGetProvider(request.ExternalUrl, out _))
        {
            request.MetadataStatus = MediaRequestMetadataStatus.Unsupported;
            return false;
        }

        request.MetadataStatus = MediaRequestMetadataStatus.Pending;
        return true;
    }

    /// <summary>Queues a lookup on the provider's own single-worker queue, which spaces calls to that provider across all callers.</summary>
    public bool QueueFetch(int requestId, string? url)
    {
        if (!ExternalMetadataResolver.TryGetProvider(url, out var provider))
            return false;

        var link = url!;
        switch (provider)
        {
            case LinkType.Anilist:
                backgroundJobs.Enqueue<MediaRequestMetadataJob>(job => job.FetchAnilist(requestId, link));
                break;
            case LinkType.Mal:
                backgroundJobs.Enqueue<MediaRequestMetadataJob>(job => job.FetchJikan(requestId, link));
                break;
            case LinkType.Vndb:
                backgroundJobs.Enqueue<MediaRequestMetadataJob>(job => job.FetchVndb(requestId, link));
                break;
            case LinkType.Tmdb:
                backgroundJobs.Enqueue<MediaRequestMetadataJob>(job => job.FetchTmdb(requestId, link));
                break;
            case LinkType.Igdb:
                backgroundJobs.Enqueue<MediaRequestMetadataJob>(job => job.FetchIgdb(requestId, link));
                break;
            case LinkType.GoogleBooks or LinkType.Bookmeter:
                backgroundJobs.Enqueue<MediaRequestMetadataJob>(job => job.FetchBooks(requestId, link));
                break;
            case LinkType.Syosetsu:
                backgroundJobs.Enqueue<MediaRequestMetadataJob>(job => job.FetchSyosetu(requestId, link));
                break;
            default:
                return false;
        }

        return true;
    }

    /// <summary>Replaces the request's metadata with what the provider returned; genres and tags go through the same mappings as decks.</summary>
    public static async Task ApplyAsync(JitenDbContext context, MediaRequest request, Metadata metadata, LinkType provider)
    {
        await LoadMetadataCollections(context, request);

        var original = CleanTitle(metadata.OriginalTitle);
        if (string.Equals(original, "Unknown", StringComparison.Ordinal))
            original = null;

        var romaji = CleanTitle(metadata.RomajiTitle);
        if (SameTitle(romaji, original))
            romaji = null;

        var english = CleanTitle(metadata.EnglishTitle);
        if (SameTitle(english, original) || SameTitle(english, romaji))
            english = null;

        request.OriginalTitle = original;
        request.RomajiTitle = romaji;
        request.EnglishTitle = english;
        request.ReleaseDate = metadata.ReleaseDate.HasValue ? DateOnly.FromDateTime(metadata.ReleaseDate.Value) : null;

        var mapped = await MetadataProviderHelper.MapGenresAndTags(context, metadata, provider);

        // Diffed rather than cleared and re-added: a removed and a re-added row share a key, which the change tracker rejects.
        foreach (var stale in request.Genres.Where(g => !mapped.Genres.Contains(g.Genre)).ToList())
            request.Genres.Remove(stale);
        foreach (var genre in mapped.Genres.Where(g => request.Genres.All(existing => existing.Genre != g)))
            request.Genres.Add(new MediaRequestGenre { MediaRequestId = request.Id, Genre = genre });

        foreach (var stale in request.Tags.Where(t => !mapped.Tags.ContainsKey(t.TagId)).ToList())
            request.Tags.Remove(stale);
        foreach (var (tagId, percentage) in mapped.Tags)
        {
            var existing = request.Tags.FirstOrDefault(t => t.TagId == tagId);
            if (existing != null)
                existing.Percentage = percentage;
            else
                request.Tags.Add(new MediaRequestTag { MediaRequestId = request.Id, TagId = tagId, Percentage = percentage });
        }

        SyncTitles(request, metadata);
        SyncLinks(request, metadata);

        request.MetadataStatus = MediaRequestMetadataStatus.Fetched;
        request.MetadataFetchedAt = DateTime.UtcNow;
    }

    private static async Task LoadMetadataCollections(JitenDbContext context, MediaRequest request)
    {
        var entry = context.Entry(request);
        if (entry.State == EntityState.Added || entry.State == EntityState.Detached)
            return;

        if (!entry.Collection(r => r.Genres).IsLoaded)
            await entry.Collection(r => r.Genres).LoadAsync();
        if (!entry.Collection(r => r.Tags).IsLoaded)
            await entry.Collection(r => r.Tags).LoadAsync();
        if (!entry.Collection(r => r.Titles).IsLoaded)
            await entry.Collection(r => r.Titles).LoadAsync();
        if (!entry.Collection(r => r.Links).IsLoaded)
            await entry.Collection(r => r.Links).LoadAsync();
    }

    /// <summary>Every name the provider gives, deduplicated by <see cref="TitleNormalizer"/>, so duplicate checks can match any of them.</summary>
    private static void SyncTitles(MediaRequest request, Metadata metadata)
    {
        var wanted = new List<(string Title, DeckTitleType Type)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? title, DeckTitleType type)
        {
            var cleaned = CleanTitle(title);
            if (cleaned == null || wanted.Count >= MaxStoredTitles)
                return;

            var key = TitleNormalizer.Normalize(cleaned);
            if (key.Length > 0 && seen.Add(key))
                wanted.Add((cleaned, type));
        }

        Add(request.OriginalTitle, DeckTitleType.Original);
        Add(request.RomajiTitle, DeckTitleType.Romaji);
        Add(request.EnglishTitle, DeckTitleType.English);
        foreach (var alias in metadata.Aliases)
            Add(alias, DeckTitleType.Alias);

        foreach (var stale in request.Titles.Where(t => !wanted.Any(w => w.Title == t.Title && w.Type == t.TitleType)).ToList())
            request.Titles.Remove(stale);
        foreach (var (title, type) in wanted.Where(w => !request.Titles.Any(t => t.Title == w.Title && t.TitleType == w.Type)))
            request.Titles.Add(new MediaRequestTitle { MediaRequestId = request.Id, Title = title, TitleType = type });
    }

    private static void SyncLinks(MediaRequest request, Metadata metadata)
    {
        var wanted = metadata.Links
                             .Where(l => !string.IsNullOrWhiteSpace(l.Url) && l.Url.Length <= 500 && ExternalUrlParser.TryGetEntityKey(l.Url, out _))
                             .GroupBy(l => l.Url.Trim())
                             .Select(g => (Url: g.Key, g.First().LinkType))
                             .ToList();

        foreach (var stale in request.Links.Where(l => !wanted.Any(w => w.Url == l.Url)).ToList())
            request.Links.Remove(stale);
        foreach (var (url, linkType) in wanted.Where(w => request.Links.All(l => l.Url != w.Url)))
            request.Links.Add(new MediaRequestLink { MediaRequestId = request.Id, Url = url, LinkType = linkType });
    }

    private static void ClearMetadata(MediaRequest request)
    {
        request.OriginalTitle = null;
        request.RomajiTitle = null;
        request.EnglishTitle = null;
        request.ReleaseDate = null;
        request.MetadataFetchedAt = null;
        request.Genres.Clear();
        request.Tags.Clear();
        request.Titles.Clear();
        request.Links.Clear();
        request.PossibleDuplicateOfRequestId = null;
        request.PossibleDuplicateDeckId = null;
        request.DuplicateFlagDismissed = false;
    }

    private static bool SameLink(string? a, string? b) =>
        string.Equals(a?.Trim() ?? string.Empty, b?.Trim() ?? string.Empty, StringComparison.Ordinal);

    private static bool SameTitle(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? CleanTitle(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length > MaxTitleLength ? trimmed[..MaxTitleLength] : trimmed;
    }
}
