using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Web;
using Jiten.Api.Dtos;
using Jiten.Core.Data;

namespace Jiten.Api.Services;

public static class CorpusReportService
{
    private static readonly string[] ChartColors = ["#bd93f9", "#50fa7b", "#ff79c6", "#8be9fd", "#f1fa8c"];

    public static string GenerateReport(
        CorpusSearchResponse data,
        List<CorpusCoOccurrence> coOccurrences,
        CorpusSearchRequest request)
    {
        // Mirror the dashboard ordering: when enabled, list terms by descending occurrence count.
        var results = request.SortByOccurrence
            ? data.Results.OrderByDescending(r => r.TotalOccurrences).ToList()
            : data.Results;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>Corpus Report — {E(string.Join(", ", request.Terms))}</title>");
        sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/chart.js@4/dist/chart.umd.min.js\"></script>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<header>");
        sb.AppendLine("<h1>Jiten Corpus Analysis Report</h1>");
        sb.AppendLine($"<p class=\"meta\">Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</p>");
        var scopeLabel = data.FilteredScope.HasFilters ? "Filtered scope" : "Searchable corpus";
        sb.AppendLine($"<p class=\"meta\">{scopeLabel}: {data.FilteredScope.Decks:N0} decks · {data.FilteredScope.Works:N0} works · {data.FilteredScope.Characters:N0} characters</p>");
        if (data.FilteredScope.HasFilters)
            sb.AppendLine($"<p class=\"meta\">Narrowed by filters (year / difficulty / media type) from {data.CorpusStats.DecksWithRawText:N0} decks · {data.CorpusStats.TotalWorks:N0} works · {data.CorpusStats.TotalCharacters:N0} characters total — all stats are relative to this scope.</p>");
        sb.AppendLine($"<p class=\"meta\">Terms: {E(string.Join(" · ", request.Terms))}</p>");

        if (request.MediaTypes is { Count: > 0 })
            sb.AppendLine($"<p class=\"meta\">Media filter: {string.Join(", ", request.MediaTypes)}</p>");
        if (request.MinDifficulty.HasValue || request.MaxDifficulty.HasValue)
            sb.AppendLine($"<p class=\"meta\">Difficulty: {request.MinDifficulty?.ToString("F1") ?? "0"} – {request.MaxDifficulty?.ToString("F1") ?? "5"}</p>");
        if (request.MinReleaseYear.HasValue || request.MaxReleaseYear.HasValue)
            sb.AppendLine($"<p class=\"meta\">Release years: {request.MinReleaseYear?.ToString() ?? "∞"} – {request.MaxReleaseYear?.ToString() ?? "∞"}</p>");

        sb.AppendLine("</header>");

