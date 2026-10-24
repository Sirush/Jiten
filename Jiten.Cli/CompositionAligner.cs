using System.Text;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Cli;

/// <summary>Re-points kana composition slots at the homograph a kanji sibling form pins (ほこり in ほこり除け: 誇り to 埃); idempotent, never rebuilds the table.</summary>
public static class CompositionAligner
{
    private sealed record Row(int WordId, short ReadingIndex, short Position, string Surface, bool Grammatical)
    {
        public int ComponentWordId { get; set; }
        public short ComponentReadingIndex { get; set; }
    }

    private sealed record Fix(Row Row, int NewWordId, short NewReadingIndex);

    private sealed class FormData
    {
        public readonly Dictionary<int, string[]> Pos = new();
        public readonly Dictionary<int, List<(short Ridx, string Text, string Hira)>> KanaForms = new();
        public readonly Dictionary<(int, short), string> Text = new();
        public readonly Dictionary<(int, short), string> Ruby = new();
        public readonly Dictionary<int, HashSet<string>> KanjiForms = new();
    }

    public static async Task Align(IDbContextFactory<JitenDbContext> contextFactory, bool dryRun)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        Console.WriteLine("Loading compositions and word forms for alignment...");
        var rows = await LoadRows(conn);
        var data = await LoadForms(conn, rows.Select(r => r.WordId).ToHashSet());

        var fixes = new List<Fix>();
        foreach (var parent in rows.GroupBy(r => r.WordId))
        {
            var forms = parent.GroupBy(r => r.ReadingIndex).Select(g => g.OrderBy(r => r.Position).ToList()).ToList();
            if (forms.Count >= 2)
                AlignToKanjiSiblings(data, forms, fixes);
        }

