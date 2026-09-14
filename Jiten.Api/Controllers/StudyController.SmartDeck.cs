using System.Text.Json;
using Hangfire;
using Jiten.Api.Authorization;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services.SmartDeck;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Core.Services.SmartDeck;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public partial class StudyController
{
    private const string SmartDeckFeature = "smart-deck";
    private const string SmartDeckName = "Smart Deck";
    private static readonly TimeSpan SmartUnitReportTtl = TimeSpan.FromHours(1);

    [HttpGet("smart-deck")]
    [SwaggerOperation(Summary = "Smart Deck settings, driving titles and state")]
    public async Task<IResult> GetSmartDeck([FromQuery] SmartDeckPreviewQuery preview)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var limits = await userLimits.GetLimitsAsync(userId);
        var deck = await userContext.UserStudyDecks.AsNoTracking()
                                    .FirstOrDefaultAsync(sd => sd.UserId == userId && sd.DeckType == StudyDeckType.Smart);
        var settings = await LoadSmartSettings(userId);
        if (preview.WeighPlanning.HasValue) settings = settings with { WeighPlanning = preview.WeighPlanning.Value };
        if (preview.LookaheadUnits.HasValue) settings = settings with { LookaheadUnits = preview.LookaheadUnits.Value };
        if (preview.TargetPercentage.HasValue) settings = settings with { TargetPercentage = preview.TargetPercentage.Value };
        if (!string.IsNullOrWhiteSpace(preview.SequenceOverrides))
        {
            try
            {
                var overrides = JsonSerializer.Deserialize<Dictionary<int, bool>>(preview.SequenceOverrides);
                if (overrides != null) settings = settings with { SequenceOverrides = overrides };
            }
            catch (JsonException)
            {
                return Results.BadRequest("sequenceOverrides must be a JSON object of deck id to boolean.");
            }
        }
        if (preview.PosFilter != null && !IsValidPosFilter(preview.PosFilter))
            return Results.BadRequest("PosFilter must be a valid JSON array of strings.");
        settings = settings.Normalized();
        var sources = await smartDeckBuilder.LoadSources(userId, settings);

        SmartDeckPreviewCountDto? previewCount = null;
        if (preview.IsPreview && limits.IsPlus)
        {
            var filters = new SmartDeckWordFilters(
                preview.ExcludeKana ?? deck?.ExcludeKana ?? false,
                preview.MinGlobalFrequency ?? deck?.MinGlobalFrequency,
                preview.MaxGlobalFrequency ?? deck?.MaxGlobalFrequency,
                preview.PosFilter ?? deck?.PosFilter);
            var counted = await smartDeckBuilder.CountPreview(userId, sources, filters);
            previewCount = new SmartDeckPreviewCountDto { Words = counted.Words, NewWords = counted.NewWords, WindowWords = counted.WindowWords };
        }

        var dto = new SmartDeckStatusDto
        {
            Locked = !limits.IsPlus,
            Exists = deck != null,
            IsActive = deck?.IsActive ?? false,
            UserStudyDeckId = deck?.UserStudyDeckId,
            Settings = sources.Settings,
            ExcludeKana = deck?.ExcludeKana ?? false,
            MinGlobalFrequency = deck?.MinGlobalFrequency,
            MaxGlobalFrequency = deck?.MaxGlobalFrequency,
            PosFilter = deck?.PosFilter,
            Titles = await BuildTitleDtos(sources),
            ListedTitles = await BuildListedTitleDtos(settings, sources),
            TitlesBeyondCap = sources.TitlesBeyondCap,
            WordCount = deck == null ? 0 : await userContext.UserStudyDeckWords.CountAsync(w => w.UserStudyDeckId == deck.UserStudyDeckId),
            LastRebuiltAt = deck == null ? null : await smartDeckBuilder.GetLastRebuilt(userId),
            Building = deck != null && await smartDeckDirty.IsRebuildPending(userId),
            Preview = previewCount,
        };
        return Results.Ok(dto);
    }

    /// <summary>Unsaved dialog values; nulls mean "as stored". Bound from the query string.</summary>
    public sealed class SmartDeckPreviewQuery
    {
        public bool? WeighPlanning { get; set; }
        public int? LookaheadUnits { get; set; }
        public int? TargetPercentage { get; set; }
        public string? SequenceOverrides { get; set; }
        public bool? ExcludeKana { get; set; }
        public int? MinGlobalFrequency { get; set; }
        public int? MaxGlobalFrequency { get; set; }
        public string? PosFilter { get; set; }

        public bool IsPreview => WeighPlanning.HasValue || LookaheadUnits.HasValue || TargetPercentage.HasValue
                                 || SequenceOverrides != null || ExcludeKana.HasValue || MinGlobalFrequency.HasValue
                                 || MaxGlobalFrequency.HasValue || PosFilter != null;
    }

    [HttpGet("smart-deck/promo")]
    [SwaggerOperation(Summary = "Whether the Smart Deck nudge on the decks page is still worth showing")]
    public async Task<IResult> GetSmartDeckPromo()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();
        var settings = await LoadSmartSettings(userId);
        return Results.Ok(new { dismissed = settings.PromoDismissed || settings.Enabled });
    }

    [HttpPost("smart-deck/promo/dismiss")]
    [SwaggerOperation(Summary = "Hide the Smart Deck nudge for good")]
    public async Task<IResult> DismissSmartDeckPromo()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();
        var settings = await LoadSmartSettings(userId);
        await SaveSmartSettings(userId, settings with { PromoDismissed = true });
        await userContext.SaveChangesAsync();
        return Results.Ok();
    }

    [HttpPut("smart-deck/settings")]
    [JitenPlus(Feature = SmartDeckFeature)]
    [SwaggerOperation(Summary = "Replace the Smart Deck settings and word filters; the first save creates the deck")]
    public async Task<IResult> UpdateSmartDeckSettings([FromBody] SmartDeckSettingsRequest request)
    {
        var userId = currentUserService.UserId!;

        if (!IsValidPosFilter(request.PosFilter))
            return Results.BadRequest("PosFilter must be a valid JSON array of strings.");
        if (request.MaxGlobalFrequency is > 0 && request.MinGlobalFrequency > request.MaxGlobalFrequency)
            return Results.BadRequest("MinGlobalFrequency cannot exceed MaxGlobalFrequency.");

        var settings = new SmartDeckSettings
        {
            Enabled = true,
            PromoDismissed = true,
            WeighPlanning = request.WeighPlanning,
            LookaheadUnits = request.LookaheadUnits,
            TargetPercentage = request.TargetPercentage,
            RecencyHalfLifeDays = request.RecencyHalfLifeDays,
            PinnedDeckIds = request.PinnedDeckIds,
            IncludedDeckIds = request.IncludedDeckIds,
            ExcludedDeckIds = request.ExcludedDeckIds,
            SequenceOverrides = request.SequenceOverrides,
        }.Normalized();

        var deck = await userContext.UserStudyDecks
                                    .FirstOrDefaultAsync(sd => sd.UserId == userId && sd.DeckType == StudyDeckType.Smart);
        if (deck == null)
        {
            var nextSort = await userContext.UserStudyDecks.Where(sd => sd.UserId == userId).Select(sd => (int?)sd.SortOrder).MaxAsync() ?? -1;
            deck = new UserStudyDeck
            {
                UserId = userId, DeckType = StudyDeckType.Smart, Name = SmartDeckName, SortOrder = nextSort + 1, IsActive = true,
                Order = (int)DeckOrder.ImportOrder,
            };
            userContext.UserStudyDecks.Add(deck);
        }
        deck.ExcludeKana = request.ExcludeKana;
        deck.MinGlobalFrequency = request.MinGlobalFrequency;
        deck.MaxGlobalFrequency = request.MaxGlobalFrequency;
        deck.PosFilter = request.PosFilter;

        await SaveSmartSettings(userId, settings);
        await userContext.SaveChangesAsync();

        smartDeckBuilder.ForgetSources(userId);
        await smartDeckDirty.SetEnabled(userId, true);
        await deckMembership.Invalidate(userId, deck.UserStudyDeckId);
        await sessionService.BumpStudyOverviewVersion(userId);
        await ScheduleRebuild(userId);

        return Results.Ok(settings);
    }

    [HttpPost("smart-deck/sources/{deckId:int}")]
    [JitenPlus(Feature = SmartDeckFeature)]
    [SwaggerOperation(Summary = "Pin, unpin, include, exclude or clear one title")]
    public async Task<IResult> ApplySmartDeckSourceAction(int deckId, [FromBody] SmartDeckSourceActionRequest request)
    {
        var userId = currentUserService.UserId!;

        var isParent = await context.Decks.AsNoTracking().AnyAsync(d => d.DeckId == deckId && d.ParentDeckId == null);
        if (!isParent) return Results.NotFound("Only top-level titles can drive the Smart Deck.");

        var settings = await LoadSmartSettings(userId);
        var pinned = settings.PinnedDeckIds.Where(id => id != deckId).ToList();
        var included = settings.IncludedDeckIds.Where(id => id != deckId).ToList();
        var excluded = settings.ExcludedDeckIds.Where(id => id != deckId).ToList();

        switch (request.Action?.Trim().ToLowerInvariant())
        {
            case "pin":
                if (pinned.Count >= SmartDeckConstants.MaxPins)
                    return Results.BadRequest($"You can pin up to {SmartDeckConstants.MaxPins} titles.");
                pinned.Add(deckId);
                if (settings.IncludedDeckIds.Contains(deckId)) included.Add(deckId);
                break;
            case "unpin":
                if (settings.IncludedDeckIds.Contains(deckId)) included.Add(deckId);
                break;
            case "include":
                included.Add(deckId);
                break;
            case "exclude":
                excluded.Add(deckId);
                break;
            case "clear":
                break;
            default:
                return Results.BadRequest("Action must be pin, unpin, include, exclude or clear.");
        }

        var updated = (settings with { PinnedDeckIds = pinned, IncludedDeckIds = included, ExcludedDeckIds = excluded }).Normalized();
        await SaveSmartSettings(userId, updated);
        await userContext.SaveChangesAsync();
        smartDeckBuilder.ForgetSources(userId);
        if (updated.Enabled)
        {
            await smartDeckDirty.SetEnabled(userId, true);
            await ScheduleRebuild(userId);
        }

        return Results.Ok(updated);
    }

    [HttpPost("smart-deck/rebuild")]
    [JitenPlus(Feature = SmartDeckFeature)]
    [EnableRateLimiting("smart-deck-rebuild")]
    [SwaggerOperation(Summary = "Queue an immediate rebuild")]
    public async Task<IResult> RebuildSmartDeck()
    {
        var userId = currentUserService.UserId!;
        var settings = await LoadSmartSettings(userId);
        if (!settings.Enabled) return Results.BadRequest("Set up the Smart Deck first.");

        await smartDeckDirty.SetEnabled(userId, true);
        backgroundJobs.Enqueue<SmartDeckJob>(job => job.Rebuild(userId));
        return Results.Accepted();
    }

    [HttpGet("smart-deck/unit-report/{deckId:int}")]
    [JitenPlus(Feature = SmartDeckFeature)]
    [SwaggerOperation(Summary = "Which of the user's cards appear in one unit, by how far along they are")]
    public async Task<IResult> GetSmartDeckUnitReport(int deckId)
    {
        var userId = currentUserService.UserId!;
        var cacheKey = SmartDeckCacheKeys.UnitReport(userId, deckId);
        var cached = await ReadCached<SmartDeckUnitReportDto>(cacheKey);
        if (cached != null) return Results.Ok(cached);

        var deck = await context.Decks.AsNoTracking()
                                .Where(d => d.DeckId == deckId)
                                .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.ParentDeckId })
                                .FirstOrDefaultAsync();
        if (deck == null) return Results.NotFound();

        var parent = deck.ParentDeckId == null
            ? null
            : await context.Decks.AsNoTracking()
                             .Where(d => d.DeckId == deck.ParentDeckId.Value)
                             .Select(d => new { d.OriginalTitle, d.RomajiTitle, d.EnglishTitle })
                             .FirstOrDefaultAsync();

        var deckWords = await context.DeckWords.AsNoTracking()
                                     .Where(w => w.DeckId == deckId)
                                     .Select(w => new { w.WordId, w.ReadingIndex, w.Occurrences })
                                     .ToListAsync();
        var occurrencesByKey = new Dictionary<long, int>();
        foreach (var w in deckWords)
        {
            var key = WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex);
            occurrencesByKey[key] = occurrencesByKey.GetValueOrDefault(key) + w.Occurrences;
        }

        var wordIds = deckWords.Select(w => w.WordId).Distinct().ToList();
        var cards = wordIds.Count == 0
            ? []
            : await userContext.FsrsCards.AsNoTracking()
                               .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
                               .Select(c => new { c.WordId, c.ReadingIndex, c.State, c.Due, c.LastReview, c.CreatedAt })
                               .ToListAsync();

        var now = DateTime.UtcNow;
        var recentCutoff = now.AddDays(-7);
        var recent = new List<(long Key, int Occ)>();
        var learning = new List<(long Key, int Occ)>();
        var young = new List<(long Key, int Occ)>();
        var mature = new List<(long Key, int Occ)>();
        var tracked = 0;

        foreach (var c in cards)
        {
            var key = WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex);
            if (!occurrencesByKey.TryGetValue(key, out var occ)) continue;
            if (c.State is FsrsState.Blacklisted or FsrsState.Suspended) continue;
            tracked++;
            if (c.CreatedAt >= recentCutoff) recent.Add((key, occ));

            if (c.State is FsrsState.New or FsrsState.Learning or FsrsState.Relearning) learning.Add((key, occ));
            else if (c.State == FsrsState.Mastered) mature.Add((key, occ));
            // Maturity mirrors the deck stats: interval = due - last review, mature at 21 days.
            else if (c.LastReview.HasValue && (c.Due - c.LastReview.Value).TotalDays >= 21) mature.Add((key, occ));
            else young.Add((key, occ));
        }

        static List<long> Top(List<(long Key, int Occ)> bucket) => bucket.OrderByDescending(b => b.Occ).Take(5).Select(b => b.Key).ToList();
        var exampleKeys = Top(recent).Concat(Top(learning)).Concat(Top(young)).Concat(Top(mature)).Distinct().ToList();
        var entries = await LoadDictionaryEntries(exampleKeys);
        List<DictionaryEntryDto> Examples(List<(long Key, int Occ)> bucket) => Top(bucket).Where(entries.ContainsKey).Select(k => entries[k]).ToList();

        var report = new SmartDeckUnitReportDto
        {
            DeckId = deck.DeckId,
            OriginalTitle = deck.OriginalTitle,
            RomajiTitle = deck.RomajiTitle,
            EnglishTitle = deck.EnglishTitle,
            ParentDeckId = deck.ParentDeckId,
            ParentOriginalTitle = parent?.OriginalTitle,
            ParentRomajiTitle = parent?.RomajiTitle,
            ParentEnglishTitle = parent?.EnglishTitle,
            TotalWords = occurrencesByKey.Count,
            TrackedWords = tracked,
            LearnedLast7Days = recent.Count,
            Learning = learning.Count,
            Young = young.Count,
            Mature = mature.Count,
            NotYetStudied = Math.Max(0, occurrencesByKey.Count - tracked),
            LearnedLast7DaysExamples = Examples(recent),
            LearningExamples = Examples(learning),
            YoungExamples = Examples(young),
            MatureExamples = Examples(mature),
            ComputedAt = now,
        };

        await WriteCached(cacheKey, report, SmartUnitReportTtl);
        return Results.Ok(report);
    }

    private async Task ScheduleRebuild(string userId)
    {
        if (!await smartDeckDirty.MarkDirty(userId))
            backgroundJobs.Enqueue<SmartDeckJob>(job => job.Rebuild(userId));
    }

    private async Task ForgetSmartDeck(string userId, UserStudyDeck deck)
    {
        userContext.UserStudyDecks.Remove(deck);
        var row = await userContext.UserSettings.FirstOrDefaultAsync(us => us.UserId == userId);
        if (row != null) row.SmartDeckJson = new SmartDeckSettings { PromoDismissed = true }.Serialize();
        await userContext.SaveChangesAsync();
        smartDeckBuilder.ForgetSources(userId);
        await smartDeckBuilder.ClearLastRebuilt(userId);
        await smartDeckDirty.SetEnabled(userId, false);
    }

    private async Task<SmartDeckSettings> LoadSmartSettings(string userId)
    {
        var json = await userContext.UserSettings.AsNoTracking()
                                    .Where(us => us.UserId == userId)
                                    .Select(us => us.SmartDeckJson)
                                    .FirstOrDefaultAsync();
        return SmartDeckSettings.Parse(json);
    }

    private async Task SaveSmartSettings(string userId, SmartDeckSettings settings)
    {
        var row = await userContext.UserSettings.FirstOrDefaultAsync(us => us.UserId == userId);
        if (row == null)
        {
            row = new UserSettings { UserId = userId };
            userContext.UserSettings.Add(row);
        }
        row.SmartDeckJson = settings.Serialize();
    }

    private async Task<List<SmartDeckTitleDto>> BuildTitleDtos(SmartDeckSources sources)
    {
        if (sources.Titles.Count == 0) return [];

        var parentIds = sources.Titles.Select(t => t.ParentDeckId).ToList();
        var unitIds = sources.Windows.Values.SelectMany(w => w.WindowDeckIds).Distinct().ToList();
        var deckIds = parentIds.Concat(unitIds).ToList();
        var decks = await context.Decks.AsNoTracking()
                                 .Where(d => deckIds.Contains(d.DeckId))
                                 .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.MediaType, d.DeckOrder })
                                 .ToDictionaryAsync(d => d.DeckId);

        return sources.Titles.Where(t => decks.ContainsKey(t.ParentDeckId)).Select(t =>
        {
            var deck = decks[t.ParentDeckId];
            sources.Windows.TryGetValue(t.ParentDeckId, out var window);
            return new SmartDeckTitleDto
            {
                DeckId = t.ParentDeckId,
                OriginalTitle = deck.OriginalTitle,
                RomajiTitle = deck.RomajiTitle,
                EnglishTitle = deck.EnglishTitle,
                CoverName = deck.CoverName,
                MediaType = (int)deck.MediaType,
                Status = t.Status,
                Weight = Math.Round(t.Weight, 3),
                Pinned = t.Pinned,
                Boosted = t.Boosted,
                Planning = t.Planning,
                ManuallyIncluded = t.ManuallyIncluded,
                LastActivity = t.LastActivity,
                CursorDeckId = window?.CursorDeckId,
                Window = window?.WindowDeckIds
                               .Where(decks.ContainsKey)
                               .Select(u => new SmartDeckUnitDto
                               {
                                   DeckId = u, OriginalTitle = decks[u].OriginalTitle, RomajiTitle = decks[u].RomajiTitle,
                                   EnglishTitle = decks[u].EnglishTitle, DeckOrder = decks[u].DeckOrder
                               }).ToList() ?? [],
                CompletedUnits = window?.CompletedUnits ?? 0,
                TotalUnits = window?.TotalUnits ?? 0,
                Sequential = window?.Sequential ?? SmartDeckConstants.IsSequentialByDefault(deck.MediaType),
                WindowSource = (window?.Source ?? SmartDeckWindowSource.None).ToString().ToLowerInvariant(),
            };
        }).ToList();
    }

    private async Task<List<SmartDeckUnitDto>> BuildListedTitleDtos(SmartDeckSettings settings, SmartDeckSources sources)
    {
        var driving = sources.Titles.Select(t => t.ParentDeckId).ToHashSet();
        var ids = settings.PinnedDeckIds.Concat(settings.IncludedDeckIds).Concat(settings.ExcludedDeckIds)
                          .Where(id => !driving.Contains(id)).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await context.Decks.AsNoTracking()
                            .Where(d => ids.Contains(d.DeckId))
                            .Select(d => new SmartDeckUnitDto { DeckId = d.DeckId, OriginalTitle = d.OriginalTitle, RomajiTitle = d.RomajiTitle, EnglishTitle = d.EnglishTitle })
                            .ToListAsync();
    }

    /// <summary>Same form selection as the static word list: the requested form, else the lowest reading index, with a kanji hint for kana forms.</summary>
    private async Task<Dictionary<long, DictionaryEntryDto>> LoadDictionaryEntries(List<long> keys)
    {
        var wordIds = keys.Select(k => (int)(k >> 8)).Distinct().ToList();
        var words = await context.JMDictWords.AsNoTracking()
                                 .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                 .Where(w => wordIds.Contains(w.WordId))
                                 .ToDictionaryAsync(w => w.WordId);
        var forms = await context.WordForms.AsNoTracking().Where(wf => wordIds.Contains(wf.WordId)).ToListAsync();
        RubyTextHelper.EnrichForms(forms);
        var formsByWord = forms.GroupBy(f => f.WordId).ToDictionary(g => g.Key, g => g.ToList());
        var scopedFreqs = await frequencySource.LoadFrequencies(context, wordIds);

        var result = new Dictionary<long, DictionaryEntryDto>();
        foreach (var key in keys)
        {
            var wordId = (int)(key >> 8);
            var readingIndex = (byte)(key & 0xFF);
            if (!words.TryGetValue(wordId, out var word) || !formsByWord.TryGetValue(wordId, out var wordForms) || wordForms.Count == 0) continue;

            var form = wordForms.FirstOrDefault(f => f.ReadingIndex == readingIndex) ?? wordForms.OrderBy(f => f.ReadingIndex).First();
            var kanjiHint = form.FormType == JmDictFormType.KanaForm
                ? wordForms.Where(f => f.FormType == JmDictFormType.KanjiForm && !f.IsSearchOnly).OrderBy(f => f.ReadingIndex).FirstOrDefault()?.RubyText
                : null;
            var firstDef = word.Definitions.Where(d => d.EnglishMeanings.Count > 0).OrderBy(d => d.SenseIndex).FirstOrDefault();
            var rank = scopedFreqs.Rank(wordId, form.ReadingIndex);

            result[key] = new DictionaryEntryDto
            {
                WordId = wordId, ReadingIndex = (byte)form.ReadingIndex, Text = form.Text, RubyText = form.RubyText,
                PrimaryKanjiText = kanjiHint, PartsOfSpeech = word.PartsOfSpeech, Meanings = firstDef?.EnglishMeanings ?? [],
                Senses = DictionarySenseDto.FromDefinitions(word.Definitions),
                FrequencyRank = rank > 0 ? rank : int.MaxValue,
            };
        }
        return result;
    }

    /// <summary>Strongest contributing title per introduced card, so the card can say which show put it in the queue.</summary>
    private async Task<Dictionary<long, SmartDeckReasonDto>> BuildSmartReasons(string userId, List<long> keys)
    {
        var result = new Dictionary<long, SmartDeckReasonDto>();
        if (keys.Count == 0) return result;

        try
        {
            var sources = await smartDeckBuilder.LoadSourcesCached(userId);
            var contributions = await smartDeckBuilder.Explain(sources, keys);
            var deckIds = contributions.Values.Where(c => c.Count > 0).Select(c => c[0])
                                       .SelectMany(c => c.WindowUnitDeckId.HasValue ? new[] { c.ParentDeckId, c.WindowUnitDeckId.Value } : [c.ParentDeckId])
                                       .Distinct().ToList();
            if (deckIds.Count == 0) return result;

            var decks = await context.Decks.AsNoTracking()
                                     .Where(d => deckIds.Contains(d.DeckId))
                                     .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle })
                                     .ToDictionaryAsync(d => d.DeckId);

            foreach (var (key, list) in contributions)
            {
                if (list.Count == 0 || !decks.TryGetValue(list[0].ParentDeckId, out var parent)) continue;
                var top = list[0];
                var unit = top.WindowUnitDeckId.HasValue ? decks.GetValueOrDefault(top.WindowUnitDeckId.Value) : null;
                result[key] = new SmartDeckReasonDto
                {
                    DeckId = top.ParentDeckId, OriginalTitle = parent.OriginalTitle, RomajiTitle = parent.RomajiTitle, EnglishTitle = parent.EnglishTitle,
                    Occurrences = top.Occurrences, UnitDeckId = unit?.DeckId, UnitOriginalTitle = unit?.OriginalTitle,
                    UnitRomajiTitle = unit?.RomajiTitle, UnitEnglishTitle = unit?.EnglishTitle,
                    UnitOccurrences = top.WindowOccurrences, Share = Math.Round(top.Share, 3),
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build smart deck reasons for {UserId}", userId);
        }

        return result;
    }

    private async Task<T?> ReadCached<T>(string cacheKey) where T : class
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(cacheKey);
            return value.HasValue ? JsonSerializer.Deserialize<T>(value!) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read smart deck cache {Key}", cacheKey);
            return null;
        }
    }

    private async Task WriteCached<T>(string cacheKey, T payload, TimeSpan ttl)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(cacheKey, JsonSerializer.Serialize(payload), ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write smart deck cache {Key}", cacheKey);
        }
    }
}
