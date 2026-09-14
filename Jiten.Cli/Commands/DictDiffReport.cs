using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using static Jiten.Cli.Commands.DictDiffCommands;

namespace Jiten.Cli.Commands;

/// <summary>Turns two dict-diff snapshots into a single self-contained HTML report.</summary>
public static class DictDiffReport
{
    private const string VocabUrl = "https://jiten.moe/vocabulary/";

    private sealed class Agg
    {
        public int Occ;
        public readonly HashSet<int> Decks = [];
        public readonly Dictionary<string, int> Surfaces = new();
        public string? Context;
    }

    private sealed class Column(string key, string label, bool numeric = false, bool html = false)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public bool Numeric { get; } = numeric;
        public bool Html { get; } = html;
    }

    private sealed class Section(string id, string title, string note, List<Column> columns)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string Note { get; } = note;
        public List<Column> Columns { get; } = columns;
        public List<Dictionary<string, object?>> Rows { get; } = [];
    }

    public static string Build(Snapshot before, Snapshot after, string beforePath, string afterPath)
    {
        var (keysB, surfB) = Aggregate(before);
        var (keysA, surfA) = Aggregate(after);

        string Word(int w, byte r) => WordHtml(after, before, w, r);
        string WordBefore(int w, byte r) => WordHtml(before, after, w, r);

        var sections = new List<Section>();

        // ---- surface remapped
        var remap = new Section("remap", "Surface remapped",
            "The same surface string resolved to a different (word, reading) after the sync. Sorted by how many occurrences moved. This is the main table to read.",
            [
                new Column("surface", "Surface"),
                new Column("before", "Before", html: true),
                new Column("after", "After", html: true),
                new Column("moved", "Moved occ.", numeric: true),
                new Column("total", "Total occ.", numeric: true),
                new Column("decks", "Decks", numeric: true),
                new Column("kind", "Kind", html: true),
                new Column("context", "Context"),
            ]);
        foreach (var surface in surfB.Keys.Union(surfA.Keys))
        {
            var b = surfB.GetValueOrDefault(surface) ?? new Dictionary<(int, byte), Agg>();
            var a = surfA.GetValueOrDefault(surface) ?? new Dictionary<(int, byte), Agg>();
            if (b.Keys.ToHashSet().SetEquals(a.Keys)) continue;
            var allKeys = b.Keys.Union(a.Keys).ToList();
            var moved = (allKeys.Sum(k => Math.Abs((b.GetValueOrDefault(k)?.Occ ?? 0) - (a.GetValueOrDefault(k)?.Occ ?? 0))) + 1) / 2;
            if (moved == 0) continue;

            var kinds = new List<string>();
            var bIds = b.Keys.Select(k => k.Item1).ToHashSet();
            var aIds = a.Keys.Select(k => k.Item1).ToHashSet();
            if (a.Keys.Any(k => !before.Entries.ContainsKey(k.Item1))) kinds.Add(Badge("new entry", "new"));
            if (b.Keys.Any(k => !after.Entries.ContainsKey(k.Item1))) kinds.Add(Badge("deleted entry", "del"));
            if (bIds.SetEquals(aIds)) kinds.Add(Badge("reading shift", "ri"));
            else if (b.Count > 0 && a.Count > 0) kinds.Add(Badge("different word", "word"));
            else if (b.Count == 0) kinds.Add(Badge("newly resolved", "new"));
            else kinds.Add(Badge("no longer resolved", "del"));

            var decks = b.Values.SelectMany(x => x.Decks).Union(a.Values.SelectMany(x => x.Decks)).Count();
            remap.Rows.Add(new Dictionary<string, object?>
            {
                ["surface"] = surface,
                ["before"] = KeyList(b, WordBefore),
                ["after"] = KeyList(a, Word),
                ["moved"] = moved,
                ["total"] = Math.Max(b.Values.Sum(x => x.Occ), a.Values.Sum(x => x.Occ)),
                ["decks"] = decks,
                ["kind"] = string.Join(" ", kinds),
                ["context"] = a.Values.Select(x => x.Context).FirstOrDefault(c => c != null)
                              ?? b.Values.Select(x => x.Context).FirstOrDefault(c => c != null) ?? "",
            });
        }
        remap.Rows.Sort((x, y) => ((int)y["moved"]!).CompareTo((int)x["moved"]!));
        sections.Add(remap);

        // ---- words gone
        var gone = new Section("gone", "Words gone",
            "(word, reading) pairs produced before the sync and never after, across the whole corpus.",
            [
                new Column("word", "Word", html: true),
                new Column("occ", "Occ. before", numeric: true),
                new Column("decks", "Decks", numeric: true),
                new Column("surfaces", "Surfaces"),
                new Column("status", "Entry status", html: true),
                new Column("context", "Context"),
            ]);
        foreach (var (key, agg) in keysB.Where(kv => !keysA.ContainsKey(kv.Key)))
        {
            gone.Rows.Add(new Dictionary<string, object?>
            {
                ["word"] = WordBefore(key.Item1, key.Item2),
                ["occ"] = agg.Occ,
                ["decks"] = agg.Decks.Count,
                ["surfaces"] = SurfaceList(agg),
                ["status"] = EntryStatus(before, after, key.Item1, key.Item2),
                ["context"] = agg.Context ?? "",
            });
        }
        gone.Rows.Sort((x, y) => ((int)y["occ"]!).CompareTo((int)x["occ"]!));
        sections.Add(gone);

        // ---- words new
        var fresh = new Section("new", "Words new",
            "(word, reading) pairs produced after the sync and never before. Unranked or very high rank with many occurrences is the usual smell.",
            [
                new Column("word", "Word", html: true),
                new Column("occ", "Occ. after", numeric: true),
                new Column("rank", "Rank", numeric: true),
                new Column("decks", "Decks", numeric: true),
                new Column("surfaces", "Surfaces"),
                new Column("status", "Entry status", html: true),
                new Column("context", "Context"),
            ]);
        foreach (var (key, agg) in keysA.Where(kv => !keysB.ContainsKey(kv.Key)))
        {
            fresh.Rows.Add(new Dictionary<string, object?>
            {
                ["word"] = Word(key.Item1, key.Item2),
                ["occ"] = agg.Occ,
                ["rank"] = Rank(after, key.Item1, key.Item2),
                ["decks"] = agg.Decks.Count,
                ["surfaces"] = SurfaceList(agg),
                ["status"] = EntryStatus(before, after, key.Item1, key.Item2),
                ["context"] = agg.Context ?? "",
            });
        }
        fresh.Rows.Sort((x, y) => ((int)y["occ"]!).CompareTo((int)x["occ"]!));
        sections.Add(fresh);

        // ---- count changed
        var changed = new Section("count", "Occurrence count changed",
            "Pairs present on both sides with a different total. Mostly the shadow of the tables above; large deltas that do not appear there point at segmentation changes.",
            [
                new Column("word", "Word", html: true),
                new Column("before", "Before", numeric: true),
                new Column("after", "After", numeric: true),
                new Column("delta", "Delta", numeric: true),
                new Column("decks", "Decks", numeric: true),
                new Column("surfaces", "Surfaces after"),
            ]);
        foreach (var (key, aggB) in keysB)
        {
            if (!keysA.TryGetValue(key, out var aggA) || aggA.Occ == aggB.Occ) continue;
            changed.Rows.Add(new Dictionary<string, object?>
            {
                ["word"] = Word(key.Item1, key.Item2),
                ["before"] = aggB.Occ,
                ["after"] = aggA.Occ,
                ["delta"] = aggA.Occ - aggB.Occ,
                ["decks"] = aggA.Decks.Union(aggB.Decks).Count(),
                ["surfaces"] = SurfaceList(aggA),
            });
        }
        changed.Rows.Sort((x, y) => Math.Abs((int)y["delta"]!).CompareTo(Math.Abs((int)x["delta"]!)));
        sections.Add(changed);

        // ---- dictionary integrity
        var usedBeforeByWord = keysB.GroupBy(kv => kv.Key.Item1).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.Occ));
        var usedAfterByWord = keysA.GroupBy(kv => kv.Key.Item1).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.Occ));

        var deleted = new Section("deleted", "Dictionary: entries deleted",
            "WordIds present before the sync and absent after. Any DeckWord or FSRS card pointing at them is now dangling.",
            [
                new Column("word", "Word", html: true),
                new Column("pos", "POS"),
                new Column("used", "Occ. in corpus before", numeric: true),
            ]);
        foreach (var (id, entry) in before.Entries.Where(kv => !after.Entries.ContainsKey(kv.Key)))
        {
            deleted.Rows.Add(new Dictionary<string, object?>
            {
                ["word"] = WordBefore(id, 0),
                ["pos"] = string.Join(", ", entry.Pos),
                ["used"] = usedBeforeByWord.GetValueOrDefault(id),
            });
        }
        deleted.Rows.Sort((x, y) => ((int)y["used"]!).CompareTo((int)x["used"]!));
        sections.Add(deleted);

        var shifts = new Section("shift", "Dictionary: reading index changed",
            "Same WordId, but the form stored at a ReadingIndex is different or missing. Existing DeckWords and cards keyed on (WordId, ReadingIndex) now name a different reading.",
            [
                new Column("id", "WordId", numeric: true),
                new Column("ri", "Index", numeric: true),
                new Column("before", "Form before"),
                new Column("after", "Form after"),
                new Column("change", "Change", html: true),
                new Column("used", "Occ. in corpus", numeric: true),
            ]);
        foreach (var (id, entryB) in before.Entries)
        {
            if (!after.Entries.TryGetValue(id, out var entryA)) continue;
            var formsA = entryA.Forms.ToDictionary(f => f.I);
            foreach (var fb in entryB.Forms)
            {
                formsA.TryGetValue(fb.I, out var fa);
                string change;
                if (fa == null) change = Badge("removed", "del");
                else if (fa.T != fb.T) change = Badge("text changed", "ri");
                else if (fb.A && !fa.A) change = Badge("deactivated", "warn");
                else continue;

                var used = keysB.GetValueOrDefault((id, (byte)fb.I))?.Occ ?? 0;
                shifts.Rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["ri"] = fb.I,
                    ["before"] = FormText(fb),
                    ["after"] = fa == null ? "" : FormText(fa),
                    ["change"] = change,
                    ["used"] = used,
                });
            }
        }
        shifts.Rows.Sort((x, y) => ((int)y["used"]!).CompareTo((int)x["used"]!));
        sections.Add(shifts);

        var posChanged = new Section("pos", "Dictionary: part of speech changed",
            "Entry-level POS list differs. POS drives lookup compatibility, so a changed list can flip which entry wins.",
            [
                new Column("word", "Word", html: true),
                new Column("before", "POS before"),
                new Column("after", "POS after"),
                new Column("used", "Occ. in corpus", numeric: true),
            ]);
        foreach (var (id, entryB) in before.Entries)
        {
            if (!after.Entries.TryGetValue(id, out var entryA)) continue;
            if (entryB.Pos.SequenceEqual(entryA.Pos)) continue;
            posChanged.Rows.Add(new Dictionary<string, object?>
            {
                ["word"] = Word(id, 0),
                ["before"] = string.Join(", ", entryB.Pos),
                ["after"] = string.Join(", ", entryA.Pos),
                ["used"] = Math.Max(usedBeforeByWord.GetValueOrDefault(id), usedAfterByWord.GetValueOrDefault(id)),
            });
        }
        posChanged.Rows.Sort((x, y) => ((int)y["used"]!).CompareTo((int)x["used"]!));
        sections.Add(posChanged);

        var added = new Section("added", "Dictionary: new entries seen in the corpus",
            "Entries that did not exist before the sync and were picked at least once after it.",
            [
                new Column("word", "Word", html: true),
                new Column("pos", "POS"),
                new Column("used", "Occ. after", numeric: true),
                new Column("surfaces", "Surfaces"),
            ]);
        var newEntryCount = after.Entries.Keys.Count(id => !before.Entries.ContainsKey(id));
        foreach (var (id, entry) in after.Entries.Where(kv => !before.Entries.ContainsKey(kv.Key)))
        {
            var uses = keysA.Where(kv => kv.Key.Item1 == id).ToList();
            if (uses.Count == 0) continue;
            var merged = new Agg();
            foreach (var (_, agg) in uses)
                foreach (var (s, c) in agg.Surfaces) merged.Surfaces[s] = merged.Surfaces.GetValueOrDefault(s) + c;
            added.Rows.Add(new Dictionary<string, object?>
            {
                ["word"] = Word(id, uses.OrderByDescending(kv => kv.Value.Occ).First().Key.Item2),
                ["pos"] = string.Join(", ", entry.Pos),
                ["used"] = uses.Sum(kv => kv.Value.Occ),
                ["surfaces"] = SurfaceList(merged),
            });
        }
        added.Rows.Sort((x, y) => ((int)y["used"]!).CompareTo((int)x["used"]!));
        sections.Add(added);

        var summary = new List<(string label, string value)>
        {
            ("Texts parsed", $"{before.Decks.Count} before / {after.Decks.Count} after"),
            ("Total words", $"{before.Decks.Sum(d => d.WordCount):N0} / {after.Decks.Sum(d => d.WordCount):N0}"),
            ("Unique (word, reading)", $"{keysB.Count:N0} / {keysA.Count:N0}"),
            ("Dictionary entries", $"{before.Entries.Count:N0} / {after.Entries.Count:N0} (+{newEntryCount:N0} new, -{deleted.Rows.Count:N0} deleted)"),
            ("Surfaces remapped", $"{remap.Rows.Count:N0} ({remap.Rows.Sum(r => (int)r["moved"]!):N0} occ.)"),
            ("Words gone / new", $"{gone.Rows.Count:N0} / {fresh.Rows.Count:N0}"),
            ("Reading index changes", $"{shifts.Rows.Count:N0} ({shifts.Rows.Count(r => (int)r["used"]! > 0):N0} used in corpus)"),
            ("POS changes", $"{posChanged.Rows.Count:N0}"),
        };

        var deckTable = before.Decks.Select(d =>
        {
            var a = after.Decks.FirstOrDefault(x => x.DeckId == d.DeckId);
            return (d.Title, d.MediaType, d.WordCount, a?.WordCount ?? 0, d.UniqueWords, a?.UniqueWords ?? 0);
        }).ToList();

        return Render(sections, summary, deckTable, before.TakenAt, after.TakenAt, beforePath, afterPath);
    }

    private static (Dictionary<(int, byte), Agg> keys, Dictionary<string, Dictionary<(int, byte), Agg>> surfaces) Aggregate(Snapshot snapshot)
    {
        var keys = new Dictionary<(int, byte), Agg>();
        var surfaces = new Dictionary<string, Dictionary<(int, byte), Agg>>();
        foreach (var deck in snapshot.Decks)
        {
            foreach (var row in deck.Rows)
            {
                var key = (row.W, row.R);
                if (!keys.TryGetValue(key, out var agg)) keys[key] = agg = new Agg();
                agg.Occ += row.O;
                agg.Decks.Add(deck.DeckId);
                agg.Surfaces[row.S] = agg.Surfaces.GetValueOrDefault(row.S) + row.O;
                agg.Context ??= deck.Context.GetValueOrDefault($"{row.W}:{row.R}");

                if (!surfaces.TryGetValue(row.S, out var bySurface)) surfaces[row.S] = bySurface = new Dictionary<(int, byte), Agg>();
                if (!bySurface.TryGetValue(key, out var sagg)) bySurface[key] = sagg = new Agg();
                sagg.Occ += row.O;
                sagg.Decks.Add(deck.DeckId);
                sagg.Context ??= deck.Context.GetValueOrDefault($"{row.W}:{row.R}");
            }
        }
        return (keys, surfaces);
    }

    private static string KeyList(Dictionary<(int, byte), Agg> byKey, Func<int, byte, string> word)
    {
        if (byKey.Count == 0) return "<span class=\"muted\">not resolved</span>";
        var sb = new StringBuilder();
        foreach (var (key, agg) in byKey.OrderByDescending(kv => kv.Value.Occ))
            sb.Append("<div class=\"key\">").Append(word(key.Item1, key.Item2)).Append(" <span class=\"cnt\">×").Append(agg.Occ).Append("</span></div>");
        return sb.ToString();
    }

    private static string SurfaceList(Agg agg) =>
        string.Join(", ", agg.Surfaces.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} ×{kv.Value}"))
        + (agg.Surfaces.Count > 5 ? $" (+{agg.Surfaces.Count - 5})" : "");

    private static string EntryStatus(Snapshot before, Snapshot after, int w, byte r)
    {
        var inB = before.Entries.TryGetValue(w, out var eb);
        var inA = after.Entries.TryGetValue(w, out var ea);
        if (!inB && inA) return Badge("new entry", "new");
        if (inB && !inA) return Badge("deleted entry", "del");
        if (!inB) return Badge("unknown id", "warn");
        var fb = eb!.Forms.FirstOrDefault(f => f.I == r);
        var fa = ea!.Forms.FirstOrDefault(f => f.I == r);
        if (fb == null && fa != null) return Badge("new reading", "new");
        if (fb != null && fa == null) return Badge("reading removed", "del");
        if (fb != null && fa != null && fb.T != fa.T) return Badge("reading text changed", "ri");
        if (fa is { A: false }) return Badge("inactive form", "warn");
        return Badge("existing entry", "ok");
    }

    private static object Rank(Snapshot snapshot, int w, byte r)
    {
        if (snapshot.FormRanks.TryGetValue($"{w}:{r}", out var fr) && fr > 0) return fr;
        if (snapshot.WordRanks.TryGetValue(w, out var wr) && wr > 0) return wr;
        return 999999;
    }

    private static string FormText(SnapForm f) => f.Ru == null ? f.T : $"{f.T}【{f.Ru}】";

    private static string Badge(string text, string cls) => $"<span class=\"badge {cls}\">{WebUtility.HtmlEncode(text)}</span>";

    private static string WordHtml(Snapshot primary, Snapshot fallback, int w, byte r)
    {
        var entry = primary.Entries.GetValueOrDefault(w) ?? fallback.Entries.GetValueOrDefault(w);
        var gloss = primary.Glosses.GetValueOrDefault(w) ?? fallback.Glosses.GetValueOrDefault(w);
        var sb = new StringBuilder();
        sb.Append("<span class=\"word\">");
        if (entry == null)
        {
            sb.Append("<a href=\"").Append(VocabUrl).Append(w).Append("\" target=\"_blank\">#").Append(w).Append('/').Append(r).Append("</a> <span class=\"muted\">(not in dictionary)</span>");
            return sb.Append("</span>").ToString();
        }

        var form = entry.Forms.FirstOrDefault(f => f.I == r);
        var head = form != null ? FormText(form) : entry.Forms.Count > 0 ? FormText(entry.Forms[0]) + " (reading " + r + " missing)" : "?";
        sb.Append("<a href=\"").Append(VocabUrl).Append(w).Append("\" target=\"_blank\">").Append(WebUtility.HtmlEncode(head)).Append("</a>");
        sb.Append(" <span class=\"id\">#").Append(w).Append('/').Append(r).Append("</span>");
        var pos = gloss?.Senses.FirstOrDefault()?.Pos is { Count: > 0 } sp ? sp : entry.Pos;
        if (pos.Count > 0) sb.Append(" <span class=\"pos\">").Append(WebUtility.HtmlEncode(string.Join(", ", pos))).Append("</span>");
        if (gloss != null)
        {
            var senses = gloss.Senses.Take(2).Select(s => string.Join("; ", s.Glosses) + (s.Misc.Count > 0 ? $" [{string.Join(",", s.Misc)}]" : ""));
            sb.Append("<div class=\"gloss\">").Append(WebUtility.HtmlEncode(string.Join(" | ", senses))).Append("</div>");
        }
        return sb.Append("</span>").ToString();
    }

    private static string Render(List<Section> sections, List<(string label, string value)> summary,
                                 List<(string title, string media, int wordsB, int wordsA, int uniqB, int uniqA)> decks,
                                 DateTime takenBefore, DateTime takenAfter, string beforePath, string afterPath)
    {
        var data = sections.Select(s => new
        {
            id = s.Id, title = s.Title, note = s.Note,
            columns = s.Columns.Select(c => new { key = c.Key, label = c.Label, numeric = c.Numeric, html = c.Html }),
            rows = s.Rows,
        });
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });

        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Dictionary diff report</title>
