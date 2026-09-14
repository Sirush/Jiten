using System.Diagnostics;
using Jiten.Core;
using Jiten.Core.Data.User;
using Jiten.Core.Services.SmartDeck;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Jiten.Cli.Commands;

/// <summary>Times every stage of a Smart Deck rebuild and read at the plan's caps against the local database.</summary>
public class SmartDeckBenchCommands(CliContext context)
{
    private const string BenchDeckName = "__smartdeck_bench";

    public async Task Run(CliOptions options)
    {
        var connectionString = context.Configuration.GetConnectionString("JitenDatabase");
        var userOptions = new DbContextOptionsBuilder<UserDbContext>().UseNpgsql(connectionString).Options;
        await using var jiten = context.ContextFactory.CreateDbContext();
        await using var user = new UserDbContext(userOptions);

        var userId = options.SmartDeckBenchUser ?? await user.FsrsCards.AsNoTracking()
                                                             .GroupBy(c => c.UserId)
                                                             .OrderByDescending(g => g.Count())
                                                             .Select(g => g.Key)
                                                             .FirstOrDefaultAsync();
        if (userId == null)
        {
            Console.WriteLine("No user with FSRS cards found; pass --smart-deck-bench-user.");
            return;
        }

        Console.WriteLine($"User {userId}, cards {await user.FsrsCards.CountAsync(c => c.UserId == userId)}");
        Console.WriteLine($"Titles cap {SmartDeckConstants.MaxTitles}, boosted {SmartDeckConstants.BoostedTitles}, words cap {SmartDeckConstants.MaxWords}");

        for (var pass = 1; pass <= 2; pass++)
        {
            Console.WriteLine($"\n=== Pass {pass} ({(pass == 1 ? "cold" : "warm")}) ===");
            await RunPass(jiten, user, userId, options.SmartDeckBenchRandom);
        }
    }

    private static async Task RunPass(JitenDbContext jiten, UserDbContext user, string userId, bool random)
    {
        var sw = Stopwatch.StartNew();

        var withChildren = await jiten.Decks.AsNoTracking()
                                      .Where(d => d.ParentDeckId == null && d.UniqueWordCount > 0 && d.Children.Any())
                                      .OrderBy(d => random ? EF.Functions.Random() : -d.UniqueWordCount)
                                      .Select(d => d.DeckId)
                                      .Take(SmartDeckConstants.MaxTitles / 2)
                                      .ToListAsync();
        var withoutChildren = await jiten.Decks.AsNoTracking()
                                         .Where(d => d.ParentDeckId == null && d.UniqueWordCount > 0 && !d.Children.Any())
                                         .OrderBy(d => random ? EF.Functions.Random() : -d.UniqueWordCount)
                                         .Select(d => d.DeckId)
                                         .Take(SmartDeckConstants.MaxTitles - withChildren.Count)
                                         .ToListAsync();
        var parentIds = withChildren.Concat(withoutChildren).ToList();
        var boostedIds = withChildren.Take(SmartDeckConstants.BoostedTitles).ToHashSet();

        var children = await jiten.Decks.AsNoTracking()
                                  .Where(d => d.ParentDeckId != null && boostedIds.Contains(d.ParentDeckId.Value))
                                  .OrderBy(d => d.ParentDeckId).ThenBy(d => d.DeckOrder)
                                  .Select(d => new { d.DeckId, ParentDeckId = d.ParentDeckId!.Value, d.DeckOrder })
                                  .ToListAsync();
        Report("title set (2 deck queries + children)", sw, $"{parentIds.Count} parents, {children.Count} children of {boostedIds.Count} boosted");

        var windowIds = new HashSet<int>();
        var passedIds = new HashSet<int>();
        foreach (var g in children.GroupBy(c => c.ParentDeckId))
        {
            var ordered = g.ToList();
            var cursor = ordered.Count / 2;
            foreach (var c in ordered.Skip(cursor).Take(SmartDeckConstants.DefaultLookaheadUnits)) windowIds.Add(c.DeckId);
            foreach (var c in ordered.Take(cursor)) passedIds.Add(c.DeckId);
        }

        sw.Restart();
        var parentWords = await LoadWords(jiten, parentIds);
        Report("DeckWords for parents", sw, $"{parentWords.Values.Sum(w => w.Count)} rows");

        sw.Restart();
        var windowWords = await LoadWords(jiten, windowIds.ToList());
        Report("DeckWords for window units", sw, $"{windowIds.Count} units, {windowWords.Values.Sum(w => w.Count)} rows");

        sw.Restart();
        var allChildIds = children.Select(c => c.DeckId).ToList();
        var allChildWords = await LoadWords(jiten, allChildIds);
        Report("DeckWords for ALL units of boosted titles (per-unit variant)", sw, $"{allChildIds.Count} units, {allChildWords.Values.Sum(w => w.Count)} rows");

        sw.Restart();
        var excluded = new HashSet<long>();
        await foreach (var c in user.FsrsCards.AsNoTracking().Where(c => c.UserId == userId)
                                   .Select(c => new { c.WordId, c.ReadingIndex }).AsAsyncEnumerable())
            excluded.Add(SmartDeckScorer.EncodeKey(c.WordId, c.ReadingIndex));
        var setIds = await user.UserWordSetStates.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.SetId).ToListAsync();
        if (setIds.Count > 0)
        {
            var members = await jiten.WordSetMembers.AsNoTracking().Where(m => setIds.Contains(m.SetId))
                                     .Select(m => new { m.WordId, m.ReadingIndex }).ToListAsync();
            foreach (var m in members) excluded.Add(SmartDeckScorer.EncodeKey(m.WordId, (byte)m.ReadingIndex));
        }
        Report("user exclusion keys (cards + word sets, no derivation expansion)", sw, $"{excluded.Count} keys");