        Console.WriteLine($@"=== Composition Alignment ({(dryRun ? "DRY RUN" : "APPLIED")}) ===
Kana slots aligned to a kanji sibling: {fixes.Count:N0}
Parents touched:                      {fixes.Select(f => f.Row.WordId).Distinct().Count():N0}");

        if (dryRun)
        {
            var report = fixes.Select(f => $"- {data.Text.GetValueOrDefault((f.Row.WordId, f.Row.ReadingIndex))} ({f.Row.WordId}/{f.Row.ReadingIndex}) " +
                                           $"#{f.Row.Position} {f.Row.Surface}: {Describe(data, f.Row.ComponentWordId, f.Row.ComponentReadingIndex)} -> " +
                                           Describe(data, f.NewWordId, f.NewReadingIndex));
            await File.WriteAllTextAsync("composition-alignment-fixes.md",
                                         "# Composition alignment dry-run\n\n" + string.Join("\n", report), Encoding.UTF8);
            Console.WriteLine("Dry-run: wrote composition-alignment-fixes.md, no rows updated.");
            return;
        }

        if (fixes.Count == 0) return;

        await using var tx = await conn.BeginTransactionAsync();
        const int chunk = 1_000;
        for (int i = 0; i < fixes.Count; i += chunk)
        {
            var values = string.Join(",", fixes.Skip(i).Take(chunk).Select(f =>
                $"({f.Row.WordId},{f.Row.ReadingIndex},{f.Row.Position},{f.NewWordId},{f.NewReadingIndex})"));
            await using var cmd = new NpgsqlCommand($@"
UPDATE jmdict.""WordCompositions"" wc
SET ""ComponentWordId"" = v.cw, ""ComponentReadingIndex"" = v.cr::smallint
FROM (VALUES {values}) AS v(w, r, p, cw, cr)
WHERE wc.""WordId"" = v.w AND wc.""ReadingIndex"" = v.r::smallint AND wc.""Position"" = v.p::smallint", conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        Console.WriteLine($"Updated {fixes.Count:N0} composition rows.");
    }

    private static void AlignToKanjiSiblings(FormData data, List<List<Row>> forms, List<Fix> fixes)
    {
        var slots = forms.Select(f => (IReadOnlyList<CompositionSlot>)ToSlots(data, f)).ToList();
        for (int f = 0; f < forms.Count; f++)
        {
            var siblings = slots.Where((_, s) => s != f).ToList();
            for (int i = 0; i < forms[f].Count; i++)
            {
                var row = forms[f][i];
                var donor = CompositionAlignment.FindKanjiDonor(slots[f], i, siblings, (current, candidate) => CanTake(data, current, candidate));
                if (donor == null) continue;

                var ridx = KanaReadingIndex(data, donor.Value, row.Surface) ?? data.KanaForms[donor.Value][0].Ridx;
                if (row.ComponentWordId != donor.Value || row.ComponentReadingIndex != ridx)
                    fixes.Add(new Fix(row, donor.Value, ridx));
            }
        }
    }

    /// <summary>The donor's reading must spell the surface (a kana form, or an inflecting word's stem); a current word the donor's kanji also spells is kept.</summary>
    private static bool CanTake(FormData data, CompositionSlot current, CompositionSlot donor)
    {
        var pos = data.Pos.GetValueOrDefault(donor.WordId, []);
        if (!CompositionBackfill.IsContent(pos) || CompositionCleaner.MarksPhrase(pos) || !data.KanaForms.TryGetValue(donor.WordId, out var forms))
            return false;
        if (data.KanjiForms.TryGetValue(current.WordId, out var kanji) && kanji.Contains(donor.Surface)) return false;

        var hira = CompositionBackfill.Hira(current.Surface);
        if (donor.Reading != null && donor.Reading != hira) return false;
        if (KanaReadingIndex(data, donor.WordId, current.Surface) != null) return true;
        return donor.Reading != null && CompositionBackfill.IsVerbOrAdj(pos) && forms.Any(f => f.Hira.Length >= 2 && hira.StartsWith(f.Hira[..^1]));
    }

    /// <summary>The kana form that spells the surface: exact text first, then the same hiragana, then a rendaku or gemination variant.</summary>
    private static short? KanaReadingIndex(FormData data, int wid, string surface)
    {
        if (!data.KanaForms.TryGetValue(wid, out var forms)) return null;
        var hira = CompositionBackfill.Hira(surface);
        short? best = null;
        int bestRank = int.MaxValue;
        foreach (var (ridx, text, formHira) in forms)
        {
            int rank = text == surface ? 0
                : formHira == hira ? 1
                : CompositionBackfill.ReadingMatches(formHira, hira) ? 2
                : int.MaxValue;
            if (rank >= bestRank) continue;
            best = ridx;
            bestRank = rank;
        }
        return best;
    }

    /// <summary>Places each slot's reading by walking the parent form's ruby; a slot whose boundary splits a ruby group stays unplaced.</summary>
    private static List<CompositionSlot> ToSlots(FormData data, List<Row> form)
    {
        var readings = new string?[form.Count];
        var key = (form[0].WordId, form[0].ReadingIndex);
        var text = data.Text.GetValueOrDefault(key);
        var spans = text != null && data.Ruby.TryGetValue(key, out var ruby) ? CompositionBackfill.RubySpans(text, ruby) : null;

        if (spans != null && string.Concat(spans.Select(s => s.Surface)) == text && string.Concat(form.Select(r => r.Surface)) == text)
        {
            int spanIdx = 0;
            for (int i = 0; i < form.Count; i++)
            {
                var reading = new StringBuilder();
                int remaining = form[i].Surface.Length;
                while (remaining > 0 && spans[spanIdx].Surface.Length <= remaining)
                {
                    reading.Append(spans[spanIdx].Reading);
                    remaining -= spans[spanIdx].Surface.Length;
                    spanIdx++;
                }
                if (remaining != 0) break;
                readings[i] = reading.ToString();
            }
        }

        return form.Select((r, i) => new CompositionSlot(r.ComponentWordId, r.ComponentReadingIndex, r.Surface, r.Grammatical, readings[i])).ToList();
    }

    private static string Describe(FormData data, int wid, short ridx) =>
        $"{data.Text.GetValueOrDefault((wid, ridx), "?")} ({wid}/{ridx})";

    private static async Task<List<Row>> LoadRows(NpgsqlConnection conn)
    {
        var rows = new List<Row>();
        await using var cmd = new NpgsqlCommand(@"
SELECT ""WordId"", ""ReadingIndex"", ""Position"", ""ComponentWordId"", ""ComponentReadingIndex"", ""ComponentSurface"", ""IsGrammatical""
FROM jmdict.""WordCompositions""", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new Row(r.GetInt32(0), r.GetInt16(1), r.GetInt16(2), r.GetString(5), r.GetBoolean(6))
            {
                ComponentWordId = r.GetInt32(3), ComponentReadingIndex = r.GetInt16(4)
            });
        return rows;
    }

    private static async Task<FormData> LoadForms(NpgsqlConnection conn, HashSet<int> parents)
    {
        var data = new FormData();
        await using (var cmd = new NpgsqlCommand("SELECT \"WordId\",\"PartsOfSpeech\" FROM jmdict.\"Words\"", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                data.Pos[r.GetInt32(0)] = r.IsDBNull(1) ? [] : r.GetFieldValue<string[]>(1);

        await using (var cmd = new NpgsqlCommand(
                         "SELECT \"WordId\",\"ReadingIndex\",\"Text\",\"FormType\",\"RubyText\" FROM jmdict.\"WordForms\" ORDER BY \"WordId\",\"ReadingIndex\"", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                int wid = r.GetInt32(0);
                short ridx = r.GetInt16(1);
                string text = r.GetString(2);
                data.Text[(wid, ridx)] = text;
                if (parents.Contains(wid) && !r.IsDBNull(4)) data.Ruby[(wid, ridx)] = r.GetString(4);
                if (r.GetInt16(3) == 0)
                {
                    if (!data.KanjiForms.TryGetValue(wid, out var kanji)) data.KanjiForms[wid] = kanji = [];
                    kanji.Add(text);
                    continue;
                }

                if (!data.KanaForms.TryGetValue(wid, out var forms)) data.KanaForms[wid] = forms = [];
                forms.Add((ridx, text, CompositionBackfill.Hira(text)));
            }

        return data;
    }
}
