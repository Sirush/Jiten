using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Jiten.Api.Helpers;
using Jiten.Cli;
using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Jiten.Api.Services;

public class ImportPreviewResponse
{
    public List<ImportMatchedWord> Matched { get; set; } = new();
    public List<string> Unmatched { get; set; } = new();
    public int TotalLines { get; set; }
    public string PreviewToken { get; set; } = "";
}

public class ImportMatchedWord
{
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
    public string Text { get; set; } = "";
    public string Reading { get; set; } = "";
    public int Occurrences { get; set; } = 1;
}

public class ImportCommitRequest
{
    public string PreviewToken { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<int>? ExcludeWordIds { get; set; }
}

public record ImportCommitResult(int? DeckId, string? Error);

public class ImportPreviewTextRequest
{
    public List<string> Lines { get; set; } = new();
    public bool ParseFullText { get; set; }
}

public class ImportToExistingRequest
{
    public string PreviewToken { get; set; } = "";
    public List<int>? ExcludeWordIds { get; set; }
}

public interface IDeckImportService
{
    Task<ImportPreviewResponse> ParseAndPreview(Stream fileStream, string fileName, bool parseFullText = false);
    Task<ImportPreviewResponse> ParseAndPreviewText(List<string> texts, bool parseFullText = false);
    Task<ImportCommitResult> CommitImport(string userId, ImportCommitRequest request);
    Task<ImportCommitResult> ImportToExistingDeck(string userId, int deckId, ImportToExistingRequest request);
}

public partial class DeckImportService(
    IDbContextFactory<JitenDbContext> contextFactory,
    UserDbContext userContext,
    IUserLimitsService userLimits,
    IConnectionMultiplexer redis) : IDeckImportService
{
    private static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(30);
    private static readonly Regex JapaneseRegex = JapanesePattern();
    private static readonly HashSet<string> FullTextOnlyExtensions = [".epub", ".srt", ".ass", ".ssa", ".mokuro"];

    public async Task<ImportPreviewResponse> ParseAndPreview(Stream fileStream, string fileName, bool parseFullText = false)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (FullTextOnlyExtensions.Contains(ext))
            parseFullText = true;

        if (parseFullText && ext is ".epub" or ".srt" or ".ass" or ".ssa" or ".mokuro")
        {
            var fullText = await ExtractTextFromMedia(fileStream, ext);
            return await FullTextParseAndStore(fullText);
        }

        var texts = ext switch
        {
            ".txt" => await ParseTxt(fileStream),
            ".csv" => await ParseCsv(fileStream, ','),
            ".tsv" => await ParseCsv(fileStream, '\t'),
            _ => await ParseTxt(fileStream)
        };

        if (parseFullText)
        {
            var fullText = string.Join("\n", texts);
            return await FullTextParseAndStore(fullText);
        }

        return await DirectLookupAndStore(texts);
    }

    public async Task<ImportPreviewResponse> ParseAndPreviewText(List<string> texts, bool parseFullText = false)
    {
        if (parseFullText)
        {
            var fullText = string.Join("\n", texts);
            return await FullTextParseAndStore(fullText);
        }

        return await DirectLookupAndStore(texts);
    }

