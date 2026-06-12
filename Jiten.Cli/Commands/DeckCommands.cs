using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.Data.Providers.Jimaku;
using Jiten.Parser;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.Commands;

public class DeckCommands(CliContext context)
{
    private static readonly List<string> SubtitleCleanStartsWith =
    [
        "---", "本字幕由", "更多中日", "本整理", "压制", "日听",
        "校对", "时轴", "台本整理", "听翻", "翻译", "ED",
        "OP", "字幕", "诸神", "负责", "阿里", "日校",
        "翻译", "校对", "片源", "◎", "m"
    ];

    public async Task BackfillVndbAnimeRelations(CliOptions options)
    {
        await using var db = await context.ContextFactory.CreateDbContextAsync();

        var vnDecks = await db.Decks
            .Where(d => d.MediaType == MediaType.VisualNovel)
            .SelectMany(d => d.Links.Where(l => l.LinkType == LinkType.Vndb).Select(l => new { d.DeckId, l.Url }))
            .ToListAsync();

        Console.WriteLine($"Found {vnDecks.Count} VN decks with a VNDB link.");

        int processed = 0, matched = 0;
        foreach (var vn in vnDecks)
        {
            var url = vn.Url.TrimEnd('/');
            var vndbId = url.Substring(url.LastIndexOf('/') + 1);

            var relations = MetadataProviderHelper.GetVndbAnimeRelations(vndbId);
            if (relations.Count > 0)
            {
                await MetadataProviderHelper.ProcessRelations(db, vn.DeckId, relations);
                matched++;
                if (options.Verbose)
                    Console.WriteLine($"  {vndbId}: {relations.Count} anime MAL id(s) processed.");
            }

            processed++;
            if (processed % 500 == 0)
                Console.WriteLine($"Processed {processed}/{vnDecks.Count}...");
        }

        Console.WriteLine($"Done. Scanned {processed} VN decks; {matched} had VNDB anime adaptations in the map.");
    }

