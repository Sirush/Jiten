using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using CommandLine;
using Jiten.Cli;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WanaKanaShaapu;

// ReSharper disable MethodSupportsCancellation

public class Program
{
    private static List<string> _subtitleCleanStartsWith = new List<string>
                                                           {
                                                               "---", "本字幕由", "更多中日", "本整理", "压制", "日听",
                                                               "校对", "时轴", "台本整理", "听翻", "翻译", "ED",
                                                               "OP", "字幕", "诸神", "负责", "阿里", "日校",
                                                               "翻译", "校对", "片源", "◎", "m"
                                                           };

    private static DbContextOptions<JitenDbContext> _dbOptions;

    public class Options
    {
        [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose { get; set; }

        [Option('i', "import", Required = false, HelpText = "Import the latest JMdict file.")]
        public bool Import { get; set; }

        [Option("xml", Required = false, HelpText = "Path to the JMDict dtd_xml file.")]
        public string XmlPath { get; set; }

        [Option("dic", Required = false, HelpText = "Path to the JMdict dictionary file.")]
        public string DictionaryPath { get; set; }

        [Option("furi", Required = false, HelpText = "Path to the JMDict Furigana dictionary file.")]
        public string FuriganaPath { get; set; }

        [Option('e', "extract", Required = false, HelpText = "Extract text from a file or a folder and all its subfolders.")]
        public string ExtractFilePath { get; set; }

        [Option('p', "parse", Required = false, HelpText = "Parse text in directory using metadata.json.")]
        public string Parse { get; set; }

        [Option('t', "threads", Required = false, HelpText = "Number of threads to use.")]
        public int Threads { get; set; } = 1;

        [Option('s', "script", Required = false, HelpText = "Choose an available extraction script.")]
        public string Script { get; set; }

        [Option('o', "output", Required = false, HelpText = "Output the operation to a file.")]
        public string Output { get; set; }

        [Option('x', "extra", Required = false, HelpText = "Extra arguments for some operations.")]
        public string Extra { get; set; }

        [Option('m', "metadata", Required = false, HelpText = "Download metadata for a folder.")]
        public string Metadata { get; set; }

        [Option('a', "api", Required = false, HelpText = "API to retrieve metadata from.")]
        public string Api { get; set; }

        [Option(longName: "deck-type", Required = false, HelpText = "Type of deck for the parser.")]
        public string? DeckType { get; set; }

        [Option(longName: "clean-subtitles", Required = false, HelpText = "Clean subtitles from extra info.")]
        public bool CleanSubtitles { get; set; }

        [Option(longName: "insert", Required = false, HelpText = "Insert the parsed deck.json into the database from a directory.")]
        public string Insert { get; set; }

        [Option(longName: "compute-frequencies", Required = false, HelpText = "Compute global word frequencies")]
        public bool ComputeFrequencies { get; set; }

        [Option(longName: "compute-difficulty", Required = false, HelpText = "Compute difficulty for decks")]
        public bool ComputeDifficulty { get; set; }

        [Option(longName: "debug-deck", Required = false, HelpText = "Debug a deck by id")]
        public int? DebugDeck { get; set; }

        [Option(longName: "user-dic-mass-add", Required = false,
                HelpText = "Add a list of words to the user dictionary from a file if they're not parsed correctly")]
        public string UserDicMassAdd { get; set; }

        [Option(longName: "apply-migrations", Required = false, HelpText = "Apply migrations to the database")]
        public bool ApplyMigrations { get; set; }
        
        [Option(longName: "import-pitch-accents", Required = false, HelpText = "Import pitch accents from a yomitan dictinoary directory.")]
        public string ImportPitchAccents { get; set; }
    }

    static async Task Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddJsonFile("sharedsettings.json", optional: true, reloadOnChange: true)
                            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                            .AddEnvironmentVariables()
                            .Build();

        var connectionString = configuration.GetConnectionString("JitenDatabase");
        var optionsBuilder = new DbContextOptionsBuilder<JitenDbContext>();
        optionsBuilder.UseNpgsql(connectionString, o => { o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery); });
        _dbOptions = optionsBuilder.Options;