        sw.Restart();
        var ranks = await jiten.JmDictWordFrequencies.AsNoTracking()
                               .Select(f => new { f.WordId, f.FrequencyRank })
                               .ToDictionaryAsync(f => f.WordId, f => f.FrequencyRank);
        Report("global frequency ranks", sw, $"{ranks.Count} words");
        int Rank(long key) => ranks.TryGetValue((int)(key >> 8), out var r) ? r : int.MaxValue;

        var titleWeights = parentIds.Select((id, i) => (id, w: SmartDeckConstants.TitleWeight(false, false, i * 2, 14)))
                                    .ToDictionary(t => t.id, t => t.w);

        sw.Restart();
        var variantA = parentIds.Select(pid =>
        {
            var parts = new List<SmartDeckPart> { new(pid, SmartDeckConstants.WholeTitleWeight, parentWords.GetValueOrDefault(pid, [])) };
            if (boostedIds.Contains(pid))
                foreach (var c in children.Where(c => c.ParentDeckId == pid && windowIds.Contains(c.DeckId)))
                    parts.Add(new SmartDeckPart(c.DeckId, SmartDeckConstants.WindowWeight - SmartDeckConstants.WholeTitleWeight,
                                                windowWords.GetValueOrDefault(c.DeckId, [])));
            return new SmartDeckTitleInput(pid, titleWeights[pid], parts);
        }).ToList();
        var rankedA = SmartDeckScorer.Score(variantA, excluded.Contains, Rank, SmartDeckConstants.MaxWords);
        Report("score variant A (parent + window)", sw, $"{rankedA.Count} words kept");

        sw.Restart();
        var variantB = parentIds.Select(pid =>
        {
            List<SmartDeckPart> parts;
            if (boostedIds.Contains(pid))
                parts = children.Where(c => c.ParentDeckId == pid).Select(c => new SmartDeckPart(c.DeckId,
                    windowIds.Contains(c.DeckId) ? SmartDeckConstants.WindowWeight
                    : passedIds.Contains(c.DeckId) ? SmartDeckConstants.PassedUnitWeight
                    : SmartDeckConstants.FutureUnitWeight,
                    allChildWords.GetValueOrDefault(c.DeckId, []))).ToList();
            else
                parts = [new SmartDeckPart(pid, SmartDeckConstants.WholeTitleWeight, parentWords.GetValueOrDefault(pid, []))];
            return new SmartDeckTitleInput(pid, titleWeights[pid], parts);
        }).ToList();
        var rankedB = SmartDeckScorer.Score(variantB, excluded.Contains, Rank, SmartDeckConstants.MaxWords);
        Report("score variant B (per-unit for boosted)", sw, $"{rankedB.Count} words kept");