    public async Task BackfillSpeechStats(CliOptions options)
    {
        if (options.DeckType == null || !Enum.TryParse(options.DeckType, out MediaType deckType))
        {
            Console.WriteLine("Please specify a deck type with --deck-type. Available types:");
            foreach (var type in Enum.GetNames(typeof(MediaType)))
                Console.WriteLine(type);
            return;
        }

        var rootDir = options.BackfillSpeechStats!;
        if (!Directory.Exists(rootDir))
        {
            Console.WriteLine($"Directory not found: {rootDir}");
            return;
        }

        var directories = Directory.GetDirectories(rootDir);
        int updated = 0, skipped = 0;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads) };

        await Parallel.ForEachAsync(directories, parallelOptions, async (directory, _) =>
        {
            var metadataPath = Path.Combine(directory, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                if (options.Verbose)
                    Console.WriteLine($"No metadata.json in {directory}, skipping.");
                Interlocked.Increment(ref skipped);
                return;
            }

            var metadata = JsonSerializer.Deserialize<Metadata>(await File.ReadAllTextAsync(metadataPath));
            if (metadata == null)
            {
                Console.WriteLine($"Failed to deserialize metadata in {directory}, skipping.");
                Interlocked.Increment(ref skipped);
                return;
            }

            Console.WriteLine($"Processing: {metadata.OriginalTitle}");

            await using var db = await context.ContextFactory.CreateDbContextAsync();

            var parentDeck = await db.Decks
                .Include(d => d.Children).ThenInclude(c => c.RawText)
                .Include(d => d.RawText)
                .FirstOrDefaultAsync(d => d.OriginalTitle == metadata.OriginalTitle
                                          && d.MediaType == deckType
                                          && d.ParentDeckId == null);

            if (parentDeck == null)
            {
                Console.WriteLine($"  No deck found for '{metadata.OriginalTitle}', skipping.");
                Interlocked.Increment(ref skipped);
                return;
            }

            if (options.Resume)
            {
                var hasStats = parentDeck.Children.Count > 0
                    ? parentDeck.Children.All(c => c.SpeechDuration > 0)
                    : parentDeck.SpeechDuration > 0;

                if (hasStats)
                {
                    if (options.Verbose)
                        Console.WriteLine($"  Already has speech stats, skipping.");
                    Interlocked.Increment(ref skipped);
                    return;
                }
            }

            var subtitleFiles = Directory.GetFiles(directory)
                .Where(f => SubtitleExtractor.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            if (subtitleFiles.Count == 0)
            {
                if (options.Verbose)
                    Console.WriteLine($"  No subtitle files found in {directory}, skipping.");
                Interlocked.Increment(ref skipped);
                return;
            }

            var targetDecks = parentDeck.Children.Count > 0
                ? parentDeck.Children.ToList()
                : new List<Deck> { parentDeck };

            var normalizedRawTexts = targetDecks
                .Where(d => d.RawText != null)
                .ToDictionary(d => d.DeckId, d => NormalizeForComparison(d.RawText!.RawText));

            var matched = new HashSet<int>();
            int matchedCount = 0;
            var extractor = new SubtitleExtractor();

            foreach (var subtitleFile in subtitleFiles)
            {
                var subText = await extractor.Extract(subtitleFile);
                var normalizedSub = NormalizeForComparison(subText);

                Deck? matchedDeck = null;
                double bestRatio = 0;
                foreach (var target in targetDecks)
                {
                    if (matched.Contains(target.DeckId)) continue;
                    if (!normalizedRawTexts.TryGetValue(target.DeckId, out var normalizedRaw)) continue;

                    var ratio = LengthRatio(normalizedSub, normalizedRaw);
                    if (ratio > bestRatio)
                    {
                        bestRatio = ratio;
                        matchedDeck = target;
                    }
                }

                if (bestRatio < 0.90)
                    matchedDeck = null;

                if (matchedDeck == null)
                {
                    Console.WriteLine($"  No match for: {Path.GetFileName(subtitleFile)} (best ratio: {bestRatio:P1})");
                    continue;
                }

                var items = await extractor.ExtractItems(subtitleFile);
                if (items.Count == 0) continue;

                SubtitleStats stats;
                try
                {
                    stats = await Jiten.Parser.SubtitleMoraRateCalculator.ComputeAsync(items);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Sudachi error for {Path.GetFileName(subtitleFile)}: {ex.Message}");
                    continue;
                }

                matchedDeck.SpeechDuration = stats.DurationMs;
                matchedDeck.SpeechMoraCount = stats.MoraCount;
                matched.Add(matchedDeck.DeckId);
                matchedCount++;

                if (options.Verbose)
                    Console.WriteLine($"  Matched: {Path.GetFileName(subtitleFile)} → {matchedDeck.OriginalTitle} (ratio={bestRatio:P1}, duration={stats.DurationMs}ms, mora={stats.MoraCount})");
            }

            if (parentDeck.Children.Count > 0 && matched.Count > 0)
            {
                var childrenWithSpeech = parentDeck.Children.Where(c => c.SpeechDuration > 0).ToList();
                if (childrenWithSpeech.Count > 0)
                {
                    var avgSpeed = childrenWithSpeech.Average(c => c.SpeechSpeed);
                    parentDeck.SpeechDuration = childrenWithSpeech.Sum(c => c.SpeechDuration);
                    parentDeck.SpeechMoraCount = (long)(avgSpeed * (parentDeck.SpeechDuration / 60000.0));
                }
            }

            if (matchedCount > 0)
            {
                await db.SaveChangesAsync();
                Interlocked.Add(ref updated, matchedCount);
                Console.WriteLine($"  Updated {matchedCount} deck(s).");
            }
            else
            {
                Console.WriteLine($"  No matches found for any subtitle files.");
            }
        });

        Console.WriteLine($"\nDone. Updated: {updated}, Skipped directories: {skipped}");
    }

    public async Task<bool> Insert(CliOptions options)
    {
        var directories = Directory.GetDirectories(options.Insert!).ToList();
        int directoryCount = directories.Count;

        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            ReferenceHandler = ReferenceHandler.Preserve
        };

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.Threads };

        await Parallel.ForEachAsync(directories, parallelOptions, async (directory, _) =>
        {
            if (!File.Exists(Path.Combine(directory, "deck.json")))
            {
                if (options.Verbose)
                    Console.WriteLine($"No deck found in {directory}, skipping.");
                return;
            }

            if (options.Verbose)
            {
                Console.WriteLine("=========================================");
                Console.WriteLine($"Processing directory {directory} ({directories.IndexOf(directory) + 1}/{directoryCount}) [{(directories.IndexOf(directory) + 1) * 100 / directoryCount}%]");
                Console.WriteLine("=========================================");
            }

            var deck = JsonSerializer.Deserialize<Deck>(await File.ReadAllTextAsync(Path.Combine(directory, "deck.json")),
                                                        serializerOptions);
            if (deck == null) return;

            using var coverOptimized = new ImageMagick.MagickImage(Path.Combine(directory, "cover.jpg"));

            coverOptimized.Resize(400, 400);
            coverOptimized.Strip();
            coverOptimized.Quality = 85;
            coverOptimized.Format = ImageMagick.MagickFormat.Jpeg;

            await JitenHelper.InsertDeck(context.ContextFactory, deck, coverOptimized.ToByteArray(), options.UpdateDecks);

            if (options.Verbose)
                Console.WriteLine($"Deck {deck.OriginalTitle} inserted into the database.");
        });
        return false;
    }

    public async Task Parse(CliOptions options)
    {
        if (options.DeckType == null || !Enum.TryParse(options.DeckType, out MediaType deckType))
        {
            Console.WriteLine("Please specify a deck type for the parser. Available types:");
            foreach (var type in Enum.GetNames(typeof(MediaType)))
            {
                Console.WriteLine(type);
            }

            return;
        }

        if (options.Parse == null)
            return;

        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            ReferenceHandler = ReferenceHandler.Preserve
        };

        var directories = Directory.GetDirectories(options.Parse).ToList();
        int directoryCount = directories.Count;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.Threads };

        await Parallel.ForEachAsync(directories, parallelOptions, async (directory, _) =>
        {
            if (File.Exists(Path.Combine(directory, "deck.json")))
            {
                if (options.Verbose)
                    Console.WriteLine($"Deck already exists in {directory}, skipping.");
                return;
            }

            if (!File.Exists(Path.Combine(directory, "metadata.json")))
            {
                if (options.Verbose)
                    Console.WriteLine($"No metadata found in {directory}, skipping.");
                return;
            }

            if (options.Verbose)
            {
                Console.WriteLine("=========================================");
                Console.WriteLine($"Processing directory {directory} ({directories.IndexOf(directory) + 1}/{directoryCount}) [{(directories.IndexOf(directory) + 1) * 100 / directoryCount}%]");
                Console.WriteLine("=========================================");
            }

            var metadata = JsonSerializer.Deserialize<Metadata>(await File.ReadAllTextAsync(Path.Combine(directory, "metadata.json")));
            if (metadata == null) return;

            var baseDeck = await ProcessMetadata(directory, metadata, null, options, deckType, 0);
            if (baseDeck == null)
            {
                Console.WriteLine("ERROR: BASE DECK RETURNED NULL");
                return;
            }

            baseDeck.MediaType = deckType;
            baseDeck.OriginalTitle = metadata.OriginalTitle;
            baseDeck.RomajiTitle = metadata.RomajiTitle;
            baseDeck.EnglishTitle = metadata.EnglishTitle;
            baseDeck.Links = metadata.Links;
            baseDeck.CoverName = metadata.Image ?? "nocover.jpg";

            foreach (var link in baseDeck.Links)
            {
                link.Deck = baseDeck;
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "deck.json"), JsonSerializer.Serialize(baseDeck, serializerOptions));

            if (options.Verbose)
                Console.WriteLine($"Base deck {baseDeck.OriginalTitle} processed with {baseDeck.DeckWords.Count} words." +
                                  Environment.NewLine);
        });
    }

    private async Task<Deck?> ProcessMetadata(string directory, Metadata metadata, Deck? parentDeck, CliOptions options, MediaType deckType, int deckOrder)
    {
        Deck deck = new();
        string? filePath = metadata.FilePath;
        await using var context1 = await context.ContextFactory.CreateDbContextAsync();

        if (!string.IsNullOrEmpty(filePath))
        {
            if (!File.Exists(filePath))
            {
                filePath = Path.Combine(directory, Path.GetFileName(filePath));
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"File {filePath} not found.");
                    return null;
                }
            }

            List<string> lines = [];
            Jiten.Parser.SubtitleStats? subtitleStats = null;
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();

            if (extension == ".epub")
            {
                var extractor = new EbookExtractor();
                var text = await ExtractEpub(filePath, extractor, options);

                if (string.IsNullOrEmpty(text))
                {
                    Console.WriteLine("ERROR: TEXT RETURNED EMPTY");
                    return deck;
                }

                lines = text.Split(Environment.NewLine).ToList();
            }
            else if (extension != null && SubtitleExtractor.SupportedExtensions.Contains(extension))
            {
                var extractor = new SubtitleExtractor();
                var text = await extractor.Extract(filePath);
                if (!string.IsNullOrEmpty(text))
                    lines = text.Split(Environment.NewLine).ToList();

                var items = await extractor.ExtractItems(filePath);
                if (items.Count > 0)
                {
                    subtitleStats = await Jiten.Parser.SubtitleMoraRateCalculator.ComputeAsync(items);
                }
            }
            else
            {
                lines = (await File.ReadAllLinesAsync(filePath)).ToList();
            }

            if (options.CleanSubtitles)
            {
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    lines[i] = lines[i].Trim();
                    if (SubtitleCleanStartsWith.Any(s => lines[i].StartsWith(s)))
                    {
                        lines.RemoveAt(i);
                        break;
                    }

                    lines[i] = Regex.Replace(lines[i], @"\((.*?)\)", "");
                    lines[i] = Regex.Replace(lines[i], @"（(.*?)）", "");

                    if (string.IsNullOrWhiteSpace(lines[i]))
                    {
                        lines.RemoveAt(i);
                    }
                }
            }

            deck = await Jiten.Parser.Parser.ParseTextToDeck(context.ContextFactory, string.Join(Environment.NewLine, lines), context.StoreRawText, true,
                                                             deckType);
            deck.ParentDeck = parentDeck;
            deck.DeckOrder = deckOrder;
            deck.OriginalTitle = metadata.OriginalTitle;
            deck.MediaType = deckType;
            if (subtitleStats.HasValue)
            {
                deck.SpeechDuration = subtitleStats.Value.DurationMs;
                deck.SpeechMoraCount = subtitleStats.Value.MoraCount;
            }

            if (deckType is MediaType.Manga or MediaType.Anime or MediaType.Movie or MediaType.Drama or MediaType.Audio)
                deck.SentenceCount = 0;

            if (options.Verbose)
                Console.WriteLine($"Parsed {filePath} with {deck.DeckWords.Count} words.");
        }

        foreach (var child in metadata.Children)
        {
            var childDeck = await ProcessMetadata(directory, child, deck, options, deckType, ++deckOrder);
            if (childDeck == null)
            {
                Console.WriteLine("ERROR: CHILD DECK RETURNED NULL");
                return null;
            }

            deck.Children.Add(childDeck);
        }

        await deck.AddChildDeckWords(context1);

        return deck;
    }

    private async Task<string> ExtractEpub(string? file, EbookExtractor extractor, CliOptions options)
    {
        if (options.Verbose)
        {
            Console.WriteLine("=========================================");
            Console.WriteLine($"=== Processing {file} ===");
            Console.WriteLine("=========================================");
        }

        var extension = Path.GetExtension(file)?.ToLower();
        if (extension is ".epub" or ".txt")
        {
            var text = extension switch
            {
                ".epub" => await extractor.ExtractTextFromEbook(file),
                ".txt" => await File.ReadAllTextAsync(file!),
                _ => throw new NotSupportedException($"File extension {extension} not supported")
            };

            if (String.IsNullOrEmpty(text))
            {
                Console.WriteLine("ERROR: TEXT RETURNED EMPTY");
                return "";
            }

            return text;
        }

        return "";
    }

    public async Task BackfillSpeechStatsFromJimaku(CliOptions options)
    {
        if (options.DeckType == null || !Enum.TryParse(options.DeckType, out MediaType deckType))
        {
            Console.WriteLine("Please specify a deck type with --deck-type. Available types:");
            foreach (var type in Enum.GetNames(typeof(MediaType)))
                Console.WriteLine(type);
            return;
        }

        var jimakuApiKey = context.Configuration["JimakuApiKey"];
        if (string.IsNullOrEmpty(jimakuApiKey))
        {
            Console.WriteLine("JimakuApiKey not found in configuration.");
            return;
        }

        using var httpClient = new HttpClient();
        var rateLimit = new[] { DateTime.UtcNow.Ticks };

        await using var db = await context.ContextFactory.CreateDbContextAsync();

        var parentDecks = await db.Decks
            .Include(d => d.Links)
            .Include(d => d.Children).ThenInclude(c => c.RawText)
            .Include(d => d.RawText)
            .Where(d => d.ParentDeckId == null
                        && d.MediaType == deckType
                        && d.SpeechDuration == 0
                        && d.Children.All(c => c.SpeechDuration == 0))
            .ToListAsync();

        Console.WriteLine($"Found {parentDecks.Count} parent decks with no speech stats for type {deckType}.");

        int updated = 0, skipped = 0;

        foreach (var parentDeck in parentDecks)
        {
            var anilistLink = parentDeck.Links.FirstOrDefault(l => l.LinkType == LinkType.Anilist);
            var tmdbLink = parentDeck.Links.FirstOrDefault(l => l.LinkType == LinkType.Tmdb);

            int? anilistId = null;
            string? tmdbId = null;

            if (anilistLink != null)
            {
                var match = Regex.Match(anilistLink.Url, @"/anime/(\d+)");
                if (match.Success) anilistId = int.Parse(match.Groups[1].Value);
            }

            if (tmdbLink != null)
            {
                var match = Regex.Match(tmdbLink.Url, @"/(movie|tv)/(\d+)");
                if (match.Success) tmdbId = $"{match.Groups[1].Value}:{match.Groups[2].Value}";
            }

            if (anilistId == null && tmdbId == null)
            {
                if (options.Verbose)
                    Console.WriteLine($"  Skipping '{parentDeck.OriginalTitle}': no Anilist or TMDB link.");
                skipped++;
                continue;
            }

            Console.WriteLine($"Processing: {parentDeck.OriginalTitle}");

            bool isAnime = deckType == MediaType.Anime;

            await RespectRateLimit(rateLimit);
            var entries = anilistId.HasValue
                ? await MetadataProviderHelper.JimakuSearchAsync(httpClient, jimakuApiKey, anilistId: anilistId, anime: isAnime)
                : null;

            if ((entries == null || entries.Count == 0) && tmdbId != null)
            {
                await RespectRateLimit(rateLimit);
                entries = await MetadataProviderHelper.JimakuSearchAsync(httpClient, jimakuApiKey, tmdbId: tmdbId, anime: isAnime);
            }

            if (entries == null || entries.Count == 0)
            {
                Console.WriteLine($"  No Jimaku entry found.");
                skipped++;
                continue;
            }

            var jimakuEntry = entries[0];
            if (options.Verbose)
                Console.WriteLine($"  Jimaku entry: {jimakuEntry.Id} - {jimakuEntry.Name}");

            await RespectRateLimit(rateLimit);
            var files = await MetadataProviderHelper.JimakuGetFilesAsync(httpClient, jimakuApiKey, jimakuEntry.Id);
            if (files == null || files.Count == 0)
            {
                Console.WriteLine($"  No files found for Jimaku entry {jimakuEntry.Id}.");
                skipped++;
                continue;
            }

            var subtitleFiles = files
                .Where(f => SubtitleExtractor.SupportedExtensions
                    .Contains(Path.GetExtension(f.Name).ToLowerInvariant()))
                .ToList();

            if (subtitleFiles.Count == 0)
            {
                Console.WriteLine($"  No subtitle files (only archives or unsupported formats).");
                skipped++;
                continue;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "jiten-jimaku-backfill", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                var downloadTasks = subtitleFiles.Select(async file =>
                {
                    var filePath = Path.Combine(tempDir, file.Name);
                    await MetadataProviderHelper.JimakuDownloadFileAsync(httpClient, file.Url, filePath);
                    return filePath;
                });
                var downloadedPaths = (await Task.WhenAll(downloadTasks)).ToList();

                var targetDecks = parentDeck.Children.Count > 0
                    ? parentDeck.Children.ToList()
                    : new List<Deck> { parentDeck };

                var normalizedRawTexts = targetDecks
                    .Where(d => d.RawText != null)
                    .ToDictionary(d => d.DeckId, d => NormalizeForComparison(d.RawText!.RawText));

                var matched = new HashSet<int>();
                int matchedCount = 0;
                var extractor = new SubtitleExtractor();

                foreach (var subtitlePath in downloadedPaths)
                {
                    var subText = await extractor.Extract(subtitlePath);
                    var normalizedSub = NormalizeForComparison(subText);

                    Deck? matchedDeck = null;
                    double bestRatio = 0;
                    foreach (var target in targetDecks)
                    {
                        if (matched.Contains(target.DeckId)) continue;
                        if (!normalizedRawTexts.TryGetValue(target.DeckId, out var normalizedRaw)) continue;

                        var ratio = LengthRatio(normalizedSub, normalizedRaw);
                        if (ratio > bestRatio)
                        {
                            bestRatio = ratio;
                            matchedDeck = target;
                        }
                    }

                    if (bestRatio < 0.90)
                        matchedDeck = null;

                    if (matchedDeck == null)
                    {
                        if (options.Verbose)
                            Console.WriteLine($"    No match for: {Path.GetFileName(subtitlePath)} (best ratio: {bestRatio:P1})");
                        continue;
                    }

                    var items = await extractor.ExtractItems(subtitlePath);
                    if (items.Count == 0) continue;

                    var stats = await SubtitleMoraRateCalculator.ComputeAsync(items);
                    matchedDeck.SpeechDuration = stats.DurationMs;
                    matchedDeck.SpeechMoraCount = stats.MoraCount;
                    matched.Add(matchedDeck.DeckId);
                    matchedCount++;

                    if (options.Verbose)
                        Console.WriteLine($"    Matched: {Path.GetFileName(subtitlePath)} → {matchedDeck.OriginalTitle} (ratio={bestRatio:P1}, duration={stats.DurationMs}ms, mora={stats.MoraCount})");
                }

                if (parentDeck.Children.Count > 0 && matched.Count > 0)
                {
                    var childrenWithSpeech = parentDeck.Children.Where(c => c.SpeechDuration > 0).ToList();
                    if (childrenWithSpeech.Count > 0)
                    {
                        var avgSpeed = childrenWithSpeech.Average(c => c.SpeechSpeed);
                        parentDeck.SpeechDuration = childrenWithSpeech.Sum(c => c.SpeechDuration);
                        parentDeck.SpeechMoraCount = (long)(avgSpeed * (parentDeck.SpeechDuration / 60000.0));
                    }
                }

                if (matchedCount > 0)
                {
                    await db.SaveChangesAsync();
                    updated += matchedCount;
                    Console.WriteLine($"  Updated {matchedCount} deck(s).");
                }
                else
                {
                    Console.WriteLine($"  No matches found for any subtitle files.");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* cleanup best-effort */ }
            }
        }

        Console.WriteLine($"\nDone. Updated: {updated}, Skipped: {skipped}");
    }

    private static async Task RespectRateLimit(long[] nextAllowedTicks)
    {
        var now = DateTime.UtcNow.Ticks;
        if (now < nextAllowedTicks[0])
        {
            var delay = TimeSpan.FromTicks(nextAllowedTicks[0] - now);
            Console.WriteLine($"  Rate limit: waiting {delay.TotalMilliseconds:F0}ms...");
            await Task.Delay(delay);
        }
        nextAllowedTicks[0] = DateTime.UtcNow.AddMilliseconds(2500).Ticks;
    }

    private static readonly Regex HalfWidthParens = new(@"\(.*?\)");
    private static readonly Regex FullWidthParens = new(@"（.*?）");
    private static readonly Regex SquareBrackets = new(@"\[.*?\]");
    private static readonly Regex Whitespace = new(@"\s+");

    private static string NormalizeForComparison(string text)
    {
        text = HalfWidthParens.Replace(text, "");
        text = FullWidthParens.Replace(text, "");
        text = SquareBrackets.Replace(text, "");
        return Whitespace.Replace(text, "");
    }

    private static double LengthRatio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        return (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
    }
}