    private async Task<string> ExtractTextFromMedia(Stream fileStream, string ext)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"jiten-import-{Guid.NewGuid():N}{ext}");
        try
        {
            await using (var fileOut = File.Create(tempPath))
                await fileStream.CopyToAsync(fileOut);

            if (ext == ".epub")
            {
                var extractor = new EbookExtractor();
                return await extractor.ExtractTextFromEbook(tempPath);
            }

            if (ext == ".mokuro")
            {
                var extractor = new MokuroExtractor();
                return await extractor.Extract(tempPath, false);
            }

            var subExtractor = new SubtitleExtractor();
            return await subExtractor.Extract(tempPath);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }

            var ssaPath = Path.ChangeExtension(tempPath, ".ssa");
            if (ssaPath != tempPath)
                try
                {
                    File.Delete(ssaPath);
                }
                catch
                {
                }
        }
    }

    private const int MaxFullTextChars = 250_000;

    private async Task<ImportPreviewResponse> FullTextParseAndStore(string fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return new ImportPreviewResponse { TotalLines = 0, PreviewToken = await StoreEmpty() };

        if (fullText.Length > MaxFullTextChars)
            fullText = fullText[..MaxFullTextChars];

        var deck = await Parser.Parser.ParseTextToDeck(contextFactory, fullText, storeRawText: false, predictDifficulty: false);

        var matched = deck.DeckWords
                          .GroupBy(dw => (dw.WordId, dw.ReadingIndex))
                          .Select(g =>
                          {
                              var first = g.First();
                              return new ImportMatchedWord
                                     {
                                         WordId = first.WordId, ReadingIndex = first.ReadingIndex, Text = first.OriginalText,
                                         Reading = first.SudachiReading, Occurrences = g.Sum(w => w.Occurrences)
                                     };
                          })
                          .ToList();

        var previewToken = Guid.NewGuid().ToString("N");
        var db = redis.GetDatabase();
        await db.StringSetAsync($"import-preview:{previewToken}", JsonSerializer.Serialize(matched), PreviewTtl);

        return new ImportPreviewResponse { Matched = matched, Unmatched = [], TotalLines = matched.Count, PreviewToken = previewToken };
    }

    private async Task<string> StoreEmpty()
    {
        var token = Guid.NewGuid().ToString("N");
        var db = redis.GetDatabase();
        await db.StringSetAsync($"import-preview:{token}", "[]", PreviewTtl);
        return token;
    }

    private async Task<ImportPreviewResponse> DirectLookupAndStore(List<string> texts)
    {
        var allTexts = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var totalLines = allTexts.Count;

        var textCounts = new Dictionary<string, int>();
        foreach (var t in allTexts)
            textCounts[t] = textCounts.GetValueOrDefault(t) + 1;

        var distinctTexts = textCounts.Keys.ToList();
        var deckWords = await Parser.Parser.GetWordsDirectLookup(contextFactory, distinctTexts);
        var matchedTexts = deckWords.Select(dw => dw.OriginalText).ToHashSet();

        var matched = deckWords
                      .GroupBy(dw => (dw.WordId, dw.ReadingIndex))
                      .Select(g =>
                      {
                          var first = g.First();
                          var occurrences = g.Sum(dw => textCounts.GetValueOrDefault(dw.OriginalText, 1));
                          return new ImportMatchedWord
                                 {
                                     WordId = first.WordId, ReadingIndex = first.ReadingIndex, Text = first.OriginalText,
                                     Reading = first.SudachiReading, Occurrences = occurrences
                                 };
                      }).ToList();

        var unmatched = distinctTexts.Where(t => !matchedTexts.Contains(t)).ToList();

        var previewToken = Guid.NewGuid().ToString("N");
        var db = redis.GetDatabase();
        var previewData = JsonSerializer.Serialize(matched);
        await db.StringSetAsync($"import-preview:{previewToken}", previewData, PreviewTtl);

        return new ImportPreviewResponse { Matched = matched, Unmatched = unmatched, TotalLines = totalLines, PreviewToken = previewToken };
    }

    public async Task<ImportCommitResult> CommitImport(string userId, ImportCommitRequest request)
    {
        var db = redis.GetDatabase();
        var previewData = await db.StringGetAsync($"import-preview:{request.PreviewToken}");
        if (!previewData.HasValue) return new(null, "Preview has expired. Please re-upload the file.");

        var matched = JsonSerializer.Deserialize<List<ImportMatchedWord>>(previewData!);
        if (matched == null) return new(null, "Failed to read preview data. Please re-upload the file.");

        var excludeSet = request.ExcludeWordIds?.ToHashSet() ?? new HashSet<int>();
        var wordsToImport = matched
                            .Where(m => !excludeSet.Contains(m.WordId))
                            .GroupBy(m => (m.WordId, m.ReadingIndex))
                            .Select(g => g.First())
                            .ToList();

        var userDeckIds = await userContext.UserStudyDecks
                                           .Where(sd => sd.UserId == userId)
                                           .Select(sd => sd.UserStudyDeckId)
                                           .ToListAsync();
        var limits = await userLimits.GetLimitsAsync(userId);
        if (userDeckIds.Count >= limits.StudyDecks) return new(null, LimitMessages.StudyDeckCount(limits));

        var totalUserWords = await userContext.UserStudyDeckWords
                                              .CountAsync(w => userDeckIds.Contains(w.UserStudyDeckId));
        if (totalUserWords + wordsToImport.Count > limits.StudyDeckWords)
            return new(null, LimitMessages.StudyDeckWordsTotal(limits, wordsToImport.Count));
        if (wordsToImport.Count > limits.ImportWords) return new(null, LimitMessages.ImportTooLarge(limits));

        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var maxOrder = await userContext.UserStudyDecks
                                        .Where(sd => sd.UserId == userId)
                                        .MaxAsync(sd => (int?)sd.SortOrder) ?? -1;

        var studyDeck = new UserStudyDeck
                        {
                            UserId = userId, DeckType = StudyDeckType.StaticWordList, Name = request.Name,
                            Description = request.Description, SortOrder = maxOrder + 1, Order = (int)Dtos.DeckOrder.ImportOrder,
                            CreatedAt = DateTime.UtcNow
                        };
        userContext.UserStudyDecks.Add(studyDeck);
        await userContext.SaveChangesAsync();

        for (var i = 0; i < wordsToImport.Count; i++)
        {
            var word = wordsToImport[i];
            userContext.UserStudyDeckWords.Add(new UserStudyDeckWord
                                               {
                                                   UserStudyDeckId = studyDeck.UserStudyDeckId, WordId = word.WordId,
                                                   ReadingIndex = word.ReadingIndex, SortOrder = i,
                                                   Occurrences = Math.Max(1, word.Occurrences)
                                               });
        }

        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();
        await db.KeyDeleteAsync($"import-preview:{request.PreviewToken}");

        return new(studyDeck.UserStudyDeckId, null);
    }

    public async Task<ImportCommitResult> ImportToExistingDeck(string userId, int deckId, ImportToExistingRequest request)
    {
        var db = redis.GetDatabase();
        var previewData = await db.StringGetAsync($"import-preview:{request.PreviewToken}");
        if (!previewData.HasValue) return new(null, "Preview has expired. Please try again.");

        var matched = JsonSerializer.Deserialize<List<ImportMatchedWord>>(previewData!);
        if (matched == null) return new(null, "Failed to read preview data.");

        var excludeSet = request.ExcludeWordIds?.ToHashSet() ?? new HashSet<int>();
        var wordsToImport = matched
                            .Where(m => !excludeSet.Contains(m.WordId))
                            .GroupBy(m => (m.WordId, m.ReadingIndex))
                            .Select(g => g.First())
                            .ToList();

        var userDeckIds = await userContext.UserStudyDecks
                                           .Where(sd => sd.UserId == userId)
                                           .Select(sd => sd.UserStudyDeckId)
                                           .ToListAsync();
        var limits = await userLimits.GetLimitsAsync(userId);
        var totalUserWords = await userContext.UserStudyDeckWords
                                              .CountAsync(w => userDeckIds.Contains(w.UserStudyDeckId));
        if (totalUserWords + wordsToImport.Count > limits.StudyDeckWords)
            return new(null, LimitMessages.StudyDeckWordsTotal(limits, wordsToImport.Count));
        if (wordsToImport.Count > limits.ImportWords) return new(null, LimitMessages.ImportTooLarge(limits));

        var existingWords = await userContext.UserStudyDeckWords
                                             .Where(w => w.UserStudyDeckId == deckId)
                                             .ToListAsync();
        var existingMap = existingWords.ToDictionary(w => (w.WordId, w.ReadingIndex));

        var maxSort = await userContext.UserStudyDeckWords
                                       .Where(w => w.UserStudyDeckId == deckId)
                                       .MaxAsync(w => (int?)w.SortOrder) ?? -1;

        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var added = 0;
        var seen = new HashSet<(int, short)>();
        foreach (var word in wordsToImport)
        {
            if (!seen.Add((word.WordId, word.ReadingIndex))) continue;

            if (existingMap.TryGetValue((word.WordId, word.ReadingIndex), out var existing))
            {
                existing.Occurrences += Math.Max(1, word.Occurrences);
            }
            else
            {
                userContext.UserStudyDeckWords.Add(new UserStudyDeckWord
                                                   {
                                                       UserStudyDeckId = deckId, WordId = word.WordId, ReadingIndex = word.ReadingIndex,
                                                       SortOrder = ++maxSort, Occurrences = Math.Max(1, word.Occurrences)
                                                   });
                added++;
            }
        }

        if (added > 0 || userContext.ChangeTracker.HasChanges())
            await userContext.SaveChangesAsync();
        await transaction.CommitAsync();
        await db.KeyDeleteAsync($"import-preview:{request.PreviewToken}");

        return new(deckId, null);
    }

    private static async Task<List<string>> ParseTxt(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        return content.Split('\n')
                      .Select(line => line.Trim().TrimEnd('\r'))
                      .Where(line => line.Length > 0)
                      .ToList();
    }

    private static async Task<List<string>> ParseCsv(Stream stream, char delimiter)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                     {
                         Delimiter = delimiter.ToString(), HasHeaderRecord = false, BadDataFound = null, MissingFieldFound = null
                     };
        using var csv = new CsvReader(reader, config);

        var rows = new List<string[]>();
        while (await csv.ReadAsync())
        {
            var record = csv.Parser.Record;
            if (record is { Length: > 0 })
                rows.Add(record);
        }

        if (rows.Count == 0) return new();

        var maxCols = rows.Max(r => r.Length);
        var japaneseCol = 0;
        var bestCount = 0;

        for (var col = 0; col < maxCols; col++)
        {
            var count = rows.Count(r => col < r.Length && JapaneseRegex.IsMatch(r[col]));
            if (count > bestCount)
            {
                bestCount = count;
                japaneseCol = col;
            }
        }

        var startRow = 0;
        if (rows.Count > 1
            && !JapaneseRegex.IsMatch(rows[0].ElementAtOrDefault(japaneseCol) ?? "")
            && JapaneseRegex.IsMatch(rows[1].ElementAtOrDefault(japaneseCol) ?? ""))
            startRow = 1;

        return rows.Skip(startRow)
                   .Select(cols => japaneseCol < cols.Length ? cols[japaneseCol].Trim() : "")
                   .Where(t => t.Length > 0)
                   .ToList();
    }

    [GeneratedRegex(@"[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF]")]
    private static partial Regex JapanesePattern();
}