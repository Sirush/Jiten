using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>Builds the public /stats snapshot. Both contexts are queried separately and joined in memory, so it runs on any provider.</summary>
public class SiteStatsBuilder(IDbContextFactory<JitenDbContext> jitenFactory, IDbContextFactory<UserDbContext> userFactory, ILogger<SiteStatsBuilder> logger)
{
    public const int ActiveWindowDays = 30;
    public const int LogWindowDays = 90;
    public const int MinLearnersPerSection = 20;
    public const int MinLearnersPerStudiedDeck = 10;
    public const int StudiedDeckCount = 12;
    public const int MinLearnersPerForgottenWord = 20;
    public const int MinReviewsPerForgottenWord = 200;
    public const int ForgottenWordCount = 25;
    public const int MinLearnersPerReadingType = 20;

    /// <summary>The name sets hold hundreds of thousands of proper nouns, which aren't vocabulary a learner knows.</summary>
    public static readonly string[] NameSetSlugs = ["names", "people", "places", "other-names"];

    public static readonly int[] AtLeastThresholds = [250, 500, 1000, 2000, 3000, 5000, 7500, 10000, 15000];

    public static readonly (int From, int To)[] FrequencyBands =
    [
        (1, 100), (101, 250), (251, 500), (501, 1000), (1001, 2000), (2001, 3000),
        (3001, 5000), (5001, 7500), (7501, 10000), (10001, 15000), (15001, 20000), (20001, 30000),
        (30001, 50000), (50001, 75000)
    ];

    private static readonly FsrsState[] KnownCardStates = [FsrsState.Review, FsrsState.Relearning, FsrsState.Mastered];

    public async Task<SiteStatsDto> BuildAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(utcNow);
        var dto = new SiteStatsDto { Date = today, ComputedAt = utcNow };

        var activity = await BuildActivity(today, ct);
        dto.Totals.Learners = activity.Learners;
        dto.Totals.Reviews = activity.ReviewsSinceSignup;
        dto.Totals.HoursReviewing = activity.MillisecondsSinceSignup / 3_600_000;
        dto.Last30Days = activity.Last30Days;
        dto.LearnersByMonth = activity.LearnersByMonth;

        var known = await BuildKnownWords(activity.ActiveUsers, ct);
        dto.Totals.WordsKnown = known.TotalKnown;
        dto.KnownWords = known.Section;

        dto.FrequencyBands = await BuildFrequencyBands(activity.ActiveUsers, ct);
        dto.Heatmap = await BuildHeatmap(utcNow, ct);

        var reading = await BuildReading(ct);
        dto.Reading = reading.Rows;
        dto.Totals.TitlesFinished = reading.TitlesFinished;
        dto.Totals.CharactersRead = reading.CharactersRead;

        dto.Studying = await BuildStudying(utcNow, ct);
        dto.Forgotten = await BuildForgotten(utcNow, ct);
        dto.Library = await BuildLibrary(utcNow, ct);

