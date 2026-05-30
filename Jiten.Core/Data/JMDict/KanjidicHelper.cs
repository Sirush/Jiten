using System.Text;
using System.Xml;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Data.JMDict;

public static class KanjidicHelper
{
    /// <summary>
    /// Imports kanji data from KANJIDIC2 XML file.
    /// </summary>
    public static async Task<bool> Import(IDbContextFactory<JitenDbContext> contextFactory, string kanjidicPath)
    {
        Console.WriteLine("Parsing KANJIDIC2 XML...");
        var kanjis = await ParseKanjidic(kanjidicPath);
        Console.WriteLine($"Parsed {kanjis.Count} kanji entries.");

        await using var context = await contextFactory.CreateDbContextAsync();

        // Clear existing kanji data
        Console.WriteLine("Clearing existing kanji data...");
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jmdict.\"WordKanji\"");
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jmdict.\"Kanji\" CASCADE");

        // Insert kanji
        Console.WriteLine("Inserting kanji...");
        const int batchSize = 1000;
        for (int i = 0; i < kanjis.Count; i += batchSize)
        {
            var batch = kanjis.Skip(i).Take(batchSize).ToList();
            context.Kanjis.AddRange(batch);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Console.WriteLine($"Inserted {Math.Min(i + batchSize, kanjis.Count)}/{kanjis.Count} kanji...");
        }

        Console.WriteLine("Kanji import complete.");
        return true;
    }

    /// <summary>
    /// Populates the WordKanji junction table by extracting kanji from JmDictWord readings.
    /// Must be called after kanji import.
    /// </summary>
    public static async Task<bool> PopulateWordKanji(IDbContextFactory<JitenDbContext> contextFactory)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // Get all kanji characters in the database for validation
        var existingKanji = await context.Kanjis
            .AsNoTracking()
            .Select(k => k.Character)
            .ToHashSetAsync();

        Console.WriteLine($"Found {existingKanji.Count} kanji in database.");

        // Clear existing WordKanji data
        Console.WriteLine("Clearing existing WordKanji data...");
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jmdict.\"WordKanji\"");

        // Process words in batches
        const int batchSize = 10000;
        int totalWords = await context.JMDictWords.CountAsync();
        int processedWords = 0;
        var wordKanjiList = new List<WordKanji>();

        Console.WriteLine($"Processing {totalWords} words...");

        while (processedWords < totalWords)
        {
            var batchWordIds = await context.JMDictWords
                .AsNoTracking()
                .OrderBy(w => w.WordId)
                .Skip(processedWords)
                .Take(batchSize)
                .Select(w => w.WordId)
                .ToListAsync();

            if (batchWordIds.Count == 0) break;

            var wordForms = await context.WordForms
                .AsNoTracking()
                .Where(wf => batchWordIds.Contains(wf.WordId))
                .OrderBy(wf => wf.WordId)
                .ThenBy(wf => wf.ReadingIndex)
                .ToListAsync();

            var words = wordForms.GroupBy(wf => wf.WordId).ToList();

            foreach (var wordGroup in words)
            {
                foreach (var form in wordGroup)
                {
                    short readingIndex = form.ReadingIndex;
                    var reading = form.Text;
                    short position = 0;

                    foreach (var rune in reading.EnumerateRunes())
                    {
                        if (JapaneseTextHelper.IsKanji(rune))
                        {
                            var kanjiStr = rune.ToString();
                            if (existingKanji.Contains(kanjiStr))
                            {
                                wordKanjiList.Add(new WordKanji
                                {
                                    WordId = form.WordId,
                                    ReadingIndex = readingIndex,
                                    KanjiCharacter = kanjiStr,
                                    Position = position
                                });
                            }
                        }
                        position++;
                    }
                }
            }

            processedWords += batchWordIds.Count;
            Console.WriteLine($"Processed {processedWords}/{totalWords} words, found {wordKanjiList.Count} word-kanji pairs...");

            // Insert in batches to avoid memory issues
            if (wordKanjiList.Count >= 50000)
            {
                await InsertWordKanjiBatch(context, wordKanjiList);
                wordKanjiList.Clear();
            }
        }

        // Insert remaining
        if (wordKanjiList.Count > 0)
        {
            await InsertWordKanjiBatch(context, wordKanjiList);
        }

        Console.WriteLine("WordKanji population complete.");
        return true;
    }

    private static async Task InsertWordKanjiBatch(JitenDbContext context, List<WordKanji> wordKanjiList)
    {
        // Deduplicate (same kanji can appear multiple times in a word at different positions)
        var distinct = wordKanjiList
            .GroupBy(wk => new { wk.WordId, wk.ReadingIndex, wk.KanjiCharacter, wk.Position })
            .Select(g => g.First())
            .ToList();

        const int insertBatchSize = 5000;
        for (int i = 0; i < distinct.Count; i += insertBatchSize)
        {
            var batch = distinct.Skip(i).Take(insertBatchSize).ToList();
            context.WordKanjis.AddRange(batch);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
    }

    public static async Task<bool> ComputeKanjiReadings(IDbContextFactory<JitenDbContext> contextFactory)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var allKanji = await context.Kanjis.AsNoTracking().ToListAsync();
        Console.WriteLine($"Found {allKanji.Count} kanji in database.");

        var decomposer = new KanjiReadingDecomposer(allKanji);
        Console.WriteLine($"Expanded to {decomposer.TotalReadingCandidates} reading candidates.");

        Console.WriteLine("Clearing existing KanjiReadingWords data...");
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jmdict.\"KanjiReadingWords\"");

        const int batchSize = 10000;
        int totalWords = await context.JMDictWords.CountAsync();
        int processedWords = 0;
        int decomposedCount = 0;
        int failedCount = 0;
        var resultList = new List<KanjiReadingWord>();
        var failedExamples = new List<(string kanji, string kana)>();

        Console.WriteLine($"Processing {totalWords} words...");

        while (processedWords < totalWords)
        {
            var batchWordIds = await context.JMDictWords
                .AsNoTracking()
                .OrderBy(w => w.WordId)
                .Skip(processedWords)
                .Take(batchSize)
                .Select(w => w.WordId)
                .ToListAsync();

            if (batchWordIds.Count == 0) break;

            var wordForms = await context.WordForms
                .AsNoTracking()
                .Where(wf => batchWordIds.Contains(wf.WordId))
                .OrderBy(wf => wf.WordId)
                .ThenBy(wf => wf.ReadingIndex)
                .ToListAsync();

            var words = wordForms.GroupBy(wf => wf.WordId).ToList();

            foreach (var wordGroup in words)
            {
                var allForms = wordGroup.ToList();
                var kanjiForms = allForms.Where(f => f.FormType == JmDictFormType.KanjiForm).ToList();
                var kanaForms = allForms.Where(f => f.FormType == JmDictFormType.KanaForm).ToList();

                if (kanjiForms.Count == 0 || kanaForms.Count == 0) continue;

                foreach (var kanjiForm in kanjiForms)
                {
                    if (!kanjiForm.Text.Any(c => JapaneseTextHelper.IsKanji(c)))
                        continue;

                    var kanaForm = kanaForms.FirstOrDefault(f => f.ReadingIndex == kanjiForm.ReadingIndex)
                                  ?? kanaForms[0];

                    // Prefer the curated furigana (RubyText) — it splits regular compounds per-kanji
                    // and groups jukujikun as a whole span. Fall back to KANJIDIC backtracking only
                    // when no furigana brackets are present.
                    List<(string KanjiChar, string Reading)>? pairs = kanjiForm.RubyText.Contains('[')
                        ? decomposer.DecomposeFromRuby(kanjiForm.RubyText)
                        : decomposer.Decompose(kanjiForm.Text, kanaForm.Text);
                    if (pairs == null)
                    {
                        failedCount++;
                        if (failedExamples.Count < 20)
                            failedExamples.Add((kanjiForm.Text, kanaForm.Text));
                        continue;
                    }

                    decomposedCount++;
                    foreach (var (kanjiChar, reading) in pairs)
                    {
                        resultList.Add(new KanjiReadingWord
                        {
                            KanjiCharacter = kanjiChar,
                            Reading = reading,
                            WordId = kanjiForm.WordId,
                            ReadingIndex = kanjiForm.ReadingIndex
                        });
                    }
                }
            }

            processedWords += batchWordIds.Count;
            Console.WriteLine($"Processed {processedWords}/{totalWords} words, " +
                              $"{decomposedCount} decomposed, {failedCount} failed, " +
                              $"{resultList.Count} reading pairs...");

            if (resultList.Count >= 50000)
            {
                await InsertKanjiReadingBatch(context, resultList);
                resultList.Clear();
            }
        }

        if (resultList.Count > 0)
        {
            await InsertKanjiReadingBatch(context, resultList);
        }

        var total = decomposedCount + failedCount;
        var pct = total > 0 ? (double)decomposedCount / total * 100 : 0;
        Console.WriteLine($"\nComplete: {decomposedCount} decomposed, {failedCount} failed ({pct:F1}% coverage)");

        if (failedExamples.Count > 0)
        {
            Console.WriteLine("\nSample failed decompositions:");
            foreach (var (kanji, kana) in failedExamples)
                Console.WriteLine($"  {kanji} ({kana})");
        }

        Console.WriteLine("KanjiReadingWords computation complete.");
        return true;
    }

    private static async Task InsertKanjiReadingBatch(JitenDbContext context, List<KanjiReadingWord> items)
    {
        var distinct = items
            .GroupBy(kr => new { kr.KanjiCharacter, kr.Reading, kr.WordId, kr.ReadingIndex })
            .Select(g => g.First())
            .ToList();

        const int insertBatchSize = 5000;
        for (int i = 0; i < distinct.Count; i += insertBatchSize)
        {
            var batch = distinct.Skip(i).Take(insertBatchSize).ToList();
            context.KanjiReadingWords.AddRange(batch);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
    }

    private static async Task<List<Kanji>> ParseKanjidic(string kanjidicPath)
    {
        var kanjis = new List<Kanji>();

        var readerSettings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Parse,
            MaxCharactersFromEntities = 0
        };

        using var reader = XmlReader.Create(kanjidicPath, readerSettings);
        await reader.MoveToContentAsync();

        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name != "character") continue;

            var kanji = await ParseCharacter(reader);
            if (kanji != null)
            {
                kanjis.Add(kanji);
            }
        }

        return kanjis;
    }

    private static async Task<Kanji?> ParseCharacter(XmlReader reader)
    {
        var kanji = new Kanji
        {
            OnReadings = [],
            KunReadings = [],
            Meanings = []
        };

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "literal":
                        var literal = await reader.ReadElementContentAsStringAsync();
                        // Accept all kanji including non-BMP characters (CJK Extension B/C/D/E)
                        // Validate it's exactly one Unicode code point
                        if (literal.EnumerateRunes().Count() == 1)
                        {
                            kanji.Character = literal;
                        }
                        else
                        {
                            return null;
                        }
                        break;

                    case "misc":
                        await ParseMisc(reader, kanji);
                        break;

                    case "reading_meaning":
                        await ParseReadingMeaning(reader, kanji);
                        break;
                }
            }

            if (reader is { NodeType: XmlNodeType.EndElement, Name: "character" })
            {
                break;
            }
        }

        return kanji;
    }

    private static async Task ParseMisc(XmlReader reader, Kanji kanji)
    {
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "grade":
                        var gradeStr = await reader.ReadElementContentAsStringAsync();
                        if (short.TryParse(gradeStr, out short grade))
                        {
                            kanji.Grade = grade;
                        }
                        break;

                    case "stroke_count":
                        // Take the first stroke count (primary)
                        if (kanji.StrokeCount == 0)
                        {
                            var strokeStr = await reader.ReadElementContentAsStringAsync();
                            if (short.TryParse(strokeStr, out short strokeCount))
                            {
                                kanji.StrokeCount = strokeCount;
                            }
                        }
                        break;

                    case "jlpt":
                        var jlptStr = await reader.ReadElementContentAsStringAsync();
                        if (short.TryParse(jlptStr, out short jlpt))
                        {
                            kanji.JlptLevel = jlpt;
                        }
                        break;
                }
            }

            if (reader is { NodeType: XmlNodeType.EndElement, Name: "misc" })
            {
                break;
            }
        }
    }

    private static async Task ParseReadingMeaning(XmlReader reader, Kanji kanji)
    {
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "rmgroup")
                {
                    await ParseRmGroup(reader, kanji);
                }
            }

            if (reader is { NodeType: XmlNodeType.EndElement, Name: "reading_meaning" })
            {
                break;
            }
        }
    }

    private static async Task ParseRmGroup(XmlReader reader, Kanji kanji)
    {
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "reading":
                        var rType = reader.GetAttribute("r_type");
                        var readingText = await reader.ReadElementContentAsStringAsync();

                        if (rType == "ja_on")
                        {
                            kanji.OnReadings.Add(readingText);
                        }
                        else if (rType == "ja_kun")
                        {
                            kanji.KunReadings.Add(readingText);
                        }
                        break;

                    case "meaning":
                        // Only take English meanings (no m_lang attribute)
                        var mLang = reader.GetAttribute("m_lang");
                        if (string.IsNullOrEmpty(mLang))
                        {
                            var meaning = await reader.ReadElementContentAsStringAsync();
                            kanji.Meanings.Add(meaning);
                        }
                        break;
                }
            }

            if (reader is { NodeType: XmlNodeType.EndElement, Name: "rmgroup" })
            {
                break;
            }
        }
    }
}
