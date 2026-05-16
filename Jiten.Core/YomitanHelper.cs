using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core;

public static class YomitanHelper
{
    /// <summary>
    /// Generates the content for the index.json file in a Yomitan dictionary.
    /// </summary>
    public static string GetIndexJson(MediaType? mediaType)
    {
        string title = mediaType != null ? $"Jiten ({mediaType})" : "Jiten";
        string revision = mediaType != null ? $"Jiten ({mediaType}) {DateTime.UtcNow:yy-MM-dd}" : $"Jiten {DateTime.UtcNow:yy-MM-dd}";
        string description = mediaType != null
            ? $"Dictionary based on frequency data of {mediaType} from jiten.moe"
            : "Dictionary based on frequency data of all media from jiten.moe";
        string indexUrl = mediaType != null
            ? $"https://api.jiten.moe/api/frequency-list/index?mediaType={mediaType}"
            : "https://api.jiten.moe/api/frequency-list/index";
        string downloadUrl = mediaType != null
            ? $"https://api.jiten.moe/api/frequency-list/download?mediaType={mediaType}"
            : "https://api.jiten.moe/api/frequency-list/download";

        return
            $$"""{"title":"{{title}}","format":3,"revision":"{{revision}}","isUpdatable":true,"indexUrl":"{{indexUrl}}","downloadUrl":"{{downloadUrl}}","sequenced":false,"frequencyMode":"rank-based","author":"Jiten","url":"https://jiten.moe","description":"{{description}}"}""";
    }

    /// <summary>
    /// Generates a zipped Yomitan frequency dictionary for a given media type.
    /// </summary>
    public static async Task<byte[]> GenerateYomitanFrequencyDeck(IDbContextFactory<JitenDbContext> contextFactory,
                                                                  List<JmDictWordFrequency> frequencies,
                                                                  List<JmDictWordFormFrequency> formFrequencies,
                                                                  MediaType? mediaType, string indexJson)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var wordIds = frequencies.Select(f => f.WordId).ToList();

        var allForms = await context.WordForms.AsNoTracking()
                                    .Where(wf => wordIds.Contains(wf.WordId))
                                    .ToListAsync();
        var formsByWord = allForms.GroupBy(wf => wf.WordId).ToDictionary(g => g.Key, g => g.OrderBy(wf => wf.ReadingIndex).ToList());

        var allFormFreqs = formFrequencies.ToDictionary(wff => (wff.WordId, wff.ReadingIndex));

        var yomitanTermList = new List<List<object>>();
        var addedEntries = new HashSet<string>();

        foreach (var freq in frequencies)
        {
            if (!formsByWord.TryGetValue(freq.WordId, out var wordForms)) continue;

            var kanaReadings = GetKanaReadingsWithFrequenciesFromForms(wordForms, allFormFreqs, freq.WordId);
            if (kanaReadings.Count == 0) continue;

            // CASE 1: Create standalone kana entries
            foreach (var (kanaText, kanaRank, kanaIndex) in kanaReadings)
            {
                var kanaFormFreq = allFormFreqs.GetValueOrDefault((freq.WordId, (short)kanaIndex));
                if (kanaFormFreq == null || kanaFormFreq.UsedInMediaAmount <= 0)
                    continue;

                if (addedEntries.Contains(kanaText))
                    continue;

                yomitanTermList.Add([
                    kanaText, "freq",
                    new Dictionary<string, object> { ["value"] = kanaRank, ["displayValue"] = $"{kanaRank}㋕" }
                ]);
                addedEntries.Add(kanaText);
            }

            // CASE 2: Create kanji entries
            foreach (var form in wordForms)
            {
                if (form.FormType != JmDictFormType.KanjiForm)
                    continue;

                var kanjiTerm = form.Text;
                var kanjiFormFreq = allFormFreqs.GetValueOrDefault((freq.WordId, form.ReadingIndex));
                var kanjiRank = kanjiFormFreq?.FrequencyRank ?? 0;

                // Skip if kanji form was never observed
                if (kanjiFormFreq == null || kanjiFormFreq.UsedInMediaAmount <= 0)
                    continue;

                // Also skip if rank is invalid
                if (kanjiRank is <= 0 or >= int.MaxValue - 1000)
                    continue;

                foreach (var (kanaText, kanaRank, kanaIndex) in kanaReadings)
                {
                    var kanaIdxFormFreq = allFormFreqs.GetValueOrDefault((freq.WordId, (short)kanaIndex));
                    bool isKanaObserved = kanaIdxFormFreq != null && kanaIdxFormFreq.UsedInMediaAmount > 0;

                    if (isKanaObserved)
                    {
                        string entryKey1 = $"{kanjiTerm}:{kanaText}:kana";
                        if (!addedEntries.Contains(entryKey1))
                        {
                            yomitanTermList.Add([
                                kanjiTerm, "freq",
                                new Dictionary<string, object>
                                {
                                    ["reading"] = kanaText,
                                    ["frequency"] = new Dictionary<string, object>
                                                    {
                                                        ["value"] = kanaRank, ["displayValue"] = $"{kanaRank}㋕"
                                                    }
                                }
                            ]);
                            addedEntries.Add(entryKey1);
                        }
                    }

                    string entryKey2 = $"{kanjiTerm}:{kanaText}:kanji";
                    if (addedEntries.Contains(entryKey2))
                        continue;

                    yomitanTermList.Add([
                        kanjiTerm, "freq",
                        new Dictionary<string, object>
                        {
                            ["reading"] = kanaText,
                            ["frequency"] = new Dictionary<string, object>
                                            {
                                                ["value"] = kanjiRank, ["displayValue"] = kanjiRank.ToString()
                                            }
                        }
                    ]);
                    addedEntries.Add(entryKey2);
                }
            }
        }