        var overlap = rankedA.Take(500).Select(w => w.Key).Intersect(rankedB.Take(500).Select(w => w.Key)).Count();
        Console.WriteLine($"  top-500 overlap A vs B: {overlap}/500");

        var deckId = await EnsureBenchDeck(user, userId);

        sw.Restart();
        await user.Database.ExecuteSqlRawAsync("DELETE FROM \"user\".\"UserStudyDeckWords\" WHERE \"UserStudyDeckId\" = {0}", deckId);
        await CopyRows(user, deckId, rankedA);
        Report("full write: DELETE + binary COPY", sw, $"{rankedA.Count} rows");

        sw.Restart();
        var perturbed = Perturb(rankedA);
        var changed = await DiffWrite(user, deckId, perturbed);
        Report("diff write via temp table (30% scores moved)", sw, $"{changed} rows touched");

        sw.Restart();
        var overviewRows = await user.UserStudyDeckWords.AsNoTracking().Where(w => w.UserStudyDeckId == deckId)
                                     .Select(w => new { w.UserStudyDeckId, w.WordId, w.ReadingIndex }).ToListAsync();
        Report("overview read (all keys, as study-decks endpoint does)", sw, $"{overviewRows.Count} rows");

        sw.Restart();
        var pageAll = await user.UserStudyDeckWords.AsNoTracking().Where(w => w.UserStudyDeckId == deckId)
                                .OrderBy(w => w.SortOrder).Select(w => new { w.WordId, w.ReadingIndex, w.Occurrences }).ToListAsync();
        Report("word browser read (all rows sorted, paged in memory as today)", sw, $"{pageAll.Count} rows");

        sw.Restart();
        var page = await user.UserStudyDeckWords.AsNoTracking().Where(w => w.UserStudyDeckId == deckId)
                             .OrderBy(w => w.SortOrder).Skip(50_000).Take(50).Select(w => new { w.WordId, w.ReadingIndex }).ToListAsync();
        Report("word browser read (SQL page 1000 of 50)", sw, $"{page.Count} rows");