<style>
  :root { --bg:#fbfbfa; --fg:#1d1d1b; --muted:#6b6b66; --line:#e2e2de; --head:#f1f1ee; --accent:#0b5fa5; --new:#1f7a3a; --del:#b3261e; --ri:#8a5a00; --warn:#7a4b00; --ok:#4a4a46; }
  body { margin:0; padding:20px 24px 60px; font:14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; background:var(--bg); color:var(--fg); }
  h1 { font-size:22px; margin:0 0 4px; }
  h2 { font-size:17px; margin:36px 0 4px; }
  .sub, .note, .muted { color:var(--muted); }
  .note { margin:0 0 10px; max-width:900px; }
  .cards { display:flex; flex-wrap:wrap; gap:10px; margin:16px 0 8px; }
  .card { border:1px solid var(--line); background:#fff; padding:8px 12px; min-width:150px; }
  .card .l { font-size:12px; color:var(--muted); }
  .card .v { font-size:15px; font-weight:600; }
  nav { margin:14px 0 0; display:flex; flex-wrap:wrap; gap:6px 14px; }
  nav a { color:var(--accent); text-decoration:none; }
  nav a .n { color:var(--muted); }
  .toolbar { display:flex; gap:10px; align-items:center; margin:6px 0 8px; }
  .toolbar input { padding:5px 8px; border:1px solid var(--line); min-width:260px; font:inherit; }
  .toolbar .count { color:var(--muted); font-size:12px; }
  .wrap { overflow-x:auto; }
  table { border-collapse:collapse; width:100%; background:#fff; border:1px solid var(--line); }
  th, td { text-align:left; vertical-align:top; padding:6px 8px; border-bottom:1px solid var(--line); }
  th { background:var(--head); cursor:pointer; user-select:none; white-space:nowrap; position:sticky; top:0; }
  th.sorted::after { content:" ▾"; color:var(--muted); } th.sorted.asc::after { content:" ▴"; }
  td.num { text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap; }
  td.context { max-width:420px; color:#333; font-size:13px; }
  td.surface { font-size:16px; white-space:nowrap; }
  .word a { color:var(--accent); text-decoration:none; font-size:15px; }
  .word .id { color:var(--muted); font-size:11px; }
  .word .pos { color:var(--muted); font-size:12px; }
  .word .gloss { color:#444; font-size:12px; max-width:360px; }
  .key { margin-bottom:4px; } .key .cnt { color:var(--muted); font-size:12px; }
  .badge { display:inline-block; font-size:11px; padding:1px 6px; border:1px solid currentColor; margin-right:4px; white-space:nowrap; }
  .badge.new { color:var(--new); } .badge.del { color:var(--del); } .badge.ri { color:var(--ri); } .badge.warn { color:var(--warn); } .badge.word { color:var(--accent); } .badge.ok { color:var(--ok); }
  .empty { padding:14px; color:var(--muted); border:1px dashed var(--line); background:#fff; }
  .more { margin:8px 0; }
  .more button { font:inherit; padding:4px 10px; border:1px solid var(--line); background:#fff; cursor:pointer; }
</style>
</head>
<body>
""");
        sb.Append("<h1>Dictionary diff report</h1>");
        sb.Append($"<div class=\"sub\">before: {WebUtility.HtmlEncode(beforePath)} ({takenBefore:yyyy-MM-dd HH:mm} UTC) · after: {WebUtility.HtmlEncode(afterPath)} ({takenAfter:yyyy-MM-dd HH:mm} UTC)</div>");
        sb.Append("<div class=\"cards\">");
        foreach (var (label, value) in summary)
            sb.Append($"<div class=\"card\"><div class=\"l\">{WebUtility.HtmlEncode(label)}</div><div class=\"v\">{WebUtility.HtmlEncode(value)}</div></div>");
        sb.Append("</div>");

        sb.Append("<nav>");
        foreach (var s in sections)
            sb.Append($"<a href=\"#{s.Id}\">{WebUtility.HtmlEncode(s.Title)} <span class=\"n\">({s.Rows.Count:N0})</span></a>");
        sb.Append("</nav>");

        sb.Append("<h2>Corpus</h2><div class=\"wrap\"><table><thead><tr><th>Text</th><th>Media</th><th>Words before</th><th>Words after</th><th>Unique before</th><th>Unique after</th></tr></thead><tbody>");
        foreach (var d in decks)
            sb.Append($"<tr><td>{WebUtility.HtmlEncode(d.title)}</td><td>{d.media}</td><td class=\"num\">{d.wordsB:N0}</td><td class=\"num\">{d.wordsA:N0}</td><td class=\"num\">{d.uniqB:N0}</td><td class=\"num\">{d.uniqA:N0}</td></tr>");
        sb.Append("</tbody></table></div>");

        sb.Append("<div id=\"sections\"></div>");
        sb.Append("<script type=\"application/json\" id=\"data\">").Append(json).Append("</script>");
        sb.Append("""
<script>
(function () {
  const PAGE = 300;
  const sections = JSON.parse(document.getElementById('data').textContent);
  const root = document.getElementById('sections');
  const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
  for (const s of sections) render(s);

  function render(s) {
    const h2 = document.createElement('h2'); h2.id = s.id; h2.textContent = s.title + ' (' + s.rows.length.toLocaleString() + ')';
    const note = document.createElement('p'); note.className = 'note'; note.textContent = s.note;
    root.append(h2, note);
    if (s.rows.length === 0) { const e = document.createElement('div'); e.className = 'empty'; e.textContent = 'Nothing here.'; root.append(e); return; }

    const bar = document.createElement('div'); bar.className = 'toolbar';
    const input = document.createElement('input'); input.placeholder = 'filter rows (plain text, any column)';
    const count = document.createElement('span'); count.className = 'count';
    bar.append(input, count); root.append(bar);
    const wrap = document.createElement('div'); wrap.className = 'wrap';
    const table = document.createElement('table');
    const thead = document.createElement('thead'); const tr = document.createElement('tr');
    for (const c of s.columns) { const th = document.createElement('th'); th.textContent = c.label; th.onclick = () => sort(c); tr.append(th); }
    thead.append(tr); table.append(thead);
    const tbody = document.createElement('tbody'); table.append(tbody); wrap.append(table); root.append(wrap);
    const more = document.createElement('div'); more.className = 'more'; const btn = document.createElement('button'); btn.textContent = 'Show more'; more.append(btn); root.append(more);

    const texts = s.rows.map(r => s.columns.map(c => c.html ? String(r[c.key] ?? '').replace(/<[^>]+>/g, ' ') : String(r[c.key] ?? '')).join(' ').toLowerCase());
    let order = s.rows.map((_, i) => i), sortKey = null, asc = false, shown = PAGE, filtered = order;

    function apply() {
      const q = input.value.trim().toLowerCase();
      filtered = q ? order.filter(i => texts[i].includes(q)) : order;
      shown = PAGE; draw();
    }
    function draw() {
      tbody.innerHTML = '';
      const frag = document.createDocumentFragment();
      for (const i of filtered.slice(0, shown)) {
        const r = s.rows[i]; const tr = document.createElement('tr');
        for (const c of s.columns) {
          const td = document.createElement('td');
          if (c.numeric) { td.className = 'num'; td.textContent = typeof r[c.key] === 'number' ? (r[c.key] === 999999 ? 'unranked' : r[c.key].toLocaleString()) : (r[c.key] ?? ''); }
          else if (c.html) td.innerHTML = r[c.key] ?? '';
          else { td.textContent = r[c.key] ?? ''; if (c.key === 'context') td.className = 'context'; if (c.key === 'surface') td.className = 'surface'; }
          tr.append(td);
        }
        frag.append(tr);
      }
      tbody.append(frag);
      count.textContent = filtered.length.toLocaleString() + ' of ' + s.rows.length.toLocaleString() + ' rows' + (shown < filtered.length ? ', showing ' + shown : '');
      more.style.display = shown < filtered.length ? '' : 'none';
    }
    function sort(c) {
      if (sortKey === c.key) asc = !asc; else { sortKey = c.key; asc = !c.numeric; }
      const val = i => { const v = s.rows[i][c.key]; return c.html ? String(v ?? '').replace(/<[^>]+>/g, '') : v; };
      order = order.slice().sort((a, b) => { const x = val(a), y = val(b); const r = c.numeric ? (x - y) : String(x).localeCompare(String(y), 'ja'); return asc ? r : -r; });
      for (const th of tr.children) th.classList.remove('sorted', 'asc');
      const th = tr.children[s.columns.indexOf(c)]; th.classList.add('sorted'); if (asc) th.classList.add('asc');
      apply();
    }
    input.oninput = apply; btn.onclick = () => { shown += PAGE; draw(); };
    apply();
  }
})();
</script>
</body>
</html>
""");
        return sb.ToString();
    }
}
