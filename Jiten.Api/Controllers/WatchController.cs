using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Jiten.Core.Data.YouTube;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Swashbuckle.AspNetCore.Annotations;
using System.Text.Json;

namespace Jiten.Api.Controllers;

/// <summary>
/// Watch mode for video decks. The transcript is only ever served as a small window around the playback
/// position, to a logged-in browser session, for videos the ledger still lists as playable: a playback aid,
/// never a copy of the subtitles. Cues are parsed per request; the word cache makes that cheap.
/// </summary>
[ApiController]
[Route("api/watch")]
[Authorize(Policy = "RequiresAccountSession")]
[EnableRateLimiting("fixed")]
[ApiExplorerSettings(IgnoreApi = true)]
public class WatchController(
    JitenDbContext context,
    IDbContextFactory<JitenDbContext> contextFactory,
    ICurrentUserService currentUserService,
    IFrequencySourceResolver frequencySource,
    IConnectionMultiplexer redis) : ControllerBase
{
    private const int LinesBefore = 4;
    private const int LinesAfter = 12;
    private const int MaxBuckets = 120;
    private static readonly TimeSpan ParseCacheTtl = TimeSpan.FromHours(1);

    [HttpGet("{deckId:int}")]
    [SwaggerOperation(Summary = "Video identity and line count for watch mode; no text")]
    public async Task<IResult> GetInfo(int deckId)
    {
        var loaded = await LoadAsync(deckId);
        if (loaded == null)
            return Results.NotFound();

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Results.Ok(new
        {
            loaded.Value.Deck.DeckId,
            loaded.Value.Deck.ParentDeckId,
            loaded.Value.VideoId,
            loaded.Value.Deck.RuntimeSeconds,
            CueCount = loaded.Value.Track.GetCues().Count
        });
    }

    /// <summary>Lines around a playback position (ms) or a line index, with the viewer's known state per word.</summary>
    [HttpGet("{deckId:int}/window")]
    public async Task<IResult> GetWindow(int deckId, [FromQuery] int? at, [FromQuery] int? index)
    {
        var loaded = await LoadAsync(deckId);
        if (loaded == null)
            return Results.NotFound();

        var cues = loaded.Value.Track.GetCues();
        if (cues.Count == 0)
            return Results.NotFound();

        var parsed = await ParseCachedAsync(deckId, cues);
        var tokens = parsed.Lines;

        var centre = index ?? IndexAt(cues, at ?? 0);
        centre = Math.Clamp(centre, 0, cues.Count - 1);
        var from = Math.Max(0, centre - LinesBefore);
        var to = Math.Min(cues.Count - 1, centre + LinesAfter);

        var lines = new List<WatchCueDto>();
        for (var i = from; i <= to; i++)
        {
            lines.Add(new WatchCueDto
            {
                Index = i,
                Start = cues[i].StartMs,
                End = cues[i].EndMs,
                Text = cues[i].Text,
                Tokens = i < tokens.Count ? tokens[i] : []
            });
        }

        var keys = lines.SelectMany(l => l.Tokens).Select(t => (t[0], (byte)t[1])).Distinct().ToList();
        var words = await DescribeWordsAsync(keys);

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Results.Ok(new { CueCount = cues.Count, Lines = lines, Words = words, Conjugations = parsed.Conjugations });
    }

    /// <summary>Unknown-word counts per time bucket, so the page can draw a heat strip without any text.</summary>
    [HttpGet("{deckId:int}/timeline")]
    public async Task<IResult> GetTimeline(int deckId, [FromQuery] int buckets = 60)
    {
        var loaded = await LoadAsync(deckId);
        if (loaded == null)
            return Results.NotFound();

        buckets = Math.Clamp(buckets, 10, MaxBuckets);
        var cues = loaded.Value.Track.GetCues();
        var tokens = cues.Count > 0 ? (await ParseCachedAsync(deckId, cues)).Lines : [];

        var totalMs = (loaded.Value.Deck.RuntimeSeconds ?? 0) * 1000;
        if (totalMs <= 0 && cues.Count > 0)
            totalMs = cues[^1].EndMs;

        var keys = tokens.SelectMany(t => t).Select(t => (t[0], (byte)t[1])).Distinct().ToList();
        var knownStates = await currentUserService.GetKnownWordsState(keys);
        var counts = new int[buckets];
        // Start of the first unknown-bearing line per bucket, so a click lands on the line rather than mid-bucket
        var starts = new int[buckets];
        Array.Fill(starts, -1);
        var unknownTotal = 0;
        var seen = new HashSet<(int, byte)>();

        for (var i = 0; i < cues.Count && totalMs > 0; i++)
        {
            var unknown = 0;
            foreach (var token in tokens[i])
            {
                var key = (token[0], (byte)token[1]);
                if (knownStates.TryGetValue(key, out var states) && !IsUnknown(states))
                    continue;
                unknown++;
                if (seen.Add(key))
                    unknownTotal++;
            }
            if (unknown == 0)
                continue;
            var bucket = Math.Min(buckets - 1, (int)((long)cues[i].StartMs * buckets / totalMs));
            counts[bucket] += unknown;
            if (starts[bucket] < 0)
                starts[bucket] = cues[i].StartMs;
        }

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Results.Ok(new { TotalMs = totalMs, Counts = counts, Starts = starts, UnknownWords = unknownTotal });
    }

    private static bool IsUnknown(List<KnownState> states) => states.Count == 0 || (states.Count == 1 && states[0] == KnownState.New);

    private static int IndexAt(List<SubtitleCue> cues, int atMs)
    {
        var lo = 0;
        var hi = cues.Count - 1;
        var best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (cues[mid].StartMs <= atMs)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }

    /// <summary>Only a video the ledger still lists as fetched or imported gets its transcript served.</summary>
    private async Task<(Deck Deck, DeckSubtitleTrack Track, string VideoId)?> LoadAsync(int deckId)
    {
        var deck = await context.Decks.AsNoTracking()
                                .Include(d => d.SubtitleTrack)
                                .FirstOrDefaultAsync(d => d.DeckId == deckId && d.MediaType == MediaType.YouTube);
        if (deck?.SubtitleTrack == null)
            return null;

        var ledger = await context.YouTubeVideos.AsNoTracking()
                                  .Where(v => v.ChildDeckId == deckId)
                                  .Select(v => new { v.VideoId, v.Status })
                                  .FirstOrDefaultAsync();
        if (ledger == null || ledger.Status is not (YouTubeVideoStatus.Imported or YouTubeVideoStatus.Fetched))
            return null;

        return (deck, deck.SubtitleTrack, ledger.VideoId);
    }

    private async Task<Dictionary<string, WatchWordDto>> DescribeWordsAsync(List<(int WordId, byte ReadingIndex)> keys)
    {
        var wordIds = keys.Select(k => k.WordId).Distinct().ToList();
        var forms = await WordFormHelper.LoadWordForms(context, wordIds);
        var frequencies = await frequencySource.LoadFrequencies(context, wordIds);
        var knownStates = await currentUserService.GetKnownWordsState(keys);

        var words = new Dictionary<string, WatchWordDto>();
        foreach (var (wordId, readingIndex) in keys)
        {
            var form = forms.GetValueOrDefault((wordId, (short)readingIndex));
            var frequency = frequencies.Resolve(wordId, (short)readingIndex);
            knownStates.TryGetValue((wordId, readingIndex), out var states);
            words[$"{wordId}-{readingIndex}"] = new WatchWordDto
            {
                WordId = wordId,
                ReadingIndex = readingIndex,
                Spelling = form?.Text ?? "",
                Reading = form?.RubyText ?? "",
                FrequencyRank = frequency.Rank,
                KnownStates = states ?? [KnownState.New]
            };
        }
        return words;
    }

    private sealed class SubtitleTokens
    {
        /// <summary>Per cue, [wordId, readingIndex, start, length, conjugationIndex] per token; -1 when unconjugated</summary>
        public List<List<int[]>> Lines { get; set; } = [];
        public List<List<string>> Conjugations { get; set; } = [];
    }

    // Redis keeps a fresh parse for an hour so seeking through a video does not reparse the whole transcript per window
    private async Task<SubtitleTokens> ParseCachedAsync(int deckId, List<SubtitleCue> cues)
    {
        var key = $"jiten:watch:tokens:v1:{deckId}";
        IDatabase? db = null;
        try
        {
            db = redis.GetDatabase();
            var cached = await db.StringGetAsync(key);
            if (cached.HasValue)
            {
                var hit = JsonSerializer.Deserialize<SubtitleTokens>(cached!);
                if (hit != null && hit.Lines.Count == cues.Count)
                    return hit;
            }
        }
        catch
        {
            // A cache outage only costs a reparse
        }

        var parsed = await ParseAsync(cues);
        if (db != null)
        {
            try { await db.StringSetAsync(key, JsonSerializer.Serialize(parsed), ParseCacheTtl); }
            catch
            {
                // Same: the response does not depend on the write
            }
        }
        return parsed;
    }

    private async Task<SubtitleTokens> ParseAsync(List<SubtitleCue> cues)
    {
        var paragraphs = await ParagraphParser.ParseAsync(contextFactory, cues.Select(c => c.Text).ToArray());

        var tokens = new SubtitleTokens { Lines = new List<List<int[]>>(cues.Count) };
        var chainIndex = new Dictionary<string, int>();
        for (var i = 0; i < cues.Count; i++)
        {
            var cueTokens = new List<int[]>();
            var text = cues[i].Text;
            var position = 0;
            foreach (var word in paragraphs[i])
            {
                var at = text.IndexOf(word.OriginalText, position, StringComparison.Ordinal);
                if (at < 0)
                    continue;
                var chain = word.Conjugations.Where(c => c.Length > 0 && !c.StartsWith('(')).ToList();
                var conjugation = -1;
                if (chain.Count > 0)
                {
                    var key = string.Join('', chain);
                    if (!chainIndex.TryGetValue(key, out conjugation))
                    {
                        conjugation = tokens.Conjugations.Count;
                        tokens.Conjugations.Add(chain);
                        chainIndex[key] = conjugation;
                    }
                }
                cueTokens.Add([word.WordId, word.ReadingIndex, at, word.OriginalText.Length, conjugation]);
                position = at + word.OriginalText.Length;
            }
            tokens.Lines.Add(cueTokens);
        }

        return tokens;
    }
}

public class WatchCueDto
{
    public int Index { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
    public string Text { get; set; } = "";
    /// <summary>[wordId, readingIndex, start, length, conjugationIndex] per token; -1 when unconjugated</summary>
    public List<int[]> Tokens { get; set; } = new();
}

public class WatchWordDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Spelling { get; set; } = "";
    public string Reading { get; set; } = "";
    public int? FrequencyRank { get; set; }
    public List<KnownState> KnownStates { get; set; } = new();
}
