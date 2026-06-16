using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.Commands;

public class TtsCommands(CliContext context)
{
    // Mirrors the keys of TtsService.Voices in Jiten.Api. Kept in sync manually since the CLI
    // does not reference Jiten.Api; the warmup drives the public API endpoint instead.
    private static readonly string[] AllVoices = ["female", "female2", "male", "male2", "asmr"];

    public async Task WarmupWordTts(int topWords, string? apiUrl, int concurrency, string? voicesCsv)
    {
        if (topWords <= 0)
        {
            Console.WriteLine("warmup-tts: top word count must be > 0.");
            return;
        }

        var baseUrl = (apiUrl ?? context.Configuration["ApiBaseUrl"] ?? "https://localhost:7299").TrimEnd('/');
        var ssrKey = context.Configuration["SsrBypassKey"];
        concurrency = concurrency > 0 ? concurrency : 4;

        var voices = string.IsNullOrWhiteSpace(voicesCsv)
            ? AllVoices
            : voicesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var unknown = voices.Where(v => !AllVoices.Contains(v)).ToArray();
        if (unknown.Length > 0)
        {
            Console.WriteLine($"warmup-tts: unknown voice(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", AllVoices)}");
            return;
        }

        if (string.IsNullOrEmpty(ssrKey))
            Console.WriteLine("warmup-tts: WARNING SsrBypassKey not configured — requests will be subject to the per-IP and 15/min generation limits and will likely be throttled.");

        Console.WriteLine($"warmup-tts: target {baseUrl}, top {topWords} words, voices [{string.Join(", ", voices)}], concurrency {concurrency}.");
        
        List<(int WordId, short ReadingIndex)> units;
        await using (var db = await context.ContextFactory.CreateDbContextAsync())
        {
            var topWordIds = await db.JmDictWordFrequencies
                .OrderBy(f => f.FrequencyRank)
                .Take(topWords)
                .Select(f => f.WordId)
                .ToListAsync();

            var rank = topWordIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

            units = (await db.WordForms
                    .Where(f => topWordIds.Contains(f.WordId))
                    .Select(f => new { f.WordId, f.ReadingIndex })
                    .Distinct()
                    .ToListAsync())
                .OrderBy(u => rank[u.WordId])
                .ThenBy(u => u.ReadingIndex)
                .Select(u => (u.WordId, u.ReadingIndex))
                .ToList();
        }

        // Round-robin the voice order per (word, reading) so requests rotate across voices.
        var tasks = new List<(int WordId, short ReadingIndex, string Voice)>(units.Count * voices.Length);
        for (var i = 0; i < units.Count; i++)
            for (var v = 0; v < voices.Length; v++)
                tasks.Add((units[i].WordId, units[i].ReadingIndex, voices[(i + v) % voices.Length]));

        Console.WriteLine($"warmup-tts: {units.Count} (word, reading) pairs x {voices.Length} voices = {tasks.Count} requests.");

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        var done = 0;
        var failed = 0;
        var sw = Stopwatch.StartNew();
        using var sem = new SemaphoreSlim(concurrency);

        var running = tasks.Select(async t =>
        {
            await sem.WaitAsync();
            try
            {
                var url = $"{baseUrl}/api/tts/word/{t.WordId}/{t.ReadingIndex}?voice={t.Voice}";
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        if (!string.IsNullOrEmpty(ssrKey)) req.Headers.Add("X-Internal-Ssr-Key", ssrKey);
                        using var resp = await client.SendAsync(req);

                        if (resp.StatusCode == (HttpStatusCode)429 && attempt < 5)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
                            continue;
                        }

                        if (!resp.IsSuccessStatusCode)
                        {
                            Interlocked.Increment(ref failed);
                            if (failed <= 20)
                                Console.WriteLine($"  [{(int)resp.StatusCode}] {t.WordId}/{t.ReadingIndex} {t.Voice}");
                        }
                        break;
                    }
                    catch (Exception ex) when (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
                        if (attempt == 2)
                        {
                            Interlocked.Increment(ref failed);
                            Console.WriteLine($"  [err] {t.WordId}/{t.ReadingIndex} {t.Voice}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                sem.Release();
                var n = Interlocked.Increment(ref done);
                if (n % 200 == 0 || n == tasks.Count)
                {
                    var rate = n / Math.Max(1, sw.Elapsed.TotalSeconds);
                    Console.WriteLine($"warmup-tts: {n}/{tasks.Count} ({failed} failed, {rate:F1}/s)");
                }
            }
        });

        await Task.WhenAll(running);
        Console.WriteLine($"warmup-tts: done in {sw.Elapsed:hh\\:mm\\:ss}. {tasks.Count - failed} ok, {failed} failed.");
    }
}