        logger.LogInformation("Site stats built in {Elapsed:F1}s", (DateTime.UtcNow - utcNow).TotalSeconds);
        return dto;
    }

    private sealed record Activity(
        int Learners,
        long ReviewsSinceSignup,
        long MillisecondsSinceSignup,
        SiteStatsLast30DaysDto Last30Days,
        List<SiteStatsMonthPointDto> LearnersByMonth,
        HashSet<string> ActiveUsers);

    private async Task<Activity> BuildActivity(DateOnly today, CancellationToken ct)
    {
        await using var userContext = await userFactory.CreateDbContextAsync(ct);

        var signups = await userContext.Users.AsNoTracking()
                                       .Select(u => new { u.Id, u.CreatedAt })
                                       .ToDictionaryAsync(u => u.Id, u => DateOnly.FromDateTime(u.CreatedAt), ct);

        var windowStart = today.AddDays(-ActiveWindowDays);
        var learners = new HashSet<string>();
        var active = new HashSet<string>();
        var studyDayReviews = new List<double>();
        long reviewsSinceSignup = 0, msSinceSignup = 0, windowReviews = 0, windowNewWords = 0;

        var rows = userContext.UserReviewDailies.AsNoTracking()
                              .Where(d => d.ReviewCount > 0)
                              .Select(d => new { d.UserId, d.LocalDate, d.ReviewCount, d.NewCardCount, d.TotalDurationMs })
                              .AsAsyncEnumerable();

        await foreach (var row in rows.WithCancellation(ct))
        {
            learners.Add(row.UserId);

            // Anki imports write history onto its original dates; only days after signup were tracked here.
            if (signups.TryGetValue(row.UserId, out var signup) && row.LocalDate >= signup)
            {
                reviewsSinceSignup += row.ReviewCount;
                msSinceSignup += row.TotalDurationMs;
            }

            if (row.LocalDate >= windowStart)
            {
                active.Add(row.UserId);
                windowReviews += row.ReviewCount;
                windowNewWords += row.NewCardCount;
                studyDayReviews.Add(row.ReviewCount);
            }
        }

        var byMonth = learners.Where(signups.ContainsKey)
                              .GroupBy(id => new DateOnly(signups[id].Year, signups[id].Month, 1))
                              .OrderBy(g => g.Key)
                              .Select(g => (g.Key, Count: g.Count()))
                              .ToList();
        long running = 0;
        var learnersByMonth = byMonth.Select(m => new SiteStatsMonthPointDto { Month = m.Key, Total = running += m.Count }).ToList();

        return new Activity(
            learners.Count,
            reviewsSinceSignup,
            msSinceSignup,
            new SiteStatsLast30DaysDto
            {
                PeopleReviewing = active.Count,
                Reviews = windowReviews,
                NewWords = windowNewWords,
                MedianReviewsPerStudyDay = Percentile(studyDayReviews, 0.5)
            },
            learnersByMonth,
            active);
    }

    private sealed record KnownWords(long TotalKnown, SiteStatsKnownWordsDto? Section);

    /// <summary>Known = card in Review, Relearning or Mastered, or (no card at all) a word in a Mastered set outside the name sets.</summary>
    private async Task<KnownWords> BuildKnownWords(HashSet<string> activeUsers, CancellationToken ct)
    {
        await using var userContext = await userFactory.CreateDbContextAsync(ct);
        await using var jitenContext = await jitenFactory.CreateDbContextAsync(ct);

        var fromCards = await userContext.FsrsCards.AsNoTracking()
                                         .Where(c => KnownCardStates.Contains(c.State))
                                         .GroupBy(c => c.UserId)
                                         .Select(g => new { UserId = g.Key, Count = g.Count() })
                                         .ToDictionaryAsync(g => g.UserId, g => (long)g.Count, ct);

        var vocabularySetIds = await jitenContext.WordSets.AsNoTracking()
                                                 .Where(s => !NameSetSlugs.Contains(s.Slug))
                                                 .Select(s => s.SetId)
                                                 .ToListAsync(ct);
        var masteredSets = await userContext.UserWordSetStates.AsNoTracking()
                                            .Where(s => s.State == WordSetStateType.Mastered && vocabularySetIds.Contains(s.SetId))
                                            .Select(s => new { s.UserId, s.SetId })
                                            .ToListAsync(ct);

        var fromSets = new Dictionary<string, long>();
        if (masteredSets.Count > 0)
        {
            var setIds = masteredSets.Select(s => s.SetId).Distinct().ToList();
            var members = await jitenContext.WordSetMembers.AsNoTracking()
                                            .Where(m => setIds.Contains(m.SetId))
                                            .Select(m => new { m.SetId, m.WordId, m.ReadingIndex })
                                            .ToListAsync(ct);
            var membersBySet = members.GroupBy(m => m.SetId)
                                      .ToDictionary(g => g.Key, g => g.Select(m => (m.WordId, (int)m.ReadingIndex)).ToList());
            var memberWordIds = members.Select(m => m.WordId).Distinct().ToList();
            var setUsers = masteredSets.Select(s => s.UserId).Distinct().ToList();

            var carded = new HashSet<(string, int, int)>();
            var cards = userContext.FsrsCards.AsNoTracking()
                                   .Where(c => setUsers.Contains(c.UserId) && memberWordIds.Contains(c.WordId))
                                   .Select(c => new { c.UserId, c.WordId, c.ReadingIndex })
                                   .AsAsyncEnumerable();
            await foreach (var c in cards.WithCancellation(ct))
                carded.Add((c.UserId, c.WordId, c.ReadingIndex));

            foreach (var user in masteredSets.GroupBy(s => s.UserId))
            {
                var words = user.SelectMany(s => membersBySet.GetValueOrDefault(s.SetId) ?? [])
                                .Where(w => !carded.Contains((user.Key, w.WordId, w.Item2)))
                                .Distinct()
                                .Count();
                fromSets[user.Key] = words;
            }
        }

        var total = fromCards.Values.Sum() + fromSets.Values.Sum();
        if (activeUsers.Count < MinLearnersPerSection)
            return new KnownWords(total, null);

        var perUser = activeUsers.Select(u => (double)(fromCards.GetValueOrDefault(u) + fromSets.GetValueOrDefault(u))).ToList();
        return new KnownWords(total, new SiteStatsKnownWordsDto
        {
            Learners = perUser.Count,
            Median = Percentile(perUser, 0.5),
            P75 = Percentile(perUser, 0.75),
            AtLeast = AtLeastThresholds.Select(t => new SiteStatsAtLeastDto
            {
                Words = t,
                Share = Math.Round(100.0 * perUser.Count(k => k >= t) / perUser.Count, 1)
            }).ToList()
        });
    }

    [Flags]
    private enum SetSources : byte { None = 0, MasteredVocabulary = 1, MasteredName = 2, Blacklisted = 4 }

    /// <summary>Share of each frequency band a learner knows, with their blacklisted words taken out of their own band.</summary>
    private async Task<List<SiteStatsFrequencyBandDto>> BuildFrequencyBands(HashSet<string> activeUsers, CancellationToken ct)
    {
        if (activeUsers.Count < MinLearnersPerSection)
            return [];

        await using var userContext = await userFactory.CreateDbContextAsync(ct);
        await using var jitenContext = await jitenFactory.CreateDbContextAsync(ct);

        var maxRank = FrequencyBands[^1].To;
        var forms = await jitenContext.WordFormFrequencies.AsNoTracking()
                                      .Where(f => f.FrequencyRank >= 1 && f.FrequencyRank <= maxRank)
                                      .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank })
                                      .ToListAsync(ct);
        var bandOf = new Dictionary<(int, int), int>(forms.Count);
        var bandSize = new int[FrequencyBands.Length];
        foreach (var f in forms)
        {
            var band = Array.FindIndex(FrequencyBands, b => f.FrequencyRank >= b.From && f.FrequencyRank <= b.To);
            if (bandOf.TryAdd((f.WordId, f.ReadingIndex), band))
                bandSize[band]++;
        }

        var bandWordIds = forms.Select(f => f.WordId).Distinct().ToList();
        var activeList = activeUsers.ToList();

        var setStates = await userContext.UserWordSetStates.AsNoTracking()
                                         .Where(s => activeList.Contains(s.UserId))
                                         .Select(s => new { s.UserId, s.SetId, s.State })
                                         .ToListAsync(ct);
        var setIds = setStates.Select(s => s.SetId).Distinct().ToList();
        var nameSetIds = (await jitenContext.WordSets.AsNoTracking()
                                            .Where(s => NameSetSlugs.Contains(s.Slug))
                                            .Select(s => s.SetId)
                                            .ToListAsync(ct)).ToHashSet();
        var setMembers = await jitenContext.WordSetMembers.AsNoTracking()
                                           .Where(m => setIds.Contains(m.SetId) && bandWordIds.Contains(m.WordId))
                                           .Select(m => new { m.SetId, m.WordId, m.ReadingIndex })
                                           .ToListAsync(ct);
        var bandSetMembers = setMembers.Where(m => bandOf.ContainsKey((m.WordId, m.ReadingIndex)))
                                       .GroupBy(m => m.SetId)
                                       .ToDictionary(g => g.Key, g => g.Select(m => (m.WordId, (int)m.ReadingIndex)).ToList());
        var setMemberForms = bandSetMembers.Values.SelectMany(v => v).ToHashSet();

        var known = new Dictionary<string, int[]>();
        var ignored = new Dictionary<string, int[]>();
        int[] Counts(Dictionary<string, int[]> map, string user) =>
            map.TryGetValue(user, out var arr) ? arr : map[user] = new int[FrequencyBands.Length];

        var cardedSetForms = new HashSet<(string, int, int)>();
        var cards = userContext.FsrsCards.AsNoTracking()
                               .Where(c => activeList.Contains(c.UserId) && bandWordIds.Contains(c.WordId))
                               .Select(c => new { c.UserId, c.WordId, c.ReadingIndex, c.State })
                               .AsAsyncEnumerable();
        await foreach (var c in cards.WithCancellation(ct))
        {
            if (!bandOf.TryGetValue((c.WordId, c.ReadingIndex), out var band))
                continue;
            if (KnownCardStates.Contains(c.State))
                Counts(known, c.UserId)[band]++;
            else if (c.State == FsrsState.Blacklisted)
                Counts(ignored, c.UserId)[band]++;
            if (setMemberForms.Contains((c.WordId, c.ReadingIndex)))
                cardedSetForms.Add((c.UserId, c.WordId, c.ReadingIndex));
        }

        foreach (var user in setStates.GroupBy(s => s.UserId))
        {
            var sources = new Dictionary<(int, int), SetSources>();
            foreach (var s in user)
            {
                foreach (var form in bandSetMembers.GetValueOrDefault(s.SetId) ?? [])
                {
                    if (cardedSetForms.Contains((user.Key, form.WordId, form.Item2)))
                        continue;
                    var flags = sources.GetValueOrDefault(form);
                    flags |= s.State != WordSetStateType.Mastered ? SetSources.Blacklisted
                        : nameSetIds.Contains(s.SetId) ? SetSources.MasteredName : SetSources.MasteredVocabulary;
                    sources[form] = flags;
                }
            }

            // Mastered beats Blacklisted, as in CurrentUserService; a mastered name set is neither known nor ignored.
            foreach (var (form, flags) in sources)
            {
                if (flags.HasFlag(SetSources.MasteredVocabulary))
                    Counts(known, user.Key)[bandOf[form]]++;
                else if (flags == SetSources.Blacklisted)
                    Counts(ignored, user.Key)[bandOf[form]]++;
            }
        }

        var result = new List<SiteStatsFrequencyBandDto>();
        for (var b = 0; b < FrequencyBands.Length; b++)
        {
            if (bandSize[b] == 0)
                continue;
            var shares = new List<double>();
            foreach (var user in activeList)
            {
                var denominator = bandSize[b] - (ignored.TryGetValue(user, out var ig) ? ig[b] : 0);
                if (denominator <= 0)
                    continue;
                shares.Add(100.0 * (known.TryGetValue(user, out var kn) ? kn[b] : 0) / denominator);
            }

            if (shares.Count < MinLearnersPerSection)
                continue;
            result.Add(new SiteStatsFrequencyBandDto
            {
                From = FrequencyBands[b].From,
                To = FrequencyBands[b].To,
                P25 = Math.Round(Percentile(shares, 0.25), 1),
                Median = Math.Round(Percentile(shares, 0.5), 1),
                P75 = Math.Round(Percentile(shares, 0.75), 1),
                P90 = Math.Round(Percentile(shares, 0.9), 1)
            });
        }

        return result;
    }

    /// <summary>Reviews by weekday and hour in each reviewer's own study timezone; reviewers without one are left out.</summary>
    private async Task<SiteStatsHeatmapDto?> BuildHeatmap(DateTime utcNow, CancellationToken ct)
    {
        await using var userContext = await userFactory.CreateDbContextAsync(ct);

        var settings = await userContext.UserFsrsSettings.AsNoTracking().ToListAsync(ct);
        var zones = new Dictionary<string, TimeZoneInfo>();
        foreach (var s in settings)
        {
            if (FsrsSettingsHelper.ResolveTimeZone(FsrsSettingsHelper.GetStudySettings(s).Timezone) is { } zone)
                zones[s.UserId] = zone;
        }

        if (zones.Count < MinLearnersPerSection)
            return null;

        var since = utcNow.AddDays(-LogWindowDays);
        var zonedUsers = zones.Keys.ToList();
        var buckets = await (from l in userContext.FsrsReviewLogs
                             join c in userContext.FsrsCards on l.CardId equals c.CardId
                             where l.ReviewDateTime >= since && zonedUsers.Contains(c.UserId)
                             group l by new { c.UserId, l.ReviewDateTime.Year, l.ReviewDateTime.Month, l.ReviewDateTime.Day, l.ReviewDateTime.Hour }
                             into g
                             select new { g.Key.UserId, g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, Count = g.Count() })
                            .ToListAsync(ct);

        var grid = new long[7][];
        for (var d = 0; d < 7; d++)
            grid[d] = new long[24];
        var learners = new HashSet<string>();
        foreach (var b in buckets)
        {
            var utc = new DateTime(b.Year, b.Month, b.Day, b.Hour, 0, 0, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zones[b.UserId]);
            grid[((int)local.DayOfWeek + 6) % 7][local.Hour] += b.Count;
            learners.Add(b.UserId);
        }

        return learners.Count < MinLearnersPerSection ? null : new SiteStatsHeatmapDto { Reviews = grid, Learners = learners.Count };
    }

    private sealed record Reading(List<SiteStatsReadingDto> Rows, long TitlesFinished, long CharactersRead);

    private async Task<Reading> BuildReading(CancellationToken ct)
    {
        await using var userContext = await userFactory.CreateDbContextAsync(ct);

        var perType = await userContext.UserAccomplishments.AsNoTracking()
                                       .Where(a => a.MediaType != null)
                                       .GroupBy(a => a.MediaType)
                                       .Select(g => new
                                       {
                                           MediaType = g.Key,
                                           Learners = g.Count(),
                                           Characters = g.Sum(a => a.TotalCharacterCount + a.UnfinishedCharacterCount),
                                           Titles = g.Sum(a => (long)a.CompletedDeckCount),
                                           Units = g.Sum(a => (long)a.CompletedUnitCount)
                                       })
                                       .ToListAsync(ct);

        var rows = perType.Where(t => t.Learners >= MinLearnersPerReadingType)
                          .OrderByDescending(t => t.Characters)
                          .Select(t => new SiteStatsReadingDto
                          {
                              MediaType = t.MediaType,
                              Learners = t.Learners,
                              Characters = t.Characters,
                              TitlesFinished = t.Titles,
                              UnitsFinished = t.Units
                          })
                          .ToList();

        var folded = perType.Where(t => t.Learners < MinLearnersPerReadingType).ToList();
        if (folded.Count > 0)
        {
            var foldedTypes = folded.Select(t => t.MediaType).ToList();
            var foldedLearners = await userContext.UserAccomplishments.AsNoTracking()
                                                  .Where(a => foldedTypes.Contains(a.MediaType))
                                                  .Select(a => a.UserId)
                                                  .Distinct()
                                                  .CountAsync(ct);
            if (foldedLearners >= MinLearnersPerReadingType)
            {
                rows.Add(new SiteStatsReadingDto
                {
                    MediaType = null,
                    Learners = foldedLearners,
                    Characters = folded.Sum(t => t.Characters),
                    TitlesFinished = folded.Sum(t => t.Titles),
                    UnitsFinished = folded.Sum(t => t.Units)
                });
            }
        }

        return new Reading(rows, perType.Sum(t => t.Titles), perType.Sum(t => t.Characters));
    }

    private async Task<List<SiteStatsStudiedDeckDto>> BuildStudying(DateTime utcNow, CancellationToken ct)
    {
        await using var userContext = await userFactory.CreateDbContextAsync(ct);
        await using var jitenContext = await jitenFactory.CreateDbContextAsync(ct);

        var since = utcNow.AddDays(-LogWindowDays);
        var added = await userContext.UserStudyDecks.AsNoTracking()
                                     .Where(s => s.DeckId != null && s.CreatedAt >= since)
                                     .Select(s => new { s.UserId, DeckId = s.DeckId!.Value })
                                     .ToListAsync(ct);
        if (added.Count == 0)
            return [];

        var deckIds = added.Select(a => a.DeckId).Distinct().ToList();
        var parents = await jitenContext.Decks.AsNoTracking()
                                        .Where(d => deckIds.Contains(d.DeckId))
                                        .Select(d => new { d.DeckId, d.ParentDeckId })
                                        .ToDictionaryAsync(d => d.DeckId, d => d.ParentDeckId ?? d.DeckId, ct);

        var top = added.Where(a => parents.ContainsKey(a.DeckId))
                       .GroupBy(a => parents[a.DeckId])
                       .Select(g => (Root: g.Key, Learners: g.Select(a => a.UserId).Distinct().Count()))
                       .Where(r => r.Learners >= MinLearnersPerStudiedDeck)
                       .OrderByDescending(r => r.Learners)
                       .ThenBy(r => r.Root)
                       .Take(StudiedDeckCount)
                       .ToList();

        var rootIds = top.Select(r => r.Root).ToList();
        var decks = await jitenContext.Decks.AsNoTracking()
                                      .Where(d => rootIds.Contains(d.DeckId))
                                      .Select(d => new { d.DeckId, d.MediaType, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName })
                                      .ToDictionaryAsync(d => d.DeckId, ct);

        return top.Where(r => decks.ContainsKey(r.Root))
                  .Select(r =>
                  {
                      var d = decks[r.Root];
                      return new SiteStatsStudiedDeckDto
                      {
                          DeckId = d.DeckId,
                          MediaType = d.MediaType,
                          OriginalTitle = d.OriginalTitle,
                          RomajiTitle = d.RomajiTitle,
                          EnglishTitle = d.EnglishTitle,
                          CoverName = d.CoverName,
                          Learners = r.Learners
                      };
                  })
                  .ToList();
    }

    private async Task<List<SiteStatsForgottenWordDto>> BuildForgotten(DateTime utcNow, CancellationToken ct)
    {
        await using var userContext = await userFactory.CreateDbContextAsync(ct);
        await using var jitenContext = await jitenFactory.CreateDbContextAsync(ct);

        var since = utcNow.AddDays(-LogWindowDays);
        var words = await (from l in userContext.FsrsReviewLogs
                           join c in userContext.FsrsCards on l.CardId equals c.CardId
                           where l.ReviewDateTime >= since && l.State == FsrsState.Review
                           group new { c.UserId, l.Rating } by new { c.WordId, c.ReadingIndex }
                           into g
                           where g.Count() >= MinReviewsPerForgottenWord
                                 && g.Select(x => x.UserId).Distinct().Count() >= MinLearnersPerForgottenWord
                           select new
                           {
                               g.Key.WordId,
                               g.Key.ReadingIndex,
                               Reviews = g.Count(),
                               Agains = g.Count(x => x.Rating == FsrsRating.Again)
                           })
                          .ToListAsync(ct);

        var top = words.OrderByDescending(w => (double)w.Agains / w.Reviews)
                       .ThenBy(w => w.WordId)
                       .Take(ForgottenWordCount)
                       .ToList();
        if (top.Count == 0)
            return [];

        var wordIds = top.Select(w => w.WordId).Distinct().ToList();
        var forms = await jitenContext.WordForms.AsNoTracking()
                                      .Where(f => wordIds.Contains(f.WordId))
                                      .Select(f => new { f.WordId, f.ReadingIndex, f.Text, f.RubyText })
                                      .ToListAsync(ct);
        var definitions = await jitenContext.Definitions.AsNoTracking()
                                            .Where(d => wordIds.Contains(d.WordId))
                                            .Select(d => new { d.WordId, d.SenseIndex, d.EnglishMeanings, d.RestrictedToReadingIndices })
                                            .ToListAsync(ct);

        return top.Select(w =>
        {
            var form = forms.FirstOrDefault(f => f.WordId == w.WordId && f.ReadingIndex == w.ReadingIndex);
            var sense = definitions.Where(d => d.WordId == w.WordId
                                               && (d.RestrictedToReadingIndices is not { Count: > 0 }
                                                   || d.RestrictedToReadingIndices.Contains(w.ReadingIndex)))
                                   .OrderBy(d => d.SenseIndex)
                                   .FirstOrDefault();
            return new SiteStatsForgottenWordDto
            {
                WordId = w.WordId,
                ReadingIndex = w.ReadingIndex,
                Text = form?.Text ?? "",
                ReadingFurigana = form?.RubyText ?? "",
                Gloss = sense == null ? null : string.Join("; ", sense.EnglishMeanings.Take(2)),
                MissedPercent = Math.Round(100.0 * w.Agains / w.Reviews, 1)
            };
        }).Where(w => w.Text != "").ToList();
    }

    private async Task<SiteStatsLibraryDto> BuildLibrary(DateTime utcNow, CancellationToken ct)
    {
        await using var jitenContext = await jitenFactory.CreateDbContextAsync(ct);

        var titles = await jitenContext.Decks.AsNoTracking()
                                       .Where(d => d.ParentDeckId == null)
                                       .Select(d => new { d.CreationDate, d.CharacterCount })
                                       .ToListAsync(ct);

        var monthStart = new DateOnly(utcNow.Year, utcNow.Month, 1);
        long running = 0;
        var byMonth = titles.GroupBy(t => new DateOnly(t.CreationDate.UtcDateTime.Year, t.CreationDate.UtcDateTime.Month, 1))
                            .OrderBy(g => g.Key)
                            .Select(g => new SiteStatsMonthPointDto { Month = g.Key, Total = running += g.Count() })
                            .ToList();

        return new SiteStatsLibraryDto
        {
            Titles = titles.Count,
            CharactersParsed = titles.Sum(t => (long)t.CharacterCount),
            AddedThisMonth = titles.Count(t => DateOnly.FromDateTime(t.CreationDate.UtcDateTime) >= monthStart),
            RequestsFulfilled = await jitenContext.MediaRequests.CountAsync(r => r.Status == MediaRequestStatus.Completed, ct),
            DifficultyVotes = await jitenContext.DifficultyVotes.CountAsync(v => v.Source == DifficultyVoteSource.Manual, ct),
            ByMonth = byMonth
        };
    }

    /// <summary>Interpolated percentile, matching Postgres PERCENTILE_CONT.</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var position = (sorted.Length - 1) * p;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