        if (results.Count > 1)
        {
            sb.AppendLine("<section>");
            sb.AppendLine("<h2>Comparison Overview</h2>");
            sb.AppendLine("<table><thead><tr><th>Term</th><th>Matching Decks</th><th>Occurrences</th><th>Occ/M Chars</th><th>Works (range)</th><th>Dispersion</th><th>Avg Dialogue %</th></tr></thead><tbody>");
            var totalOccurrences = results.Sum(r => r.TotalOccurrences);
            foreach (var r in results)
            {
                var share = totalOccurrences > 0 ? r.TotalOccurrences * 100.0 / totalOccurrences : 0;
                sb.AppendLine($"<tr><td><strong>{E(r.Term)}</strong></td><td>{r.MatchingDecks:N0}</td><td>{r.TotalOccurrences:N0} ({share:N1}%)</td><td>{r.HitsPerMillion:N1}</td><td>{r.WorksMatched:N0} ({r.WorkRangePercentage:N1}%)</td><td>{r.Dispersion:N2}</td><td>{r.DialogueWeightedAvg:N1}%</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
            sb.AppendLine("<p class=\"note\"><strong>Matching Decks</strong> counts every individual deck, including sub-decks (each chapter / episode / volume). <strong>Works (range)</strong> collapses those to their parent title, so a term in all chapters of one novel counts as many decks but a single work — the % is the share of all works in the corpus.</p>");
            sb.AppendLine("<p class=\"note\"><strong>Dispersion</strong> (Gries' Deviation of Proportions) measures how evenly the term is spread across media types relative to each register's size: <strong>0</strong> = used in proportion everywhere, <strong>1</strong> = concentrated in a single register (e.g. only in subtitles or only in novels).</p>");

            if (coOccurrences.Count > 0)
            {
                var matchingDecksByTerm = results
                    .GroupBy(r => r.Term)
                    .ToDictionary(g => g.Key, g => g.First().MatchingDecks);

                sb.AppendLine("<h3>Co-occurrence (shared decks)</h3>");
                sb.AppendLine("<table><thead><tr><th>Term A</th><th>Term B</th><th>Shared Decks</th><th>Overlap %</th><th>Jaccard %</th></tr></thead><tbody>");
                foreach (var co in coOccurrences)
                {
                    var a = matchingDecksByTerm.GetValueOrDefault(co.TermA);
                    var b = matchingDecksByTerm.GetValueOrDefault(co.TermB);
                    var min = Math.Min(a, b);
                    var union = a + b - co.SharedDecks;
                    var overlap = min > 0 ? co.SharedDecks * 100.0 / min : 0;
                    var jaccard = union > 0 ? co.SharedDecks * 100.0 / union : 0;
                    sb.AppendLine($"<tr><td>{E(co.TermA)}</td><td>{E(co.TermB)}</td><td>{co.SharedDecks:N0}</td><td>{overlap:N1}%</td><td>{jaccard:N1}%</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
                sb.AppendLine("<p class=\"note\"><strong>Shared Decks</strong> is the number of decks containing both terms (deck-level, like Matching Decks). <strong>Overlap %</strong> = shared ÷ the rarer term's decks — \"what share of the less common term's decks also contain the other\" (reaches 100% if one term's decks are a subset of the other's). <strong>Jaccard %</strong> = shared ÷ decks containing either term — symmetric, but pulled low when one term is far more common.</p>");
            }

            sb.AppendLine("</section>");
        }

        var chartId = 0;

        if (results.Count > 1)
            RenderCombinedTrendChart(sb, results, ref chartId);

        foreach (var result in results)
        {
            sb.AppendLine("<section class=\"term-section\">");
            sb.AppendLine($"<h2>{E(result.Term)}</h2>");
            sb.AppendLine($"<p>{result.TotalOccurrences:N0} occurrences · {result.HitsPerMillion:N1} occ/M chars · in {result.WorksMatched:N0}/{result.WorksTotal:N0} works ({result.WorkRangePercentage:N1}%) · dispersion {result.Dispersion:N2} · Avg dialogue: {result.DialogueWeightedAvg:N1}%</p>");

            RenderMediaChart(sb, result, ref chartId);
            RenderDifficultyChart(sb, result, ref chartId);
            RenderTrendChart(sb, result, ref chartId);
            RenderSnippets(sb, result);

            sb.AppendLine("</section>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void RenderMediaChart(StringBuilder sb, CorpusTermResult result, ref int chartId)
    {
        if (result.MediaBreakdown.Count == 0) return;

        var id = $"mediaChart{chartId++}";
        var sorted = result.MediaBreakdown.OrderBy(m => m.HitsPerMillion).ToList();
        var labels = JsArray(sorted.Select(m => m.MediaType.ToString()));
        var values = JsNumArray(sorted.Select(m => m.HitsPerMillion));

        sb.AppendLine("<h3>Media Type Breakdown (occ/M chars)</h3>");
        sb.AppendLine("<div class=\"chart-container\">");
        sb.AppendLine($"<canvas id=\"{id}\"></canvas>");
        sb.AppendLine("</div>");
        sb.AppendLine("<script>");
        sb.AppendLine($"new Chart(document.getElementById('{id}'), {{");
        sb.AppendLine("  type: 'bar',");
        sb.AppendLine($"  data: {{ labels: {labels}, datasets: [{{ data: {values}, backgroundColor: '#bd93f9', borderRadius: 4 }}] }},");
        sb.AppendLine("  options: {");
        sb.AppendLine("    indexAxis: 'y', responsive: true, maintainAspectRatio: true, aspectRatio: 2,");
        sb.AppendLine("    plugins: { legend: { display: false }, tooltip: { backgroundColor: 'rgba(0,0,0,0.85)', borderColor: '#bd93f9', borderWidth: 1 } },");
        sb.AppendLine("    scales: {");
        sb.AppendLine("      x: { grid: { color: 'rgba(255,255,255,0.06)' }, ticks: { color: '#aaa' } },");
        sb.AppendLine("      y: { grid: { display: false }, ticks: { color: '#ddd', font: { size: 12 } } }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("});");
        sb.AppendLine("</script>");
    }

    private static void RenderDifficultyChart(StringBuilder sb, CorpusTermResult result, ref int chartId)
    {
        if (result.DifficultyDistribution.Count == 0) return;

        var id = $"diffChart{chartId++}";
        var labels = JsArray(result.DifficultyDistribution.Select(d => $"{d.BucketMin:F0}\u2013{d.BucketMax:F0}"));
        var values = JsNumArray(result.DifficultyDistribution.Select(d => (double)d.DeckCount));

        sb.AppendLine("<h3>Difficulty Distribution</h3>");
        sb.AppendLine("<div class=\"chart-container\">");
        sb.AppendLine($"<canvas id=\"{id}\"></canvas>");
        sb.AppendLine("</div>");
        sb.AppendLine("<script>");
        sb.AppendLine($"new Chart(document.getElementById('{id}'), {{");
        sb.AppendLine("  type: 'bar',");
        sb.AppendLine($"  data: {{ labels: {labels}, datasets: [{{ label: 'Matching Decks', data: {values}, backgroundColor: '#50fa7b', borderRadius: 4 }}] }},");
        sb.AppendLine("  options: {");
        sb.AppendLine("    responsive: true, maintainAspectRatio: true, aspectRatio: 2.5,");
        sb.AppendLine("    plugins: { legend: { display: false }, tooltip: { backgroundColor: 'rgba(0,0,0,0.85)', borderColor: '#50fa7b', borderWidth: 1 } },");
        sb.AppendLine("    scales: {");
        sb.AppendLine("      x: { title: { display: true, text: 'Difficulty', color: '#aaa' }, grid: { display: false }, ticks: { color: '#ddd' } },");
        sb.AppendLine("      y: { title: { display: true, text: 'Decks', color: '#aaa' }, grid: { color: 'rgba(255,255,255,0.06)' }, ticks: { color: '#aaa' } }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("});");
        sb.AppendLine("</script>");
    }

    private static void RenderCombinedTrendChart(StringBuilder sb, List<CorpusTermResult> results, ref int chartId)
    {
        var allYears = results.SelectMany(r => r.Trends.Select(t => t.Year)).Distinct().OrderBy(y => y).ToList();
        if (allYears.Count == 0) return;

        var id = $"combinedTrend{chartId++}";
        var labels = JsArray(allYears.Select(y => y.ToString()));

        sb.AppendLine("<section class=\"term-section\">");
        sb.AppendLine("<h2>Combined Trends</h2>");
        sb.AppendLine("<h3>Frequency by Release Year (per million chars)</h3>");
        sb.AppendLine("<div class=\"chart-container\">");
        sb.AppendLine($"<canvas id=\"{id}\"></canvas>");
        sb.AppendLine("</div>");
        sb.AppendLine("<script>");
        sb.AppendLine($"new Chart(document.getElementById('{id}'), {{");
        sb.AppendLine("  type: 'line',");

        var datasetsJs = new StringBuilder();
        datasetsJs.Append('[');
        for (int i = 0; i < results.Count; i++)
        {
            var yearMap = results[i].Trends.ToDictionary(t => t.Year, t => t.Percentage);
            var values = JsNumArray(allYears.Select(y => yearMap.GetValueOrDefault(y, 0)));
            var color = ChartColors[i % ChartColors.Length];
            if (i > 0) datasetsJs.Append(',');
            datasetsJs.Append($"{{ label: '{E(results[i].Term)}', data: {values}, borderColor: '{color}', backgroundColor: 'transparent', fill: false, tension: 0.3, pointRadius: 3, pointBackgroundColor: '{color}' }}");
        }
        datasetsJs.Append(']');

        sb.AppendLine($"  data: {{ labels: {labels}, datasets: {datasetsJs} }},");
        sb.AppendLine("  options: {");
        sb.AppendLine("    responsive: true, maintainAspectRatio: true, aspectRatio: 3,");
        sb.AppendLine("    plugins: { legend: { display: true, labels: { color: '#ddd' } }, tooltip: { backgroundColor: 'rgba(0,0,0,0.85)', borderColor: '#ff79c6', borderWidth: 1 } },");
        sb.AppendLine("    scales: {");
        sb.AppendLine("      x: { title: { display: true, text: 'Release Year', color: '#aaa' }, grid: { color: 'rgba(255,255,255,0.06)' }, ticks: { color: '#ddd', maxRotation: 45 } },");
        sb.AppendLine("      y: { title: { display: true, text: 'Per million chars', color: '#aaa' }, grid: { color: 'rgba(255,255,255,0.06)' }, ticks: { color: '#aaa' }, beginAtZero: true }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("});");
        sb.AppendLine("</script>");
        sb.AppendLine("</section>");
    }

    private static void RenderTrendChart(StringBuilder sb, CorpusTermResult result, ref int chartId)
    {
        if (result.Trends.Count == 0) return;

        var id = $"trendChart{chartId++}";
        var labels = JsArray(result.Trends.Select(t => t.Year.ToString()));
        var values = JsNumArray(result.Trends.Select(t => t.Percentage));

        sb.AppendLine("<h3>Temporal Trends (Frequency by Release Year (per million chars))</h3>");
        sb.AppendLine("<div class=\"chart-container\">");
        sb.AppendLine($"<canvas id=\"{id}\"></canvas>");
        sb.AppendLine("</div>");
        sb.AppendLine("<script>");
        sb.AppendLine($"new Chart(document.getElementById('{id}'), {{");
        sb.AppendLine("  type: 'line',");
        sb.AppendLine($"  data: {{ labels: {labels}, datasets: [{{ label: 'Per million chars', data: {values}, borderColor: '#ff79c6', backgroundColor: 'rgba(255,121,198,0.15)', fill: true, tension: 0.3, pointRadius: 3, pointBackgroundColor: '#ff79c6' }}] }},");
        sb.AppendLine("  options: {");
        sb.AppendLine("    responsive: true, maintainAspectRatio: true, aspectRatio: 3,");
        sb.AppendLine("    plugins: { legend: { display: false }, tooltip: { backgroundColor: 'rgba(0,0,0,0.85)', borderColor: '#ff79c6', borderWidth: 1 } },");
        sb.AppendLine("    scales: {");
        sb.AppendLine("      x: { title: { display: true, text: 'Release Year', color: '#aaa' }, grid: { color: 'rgba(255,255,255,0.06)' }, ticks: { color: '#ddd', maxRotation: 45 } },");
        sb.AppendLine("      y: { title: { display: true, text: 'Per million chars', color: '#aaa' }, grid: { color: 'rgba(255,255,255,0.06)' }, ticks: { color: '#aaa' }, beginAtZero: true }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("});");
        sb.AppendLine("</script>");
    }

    private static void RenderSnippets(StringBuilder sb, CorpusTermResult result)
    {
        if (result.Snippets.Count == 0) return;

        sb.AppendLine($"<h3>Concordance ({result.Snippets.Count} snippets)</h3>");
        sb.AppendLine("<table class=\"snippets\"><thead><tr><th>#</th><th>Context</th><th>Source</th><th>Type</th><th>Diff</th><th>Year</th></tr></thead><tbody>");

        for (int i = 0; i < result.Snippets.Count; i++)
        {
            var s = result.Snippets[i];
            var source = string.IsNullOrEmpty(s.ParentTitle) ? E(s.DeckTitle) : $"{E(s.ParentTitle)} — {E(s.DeckTitle)}";
            sb.AppendLine($"<tr><td>{i + 1}</td><td class=\"snippet\">{s.Html}</td><td>{source}</td><td>{s.MediaType}</td><td>{s.Difficulty:F1}</td><td>{s.ReleaseYear}</td></tr>");
        }

        sb.AppendLine("</tbody></table>");
    }

    private static string E(string s) => HttpUtility.HtmlEncode(s);

    private static string JsArray(IEnumerable<string> items) =>
        "[" + string.Join(",", items.Select(i => $"'{JsEscape(i)}'")) + "]";

    private static string JsEscape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string JsNumArray(IEnumerable<double> items) =>
        "[" + string.Join(",", items.Select(v => v.ToString("F1", CultureInfo.InvariantCulture))) + "]";

    private const string Css = """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: "Segoe UI", system-ui, -apple-system, sans-serif; background: #1a1a2e; color: #e0e0e0; padding: 2rem; max-width: 1400px; margin: 0 auto; line-height: 1.5; }
        header { margin-bottom: 2rem; border-bottom: 1px solid #333; padding-bottom: 1rem; }
        h1 { font-size: 1.6rem; color: #8be9fd; margin-bottom: 0.5rem; }
        h2 { font-size: 1.3rem; color: #bd93f9; margin: 1.5rem 0 0.5rem; }
        h3 { font-size: 1.05rem; color: #50fa7b; margin: 1.2rem 0 0.5rem; }
        .meta { font-size: 0.85rem; color: #888; }
        .note { font-size: 0.8rem; color: #888; margin: -0.5rem 0 1rem; max-width: 900px; }
        .note strong { color: #aaa; }
        section { margin-bottom: 2rem; }
        .term-section { border: 1px solid #333; border-radius: 8px; padding: 1.5rem; margin-bottom: 1.5rem; background: #16213e; }
        .chart-container { max-width: 800px; margin: 0.5rem 0 1.5rem; }
        table { width: 100%; border-collapse: collapse; margin: 0.5rem 0 1rem; font-size: 0.9rem; }
        th, td { padding: 0.4rem 0.6rem; text-align: left; border-bottom: 1px solid #2a2a4a; }
        th { background: #1a1a3e; color: #8be9fd; font-weight: 600; position: sticky; top: 0; }
        tr:hover { background: #1e2a4a; }
        .snippet { font-family: "Noto Sans JP", "Yu Gothic", sans-serif; font-size: 0.95rem; line-height: 1.6; max-width: 600px; }
        .snippet .keyword { background: #ff79c6; color: #1a1a2e; padding: 0 2px; border-radius: 2px; font-weight: 700; }
        .snippets td { vertical-align: top; }
        strong { color: #f1fa8c; }
        @media print {
            body { background: #fff; color: #222; }
            .term-section { border-color: #ccc; background: #f8f8f8; }
            th { background: #eee; color: #333; }
            h1 { color: #333; } h2 { color: #555; } h3 { color: #666; }
        }
        """;
}