        await Parser.Default.ParseArguments<Options>(args)
                    .WithParsedAsync<Options>(async o =>
                    {
                        var watch = Stopwatch.StartNew();

                        if (o.Threads > 1)
                        {
                            Console.WriteLine($"Using {o.Threads} threads.");
                        }

                        if (o.Import)
                        {
                            //TODO: auto download them from the latest version

                            if (string.IsNullOrEmpty(o.XmlPath) || string.IsNullOrEmpty(o.DictionaryPath) ||
                                string.IsNullOrEmpty(o.FuriganaPath))
                            {
                                Console.WriteLine("For import, you need to specify -xml path/to/jmdict_dtd.xml, -dic path/to/jmdict and -furi path/to/JmdictFurigana.json.");
                                return;
                            }

                            Console.WriteLine("Importing JMdict...");
                            await JmDictHelper.Import(_dbOptions, o.XmlPath, o.DictionaryPath, o.FuriganaPath);
                        }

                        if (o.ExtractFilePath != null)
                        {
                            if (await Extract(o)) return;
                        }

                        if (o.Metadata != null)
                        {
                            if (string.IsNullOrEmpty(o.Api))
                            {
                                Console.WriteLine("Please specify an API to retrieve metadata from.");
                                return;
                            }

                            if (o.Api == "jimaku")
                            {
                                var range = o.Extra?.Split("-");
                                if (range is not { Length: 2 })
                                {
                                    Console.WriteLine("Please specify a range for Jimaku metadata in the form start-end.");
                                    return;
                                }

                                await JimakuDownloader.Download(o.Metadata, int.Parse(range[0]), int.Parse(range[1]));
                            }
                            else
                            {
                                await MetadataDownloader.DownloadMetadata(o.Metadata, o.Api);
                            }
                        }

                        if (o.Parse != null)
                        {
                            await Parse(o);
                        }

                        if (o.Insert != null)
                        {
                            if (await Insert(o)) return;
                        }

                        if (o.ComputeFrequencies)
                        {
                            await JitenHelper.ComputeFrequencies(_dbOptions);
                        }

                        if (o.ComputeDifficulty)
                        {
                            foreach (var mediaType in Enum.GetValues(typeof(MediaType)))
                            {
                                await JitenHelper.ComputeDifficulty(_dbOptions, o.Verbose, (MediaType)mediaType);
                            }
                        }

                        if (o.DebugDeck != null)
                        {
                            await JitenHelper.DebugDeck(_dbOptions, o.DebugDeck.Value);
                        }

                        if (o.UserDicMassAdd != null)
                        {
                            if (string.IsNullOrEmpty(o.XmlPath))
                            {
                                Console.WriteLine("You need to specify -xml path/to/user_dic.xml");
                                return;
                            }

                            Console.WriteLine("Importing words...");
                            await AddWordsToUserDictionary(o.UserDicMassAdd, o.XmlPath);
                        }

                        if (o.ApplyMigrations)
                        {
                            Console.WriteLine("Applying migrations to the database.");
                            await using var context = new JitenDbContext(_dbOptions);
                            await context.Database.MigrateAsync();
                            Console.WriteLine("Migrations applied.");
                        }

                        if (!string.IsNullOrEmpty(o.ImportPitchAccents))
                        {
                            Console.WriteLine("Importing pitch accents...");
                            await JmDictHelper.ImportPitchAccents(o.Verbose, _dbOptions, o.ImportPitchAccents);
                            Console.WriteLine("Pitch accents imported.");
                        }

                        if (o.Verbose)
                            Console.WriteLine($"Execution time: {watch.ElapsedMilliseconds} ms");
                    });
    }

    private static async Task<bool> Insert(Options options)
    {
        var directories = Directory.GetDirectories(options.Insert).ToList();
        int directoryCount = directories.Count;

        var serializerOptions = new JsonSerializerOptions
                                {
                                    WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
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

            await JitenHelper.InsertDeck(_dbOptions, deck, coverOptimized.ToByteArray());

            if (options.Verbose)
                Console.WriteLine($"Deck {deck.OriginalTitle} inserted into the database.");
        });
        return false;
    }

    private static async Task Parse(Options options)
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
                                    WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
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

        return;

        async Task<Deck?> ProcessMetadata(string directory, Metadata metadata, Deck? parentDeck, Options options, MediaType deckType,
                                          int deckOrder)
        {
            Deck deck = new();
            string filePath = metadata.FilePath;
            await using var context = new JitenDbContext(_dbOptions);

            if (!string.IsNullOrEmpty(metadata.FilePath))
            {
                if (!File.Exists(metadata.FilePath))
                {
                    filePath = Path.Combine(directory, Path.GetFileName(metadata.FilePath));
                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine($"File {filePath} not found.");
                        return null;
                    }
                }

                List<string> lines = [];
                if (Path.GetExtension(filePath)?.ToLower() == ".epub")
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
                else
                {
                    lines = (await File.ReadAllLinesAsync(filePath)).ToList();
                }

                if (options.CleanSubtitles)
                {
                    // lines in revert, remove lines that start with the clean starts, filter with regex for (.*)
                    for (int i = lines.Count - 1; i >= 0; i--)
                    {
                        lines[i] = lines[i].Trim();
                        if (_subtitleCleanStartsWith.Any(s => lines[i].StartsWith(s)))
                        {
                            lines.RemoveAt(i);
                            break;
                        }

                        lines[i] = Regex.Replace(lines[i], @"\((.*?)\)", "");

                        if (string.IsNullOrWhiteSpace(lines[i]))
                        {
                            lines.RemoveAt(i);
                        }
                    }
                }

                deck = await Jiten.Parser.Program.ParseTextToDeck(context, string.Join(Environment.NewLine, lines));
                deck.ParentDeck = parentDeck;
                deck.DeckOrder = deckOrder;
                deck.OriginalTitle = metadata.OriginalTitle;
                deck.MediaType = deckType;

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

            await deck.AddChildDeckWords(context);

            return deck;
        }
    }

    private static async Task<bool> Extract(Options o)
    {
        Stopwatch watch = new Stopwatch();
        watch.Start();
        if (o.Script == null)
        {
            Console.WriteLine("Please specify an extraction script.");
            return true;
        }

        string result = "";
        switch (o.Script)
        {
            case "epub":
                var extractor = new EbookExtractor();

                // if it's a directory or a single file
                if (Directory.Exists(o.ExtractFilePath))
                {
                    string?[] files = Directory.GetFiles(o.ExtractFilePath, "*.epub",
                                                         new EnumerationOptions()
                                                         {
                                                             IgnoreInaccessible = true, RecurseSubdirectories = true
                                                         });

                    if (o.Verbose)
                        Console.WriteLine($"Found {files.Length} files to extract.");

                    var options = new ParallelOptions() { MaxDegreeOfParallelism = o.Threads };

                    await Parallel.ForEachAsync(files, options, async (file, _) =>
                    {
                        await File.WriteAllTextAsync(file + ".extracted.txt", await ExtractEpub(file, extractor, o), _);
                        if (o.Verbose)
                        {
                            Console.WriteLine($"Progress: {Array.IndexOf(files, file) + 1}/{files.Length}, {Array.IndexOf(files, file) * 100 / files.Length}%, {watch.ElapsedMilliseconds} ms");
                        }
                    });
                }
                else
                {
                    var file = o.ExtractFilePath;
                    await File.WriteAllTextAsync(file + ".extracted.txt", await ExtractEpub(file, extractor, o));
                }

                break;

            case "krkr":
                result = await new KiriKiriExtractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;
            case "generic":
                result = await new GenericExtractor().Extract(o.ExtractFilePath, "SHIFT-JIS", o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;
            case "generic-utf8":
                result = await new GenericExtractor().Extract(o.ExtractFilePath, "UTF-8", o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;
            case "psb":
                result = await new PsbExtractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;
            case "msc":
                result = await new MscExtractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;
            case "cs2":
                result = await new Cs2Extractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "mes":
                result = await new MesExtractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "nexas":
                result = await new NexasExtractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "whale":
                result = await new WhaleExtractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "yuris":
                result = await new YuRisExtractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "utf":
                result = await new UtfExtractor().Extract(o.ExtractFilePath, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "bgi":
                if (o.Extra == null)
                {
                    Console.WriteLine("Please specify a filter file for BGI extraction with the -x option.");
                    return true;
                }

                result = await new BgiExtractor().Extract(o.ExtractFilePath, o.Extra, o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;
        }

        return false;
    }

    private static async Task<string> ExtractEpub(string? file, EbookExtractor extractor, Options o)
    {
        if (o.Verbose)
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
                ".txt" => await File.ReadAllTextAsync(file),
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

    private static async Task AddWordsToUserDictionary(string filePath, string userDicPath)
    {
        var file = filePath;
        var lines = await File.ReadAllLinesAsync(file);
        var parser = new Jiten.Parser.Parser();
        var startLine = 0;
        var batchSize = 1000;
        var xmlLines = new System.Collections.Concurrent.ConcurrentBag<string>();
        var processedCount = 0;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        var linesToProcess = lines.Skip(startLine).ToArray();
        await using var context = new JitenDbContext(_dbOptions);

        var lookups = context.Lookups.ToList();
        var words = context.JMDictWords.ToList();
        await Parallel.ForEachAsync(linesToProcess, parallelOptions, async (line, ct) =>
        {
            var parsed = await parser.Parse(line);

            if (parsed.Count <= 1) return;

            Console.WriteLine("Fail parsing " + line);
            // Create a new context for each task
            var lookup = lookups.FirstOrDefault(x => x.LookupKey == line);
            if (lookup == null)
            {
                Console.WriteLine("No lookup found for " + line);
                return;
            }

            var word = words.First(x => x.WordId == lookup.WordId);
            var kanas = word.Readings[word.ReadingTypes.IndexOf(JmDictReadingType.KanaReading)];
            var pos = word.PartsOfSpeech.Select(p => p.ToPartOfSpeech()).ToList();

            string posKanji = "NULL";
            if (pos.Contains(PartOfSpeech.Expression))
                posKanji = "表現";
            else if (pos.Contains(PartOfSpeech.Adverb))
                posKanji = "副詞";
            else if (pos.Contains(PartOfSpeech.Conjunction))
                posKanji = "接続詞";
            else if (pos.Contains(PartOfSpeech.Auxiliary))
                posKanji = "助動詞";
            else if (pos.Contains(PartOfSpeech.Pronoun))
                posKanji = "代名詞";
            else if (pos.Contains(PartOfSpeech.Noun))
                posKanji = "名詞";
            else if (pos.Contains(PartOfSpeech.Particle))
                posKanji = "助詞";
            else if (pos.Contains(PartOfSpeech.NaAdjective))
                posKanji = "形状詞";
            else if (pos.Contains(PartOfSpeech.IAdjective))
                posKanji = "形容詞";
            else if (pos.Contains(PartOfSpeech.Verb))
                posKanji = "動詞";
            else if (pos.Contains(PartOfSpeech.NominalAdjective))
                posKanji = "形動";
            else if (pos.Contains(PartOfSpeech.Interjection))
                posKanji = "感動詞";
            else if (pos.Contains(PartOfSpeech.Numeral))
                posKanji = "数詞";

            var xmlLine = $"{line},5146,5146,5000,{line},{posKanji},普通名詞,一般,*,*,*,{WanaKana.ToKatakana(kanas)},{line},*,*,*,*,*";
            xmlLines.Add(xmlLine);

            var currentCount = Interlocked.Increment(ref processedCount);
            if (currentCount % batchSize == 0)
            {
                await WriteBatchToFile();
            }

            Console.WriteLine("fixed");
        });

        await WriteBatchToFile();

        async Task WriteBatchToFile()
        {
            var linesToWrite = new List<string>();
            while (xmlLines.TryTake(out var line))
            {
                linesToWrite.Add(line);
            }

            if (linesToWrite.Count > 0)
            {
                await File.AppendAllLinesAsync(userDicPath, linesToWrite);
                Console.WriteLine($"Wrote {linesToWrite.Count} lines to file");
            }
        }
    }
}