        await user.Database.ExecuteSqlRawAsync("DELETE FROM \"user\".\"UserStudyDeckWords\" WHERE \"UserStudyDeckId\" = {0}", deckId);
        await user.Database.ExecuteSqlRawAsync("DELETE FROM \"user\".\"UserStudyDecks\" WHERE \"UserStudyDeckId\" = {0}", deckId);
    }

    private static async Task<Dictionary<int, List<SmartDeckWordOccurrence>>> LoadWords(JitenDbContext jiten, List<int> deckIds)
    {
        var result = deckIds.ToDictionary(id => id, _ => new List<SmartDeckWordOccurrence>());
        if (deckIds.Count == 0) return result;
        await foreach (var w in jiten.DeckWords.AsNoTracking().Where(w => deckIds.Contains(w.DeckId))
                                    .Select(w => new { w.DeckId, w.WordId, w.ReadingIndex, w.Occurrences }).AsAsyncEnumerable())
            result[w.DeckId].Add(new SmartDeckWordOccurrence(w.WordId, w.ReadingIndex, w.Occurrences));
        return result;
    }

    private static async Task<int> EnsureBenchDeck(UserDbContext user, string userId)
    {
        var existing = await user.UserStudyDecks.FirstOrDefaultAsync(d => d.UserId == userId && d.Name == BenchDeckName);
        if (existing != null) return existing.UserStudyDeckId;
        var deck = new UserStudyDeck { UserId = userId, DeckType = StudyDeckType.StaticWordList, Name = BenchDeckName, IsActive = false };
        user.UserStudyDecks.Add(deck);
        await user.SaveChangesAsync();
        return deck.UserStudyDeckId;
    }

    private static async Task CopyRows(UserDbContext user, int deckId, List<SmartDeckScoredWord> rows)
    {
        var conn = (NpgsqlConnection)user.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY \"user\".\"UserStudyDeckWords\" (\"UserStudyDeckId\", \"WordId\", \"ReadingIndex\", \"SortOrder\", \"Occurrences\") FROM STDIN (FORMAT BINARY)");
        for (var i = 0; i < rows.Count; i++)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(deckId, NpgsqlDbType.Integer);
            await writer.WriteAsync(rows[i].WordId, NpgsqlDbType.Integer);
            await writer.WriteAsync((short)rows[i].ReadingIndex, NpgsqlDbType.Smallint);
            await writer.WriteAsync(i + 1, NpgsqlDbType.Integer);
            await writer.WriteAsync((int)Math.Round(rows[i].Score * SmartDeckConstants.OccurrencesScale), NpgsqlDbType.Integer);
        }
        await writer.CompleteAsync();
    }

    private static List<SmartDeckScoredWord> Perturb(List<SmartDeckScoredWord> rows)
    {
        var rng = new Random(42);
        var copy = rows.Select(r => rng.NextDouble() < 0.3 ? r with { Score = r.Score * (0.8 + rng.NextDouble() * 0.4) } : r).ToList();
        copy.Sort((a, b) => b.Score.CompareTo(a.Score));
        return copy;
    }

    /// <summary>Loads the new ranking into a temp table, then one UPDATE for moved rows, one INSERT for new keys, one DELETE for dropped keys.</summary>
    private static async Task<int> DiffWrite(UserDbContext user, int deckId, List<SmartDeckScoredWord> rows)
    {
        var conn = (NpgsqlConnection)user.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var cmd = new NpgsqlCommand("CREATE TEMP TABLE smart_new (\"WordId\" int, \"ReadingIndex\" smallint, \"SortOrder\" int, \"Occurrences\" int) ON COMMIT DROP", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        await using (var writer = await conn.BeginBinaryImportAsync("COPY smart_new FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(rows[i].WordId, NpgsqlDbType.Integer);
                await writer.WriteAsync((short)rows[i].ReadingIndex, NpgsqlDbType.Smallint);
                await writer.WriteAsync(i + 1, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)Math.Round(rows[i].Score * SmartDeckConstants.OccurrencesScale), NpgsqlDbType.Integer);
            }
            await writer.CompleteAsync();
        }

        var touched = 0;
        string[] statements =
        [
            "UPDATE \"user\".\"UserStudyDeckWords\" w SET \"SortOrder\" = n.\"SortOrder\", \"Occurrences\" = n.\"Occurrences\" FROM smart_new n WHERE w.\"UserStudyDeckId\" = @d AND w.\"WordId\" = n.\"WordId\" AND w.\"ReadingIndex\" = n.\"ReadingIndex\" AND (w.\"SortOrder\" <> n.\"SortOrder\" OR w.\"Occurrences\" <> n.\"Occurrences\")",
            "INSERT INTO \"user\".\"UserStudyDeckWords\" (\"UserStudyDeckId\", \"WordId\", \"ReadingIndex\", \"SortOrder\", \"Occurrences\") SELECT @d, n.\"WordId\", n.\"ReadingIndex\", n.\"SortOrder\", n.\"Occurrences\" FROM smart_new n WHERE NOT EXISTS (SELECT 1 FROM \"user\".\"UserStudyDeckWords\" w WHERE w.\"UserStudyDeckId\" = @d AND w.\"WordId\" = n.\"WordId\" AND w.\"ReadingIndex\" = n.\"ReadingIndex\")",
            "DELETE FROM \"user\".\"UserStudyDeckWords\" w WHERE w.\"UserStudyDeckId\" = @d AND NOT EXISTS (SELECT 1 FROM smart_new n WHERE n.\"WordId\" = w.\"WordId\" AND n.\"ReadingIndex\" = w.\"ReadingIndex\")",
        ];
        foreach (var statement in statements)
        {
            await using var cmd = new NpgsqlCommand(statement, conn, tx);
            cmd.Parameters.AddWithValue("d", deckId);
            touched += await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return touched;
    }

    private static void Report(string label, Stopwatch sw, string detail)
        => Console.WriteLine($"  {sw.ElapsedMilliseconds,6} ms  {label}  [{detail}]");
}