        // Sort by frequency rank (best/lowest rank first)
        yomitanTermList.Sort((a, b) =>
        {
            int GetRank(List<object> entry)
            {
                var freqData = (Dictionary<string, object>)entry[2];
                if (freqData.ContainsKey("frequency"))
                    return (int)((Dictionary<string, object>)freqData["frequency"])["value"];
                else
                    return (int)freqData["value"];
            }

            return GetRank(a).CompareTo(GetRank(b));
        });

        var termBankJson = JsonSerializer.Serialize(yomitanTermList,
                                                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            var indexEntry = archive.CreateEntry("index.json", CompressionLevel.Optimal);
            await using (var entryStream = indexEntry.Open())
            await using (var streamWriter = new StreamWriter(entryStream, new UTF8Encoding(false)))
            {
                await streamWriter.WriteAsync(indexJson);
            }

            var termBankEntry = archive.CreateEntry("term_meta_bank_1.json", CompressionLevel.Optimal);
            await using (var entryStream = termBankEntry.Open())
            await using (var streamWriter = new StreamWriter(entryStream, new UTF8Encoding(false)))
            {
                await streamWriter.WriteAsync(termBankJson);
            }
        }

        return memoryStream.ToArray();
    }

    /// <summary>
    /// Retrieves kana readings. Includes:
    /// 1. Any reading that was observed (UsedInMediaAmount > 0)
    /// 2. The FIRST defined kana reading (Main Reading), if the word was observed at all.
    /// </summary>
    private static List<(string kanaText, int rank, int readingIndex)> GetKanaReadingsWithFrequenciesFromForms(
        List<JmDictWordForm> wordForms,
        Dictionary<(int, short), JmDictWordFormFrequency> formFreqs,
        int wordId)
    {
        // Check if the word as a whole was observed
        bool wordWasObserved = wordForms.Any(wf =>
        {
            var ff = formFreqs.GetValueOrDefault((wordId, wf.ReadingIndex));
            return ff != null && ff.UsedInMediaAmount > 0;
        });
        if (!wordWasObserved) return new List<(string, int, int)>();

        var kanaForms = wordForms.Where(wf => wf.FormType == JmDictFormType.KanaForm).ToList();
        short? firstKanaIndex = kanaForms.FirstOrDefault()?.ReadingIndex;

        var kanaReadings = new List<(string kanaText, int rank, int readingIndex)>();

        foreach (var form in kanaForms)
        {
            var ff = formFreqs.GetValueOrDefault((wordId, form.ReadingIndex));
            bool isObserved = ff != null && ff.UsedInMediaAmount > 0;
            bool isMain = form.ReadingIndex == firstKanaIndex;

            if (!isObserved && !isMain)
                continue;

            kanaReadings.Add((form.Text, ff?.FrequencyRank ?? 0, form.ReadingIndex));
        }

        return kanaReadings;
    }

    /// <summary>
    /// Generates a zipped Yomitan frequency dictionary from a Deck.
    /// </summary>
    public static async Task<byte[]> GenerateYomitanFrequencyDeckFromDeck(IDbContextFactory<JitenDbContext> contextFactory, Deck deck)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        string title = "";
        if (deck.ParentDeckId != null)
        {
            var parentDeck = await context.Decks.AsNoTracking()
                                          .FirstOrDefaultAsync(d => d.DeckId == deck.ParentDeckId);
            if (parentDeck != null)
            {
                title += parentDeck.OriginalTitle.Substring(0, Math.Min(parentDeck.OriginalTitle.Length, 15)) + " - ";
            }
        }

        title += deck.OriginalTitle.Substring(0, Math.Min(deck.OriginalTitle.Length, 15));
        string revision = $"Jiten ({title}) {DateTime.UtcNow:yy-MM-dd}";
        string description = $"Dictionary based on frequency data of {title} from jiten.moe";

        var indexJson =
            $$"""{"title":"{{title}}","format":3,"revision":"{{revision}}","sequenced":false,"frequencyMode":"occurrence-based","author":"Jiten","url":"https://jiten.moe","description":"{{description}}"}""";

        var deckWords = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == deck.DeckId).ToList();

        var wordIds = deckWords.Select(dw => dw.WordId).Distinct().ToList();

        var allForms2 = await context.WordForms.AsNoTracking()
                                     .Where(wf => wordIds.Contains(wf.WordId))
                                     .ToListAsync();
        var formsByWord2 = allForms2.GroupBy(wf => wf.WordId).ToDictionary(g => g.Key, g => g.OrderBy(wf => wf.ReadingIndex).ToList());

        var yomitanTermList = new List<List<object>>();
        var addedEntries = new HashSet<string>();

        var deckWordsByWordIdAndReading = deckWords
                                          .GroupBy(dw => new { dw.WordId, dw.ReadingIndex })
                                          .ToDictionary(g => g.Key, g => g.Sum(dw => dw.Occurrences));

        var deckWordsByWordId = deckWords.GroupBy(dw => dw.WordId)
                                         .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var wordId in wordIds)
        {
            if (!formsByWord2.TryGetValue(wordId, out var wordForms) ||
                !deckWordsByWordId.TryGetValue(wordId, out var wordDeckWords)) continue;

            var kanaReadings = GetKanaReadingsWithOccurrencesFromForms(wordForms, deckWordsByWordIdAndReading, wordId);
            if (kanaReadings.Count == 0) continue;

            // CASE 1: Create standalone kana entries
            foreach (var (kanaText, kanaOccurrences, kanaIndex) in kanaReadings)
            {
                if (kanaOccurrences <= 0) continue;
                if (addedEntries.Contains(kanaText)) continue;

                yomitanTermList.Add([
                    kanaText, "freq",
                    new Dictionary<string, object> { ["value"] = kanaOccurrences, ["displayValue"] = $"{kanaOccurrences}㋕" }
                ]);
                addedEntries.Add(kanaText);
            }

            // CASE 2: Create kanji entries
            foreach (var form in wordForms)
            {
                if (form.FormType != JmDictFormType.KanjiForm) continue;

                var kanjiTerm = form.Text;
                var key = new { WordId = wordId, ReadingIndex = (byte)form.ReadingIndex };
                var hasKanjiOccurrences = deckWordsByWordIdAndReading.TryGetValue(key, out int kanjiOccurrences);

                if (!hasKanjiOccurrences || kanjiOccurrences == 0)
                    continue;

                foreach (var (kanaText, kanaOccurrences, kanaIndex) in kanaReadings)
                {
                    bool isKanaObserved = kanaOccurrences > 0;

                    if (isKanaObserved)
                    {
                        string entryKey1 = $"{kanjiTerm}:{kanaText}:kana";
                        if (!addedEntries.Contains(entryKey1))
                        {
                            yomitanTermList.Add(new List<object>
                                                {
                                                    kanjiTerm, "freq",
                                                    new Dictionary<string, object>
                                                    {
                                                        ["reading"] = kanaText,
                                                        ["frequency"] = new Dictionary<string, object>
                                                                        {
                                                                            ["value"] = kanaOccurrences,
                                                                            ["displayValue"] = $"{kanaOccurrences}㋕"
                                                                        }
                                                    }
                                                });
                            addedEntries.Add(entryKey1);
                        }
                    }

                    string entryKey2 = $"{kanjiTerm}:{kanaText}:kanji";
                    if (addedEntries.Contains(entryKey2)) continue;
                    yomitanTermList.Add([
                        kanjiTerm,
                        "freq",
                        new Dictionary<string, object>
                        {
                            ["reading"] = kanaText,
                            ["frequency"] = new Dictionary<string, object>
                                            {
                                                ["value"] = kanjiOccurrences, ["displayValue"] = kanjiOccurrences.ToString()
                                            }
                        }
                    ]);
                    addedEntries.Add(entryKey2);
                }
            }
        }

        // Sort by occurrences (highest first for occurrence-based)
        yomitanTermList.Sort((a, b) =>
        {
            int GetOccurrences(List<object> entry)
            {
                var freqData = (Dictionary<string, object>)entry[2];
                if (freqData.ContainsKey("frequency"))
                    return (int)((Dictionary<string, object>)freqData["frequency"])["value"];
                else
                    return (int)freqData["value"];
            }

            return GetOccurrences(b).CompareTo(GetOccurrences(a));
        });

        var termBankJson = JsonSerializer.Serialize(yomitanTermList,
                                                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            var indexEntry = archive.CreateEntry("index.json", CompressionLevel.Optimal);
            await using (var entryStream = indexEntry.Open())
            await using (var streamWriter = new StreamWriter(entryStream, new UTF8Encoding(false)))
            {
                await streamWriter.WriteAsync(indexJson);
            }

            var termBankEntry = archive.CreateEntry("term_meta_bank_1.json", CompressionLevel.Optimal);
            await using (var entryStream = termBankEntry.Open())
            await using (var streamWriter = new StreamWriter(entryStream, new UTF8Encoding(false)))
            {
                await streamWriter.WriteAsync(termBankJson);
            }
        }

        return memoryStream.ToArray();
    }

    private static List<(string kanaText, int occurrences, int readingIndex)> GetKanaReadingsWithOccurrencesFromForms<TKey>(
        List<JmDictWordForm> wordForms,
        IReadOnlyDictionary<TKey, int> deckWordsByWordIdAndReading,
        int wordId) where TKey : notnull
    {
        bool wordObserved = false;
        foreach (var form in wordForms)
        {
            var key = (TKey)(object)new { WordId = wordId, ReadingIndex = (byte)form.ReadingIndex };
            if (deckWordsByWordIdAndReading.TryGetValue(key, out int occ) && occ > 0)
            {
                wordObserved = true;
                break;
            }
        }

        if (!wordObserved) return new List<(string, int, int)>();

        var kanaForms = wordForms.Where(wf => wf.FormType == JmDictFormType.KanaForm).ToList();
        short? firstKanaIndex = kanaForms.FirstOrDefault()?.ReadingIndex;

        var kanaReadings = new List<(string kanaText, int occurrences, int readingIndex)>();

        foreach (var form in kanaForms)
        {
            var key = (TKey)(object)new { WordId = wordId, ReadingIndex = (byte)form.ReadingIndex };
            int occurrences = 0;
            if (deckWordsByWordIdAndReading.TryGetValue(key, out int val))
                occurrences = val;

            bool isObserved = occurrences > 0;
            bool isMain = form.ReadingIndex == firstKanaIndex;

            if (!isObserved && !isMain)
                continue;

            kanaReadings.Add((form.Text, occurrences, form.ReadingIndex));
        }

        return kanaReadings;
    }

    /// <summary>
    /// Generates the content for the index.json file in a Yomitan kanji frequency dictionary.
    /// </summary>
    public static string GetKanjiIndexJson()
    {
        string title = "Jiten (Kanji)";
        string revision = $"Jiten (Kanji) {DateTime.UtcNow:yy-MM-dd}";
        string description = "Kanji frequency dictionary based on all media from jiten.moe";

        return
            $$"""{"title":"{{title}}","format":3,"revision":"{{revision}}","sequenced":false,"isUpdatable":true",indexUrl":"https://api.jiten.moe/api/frequency-list/index-kanji","frequencyMode":"rank-based","author":"Jiten","url":"https://jiten.moe","description":"{{description}}"}""";
    }

    /// <summary>
    /// Generates a zipped Yomitan kanji frequency dictionary from pre-computed frequencies.
    /// </summary>
    public static byte[] GenerateYomitanKanjiFrequencyDeck(List<(string kanji, int rank)> kanjiFrequencies)
    {
        var yomitanTermList = new List<object>();

        foreach (var (kanji, rank) in kanjiFrequencies)
        {
            yomitanTermList.Add(new object[] { kanji, "freq", new { value = rank, displayValue = rank.ToString() } });
        }

        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var termBankJson = JsonSerializer.Serialize(yomitanTermList, options);
        var indexJson = GetKanjiIndexJson();

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            AddZipEntry(archive, "index.json", indexJson);
            AddZipEntry(archive, "kanji_meta_bank_1.json", termBankJson);
        }

        return memoryStream.ToArray();
    }

    private static void AddZipEntry(ZipArchive archive, string fileName, string content)
    {
        var entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}