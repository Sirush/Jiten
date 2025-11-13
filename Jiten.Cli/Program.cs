using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using CommandLine;
using Jiten.Cli;
using Jiten.Cli.ML;
using Jiten.Cli.NGrams;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.Providers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private static bool _storeRawText;

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

        [Option("namedic", Required = false, HelpText = "Path to the JMNedict dictionary file.")]
        public string NameDictionaryPath { get; set; }

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

        [Option(longName: "update", Required = false,
                HelpText = "Update the parsed deck.json into the database from a directory if it's more recent'.")]
        public bool UpdateDecks { get; set; }

        [Option(longName: "compute-frequencies", Required = false, HelpText = "Compute global word frequencies")]
        public bool ComputeFrequencies { get; set; }

        [Option(longName: "debug-deck", Required = false, HelpText = "Debug a deck by id")]
        public int? DebugDeck { get; set; }

        [Option(longName: "user-dic-mass-add", Required = false,
                HelpText = "Add all JMDict words that are not in the list of word of this file")]
        public string UserDicMassAdd { get; set; }

        [Option(longName: "apply-migrations", Required = false, HelpText = "Apply migrations to the database")]
        public bool ApplyMigrations { get; set; }

        [Option(longName: "import-pitch-accents", Required = false, HelpText = "Import pitch accents from a yomitan dictinoary directory.")]
        public string ImportPitchAccents { get; set; }

        [Option("import-vocabulary-origin", Required = false, HelpText = "Path to the VocabularyOrigin CSV file.")]
        public string ImportVocabularyOrigin { get; set; }

        [Option(longName: "extract-features", Required = false, HelpText = "Extract features from directory for ML.")]
        public string ExtractFeatures { get; set; }

        [Option(longName: "extract-features-db", Required = false, HelpText = "Extract features from DB for ML.")]
        public string ExtractFeaturesDb { get; set; }

        [Option(longName: "extract-morphemes", Required = false,
                HelpText = "Extract morphemes from words in the directionary and add them to the database.")]
        public bool ExtractMorphemes { get; set; }

        [Option(longName: "register-admin", Required = false, HelpText = "Register an admin, requires --email --username --password.")]
        public bool RegisterAdmin { get; set; }

        [Option(longName: "email", Required = false, HelpText = "Email for the admin.")]
        public string Email { get; set; }

        [Option(longName: "username", Required = false, HelpText = "Username for the admin.")]
        public string Username { get; set; }

        [Option(longName: "password", Required = false, HelpText = "Password for the admin.")]
        public string Password { get; set; }

        [Option(longName: "compare-jmdict", Required = false, HelpText = "Compare two JMDict XML.")]
        public bool CompareJMDict { get; set; }

        [Option(longName: "prune-sudachi", Required = false, HelpText = "Prune CSV files from sudachi directory")]
        public string PruneSudachiCsvDirectory { get; set; }

        [Option(longName: "generate-ngrams", Required = false, HelpText = "Generate ngrams from the database.")]
        public bool GenerateNGrams { get; set; }

        [Option(longName: "generate-bert-embeddings", Required = false,
                HelpText = "Generate bert embeddings for the ngrams from the database.")]
        public bool GenerateBertEmbdeddings { get; set; }
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
        _storeRawText = configuration.GetValue<bool>("StoreRawText");

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
                            await JmDictHelper.ImportJMNedict(_dbOptions, o.NameDictionaryPath);
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
                                if (o.Api == "anilist-manga")
                                    await MetadataDownloader.DownloadMetadata(o.Metadata, o.Api, true, "Volume");
                                else
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

                        if (o.DebugDeck != null)
                        {
                            await JitenHelper.DebugDeck(_dbOptions, o.DebugDeck.Value);
                        }

                        if (!string.IsNullOrEmpty(o.UserDicMassAdd))
                        {
                            if (string.IsNullOrEmpty(o.XmlPath))
                            {
                                Console.WriteLine("You need to specify -xml path/to/user_dic.xml");
                                return;
                            }

                            Console.WriteLine("Importing words...");
                            await AddWordsToUserDictionary(o.UserDicMassAdd, o.XmlPath);
                        }

                        if (!string.IsNullOrEmpty(o.PruneSudachiCsvDirectory))
                        {
                            Console.WriteLine("Pruning files...");
                            await PruneSudachiCsvFiles(o.PruneSudachiCsvDirectory);
                        }

                        if (o.ApplyMigrations)
                        {
                            Console.WriteLine("Applying migrations to the Jiten database.");
                            await using var context = new JitenDbContext(_dbOptions);
                            await context.Database.MigrateAsync();
                            Console.WriteLine("Migrations applied to the Jiten database.");

                            Console.WriteLine("Applying migrations to the User database.");
                            var userOptionsBuilder = new DbContextOptionsBuilder<UserDbContext>();
                            userOptionsBuilder.UseNpgsql(connectionString,
                                                         o => { o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery); });
                            _dbOptions = optionsBuilder.Options;
                            await using var userContext = new UserDbContext(userOptionsBuilder.Options);
                            await userContext.Database.MigrateAsync();
                            Console.WriteLine("Migrations applied to the User database.");
                        }

                        if (!string.IsNullOrEmpty(o.ImportPitchAccents))
                        {
                            Console.WriteLine("Importing pitch accents...");
                            await JmDictHelper.ImportPitchAccents(o.Verbose, _dbOptions, o.ImportPitchAccents);
                            Console.WriteLine("Pitch accents imported.");
                        }

                        if (!string.IsNullOrEmpty(o.ImportVocabularyOrigin))
                        {
                            Console.WriteLine("Importing vocabulary origin...");
                            await JmDictHelper.ImportVocabularyOrigin(o.Verbose, _dbOptions, o.ImportVocabularyOrigin);
                            Console.WriteLine("Vocabulary origin imported.");
                        }

                        if (!string.IsNullOrEmpty(o.ExtractFeatures))
                        {
                            Console.WriteLine("Extracting features...");
                            var featureExtractor = new FileFeatureExtractor(_dbOptions);
                            await featureExtractor.ExtractFeatures(Jiten.Parser.Parser.ParseTextToDeck, o.ExtractFeatures);
                            Console.WriteLine("All features extracted.");
                        }

                        if (!string.IsNullOrEmpty(o.ExtractFeaturesDb))
                        {
                            Console.WriteLine("Extracting features...");
                            var featureExtractor = new DbFeatureExtractor(_dbOptions);
                            await featureExtractor.ExtractFeatures(Jiten.Parser.Parser.ParseTextToDeck, o.ExtractFeaturesDb);
                            Console.WriteLine("All features extracted.");
                        }

                        if (o.ExtractMorphemes)
                        {
                            Console.WriteLine("This function is not supported at this time.");
                            // Console.WriteLine("Extracting morphemes...");
                            // await ExtractMorphemes();
                            // Console.WriteLine("All morphemes extracted.");
                        }

                        if (o.RegisterAdmin && !string.IsNullOrEmpty(o.Email) && !string.IsNullOrEmpty(o.Username) &&
                            !string.IsNullOrEmpty(o.Password))
                        {
                            await RegisterAdmin(configuration, o.Email, o.Username, o.Password);
                        }

                        if (o.CompareJMDict)
                        {
                            if (o.XmlPath == "" || o.DictionaryPath == "" || o.Extra == null)
                            {
                                Console.WriteLine("Usage : -xml dtdPath -dic oldDictionaryPath -x newDictionaryPath");
                                return;
                            }

                            await JmDictHelper.CompareJMDicts(o.XmlPath, o.DictionaryPath, o.Extra);
                        }

                        if (o.GenerateNGrams)
                        {
                            var job = new AmbiguousWordNgramJob(_dbOptions);
                            await job.ProcessAmbiguousWordsAsync(new AmbiguousWordProcessingConfig());
                        }

                        if (o.GenerateBertEmbdeddings)
                        {
                            var job = new BertEmbeddingJob(_dbOptions, new BertModelService(new DisambiguationConfig()
                                                                                             {
                                                                                                 ModelPath =
                                                                                                     "S:\\Jiten\\bert-base-japanese-v3\\model.onnx",
                                                                                                 VocabPath =
                                                                                                     "S:\\Jiten\\bert-base-japanese-v3\\vocab.txt"
                                                                                             }));
                            await job.ComputeEmbeddingsAsync(new BertEmbeddingConfig());
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

            await JitenHelper.InsertDeck(_dbOptions, deck, coverOptimized.ToByteArray(), options.UpdateDecks);

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
                        lines[i] = Regex.Replace(lines[i], @"（(.*?)）", "");

                        if (string.IsNullOrWhiteSpace(lines[i]))
                        {
                            lines.RemoveAt(i);
                        }
                    }
                }

                deck = await Jiten.Parser.Parser.ParseTextToDeck(context, string.Join(Environment.NewLine, lines), _storeRawText, true,
                                                                 deckType);
                deck.ParentDeck = parentDeck;
                deck.DeckOrder = deckOrder;
                deck.OriginalTitle = metadata.OriginalTitle;
                deck.MediaType = deckType;

                if (deckType is MediaType.Manga or MediaType.Anime or MediaType.Movie or MediaType.Drama)
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

            case "txt":
                result = await new TxtExtractor().Extract(o.ExtractFilePath, "SHIFT-JIS", o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;
            case "txt-utf8":
                result = await new TxtExtractor().Extract(o.ExtractFilePath, "UTF-8", o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "brute" or "bruteforce":
                result = await new BruteforceExtractor().Extract(o.ExtractFilePath, "SHIFT-JIS", o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "brute-utf8" or "bruteforce-utf8":
                result = await new BruteforceExtractor().Extract(o.ExtractFilePath, "UTF-8", o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "brute-utf16" or "bruteforce-utf16":
                result = await new BruteforceExtractor().Extract(o.ExtractFilePath, "UTF-16", o.Verbose);
                if (o.Output != null)
                {
                    await File.WriteAllTextAsync(o.Output, result);
                }

                break;

            case "mokuro":
                var directories = Directory.GetDirectories(o.ExtractFilePath).ToList();
                for (var i = 0; i < directories.Count; i++)
                {
                    string? directory = directories[i];

                    result = await new MokuroExtractor().Extract(directory, o.Verbose);
                    if (o.Output != null)
                    {
                        await File.WriteAllTextAsync(Path.Combine(o.Output, $"Volume {(i + 1):00}.txt"), result);
                    }
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

    private static async Task AddWordsToUserDictionary(string existingWords, string userDicPath)
    {
        // Load exclude list (one word per line)
        var excludeList = await File.ReadAllLinesAsync(existingWords);
        var excludeSet = new HashSet<string>(excludeList, StringComparer.Ordinal);

        // Load existing XML first tokens (surface before first comma)
        var existingXmlFirstTokens = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(userDicPath))
        {
            var xmlExistingLines = await File.ReadAllLinesAsync(userDicPath);
            foreach (var l in xmlExistingLines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                var surface = l.Split(',', 2)[0].Trim();
                if (!string.IsNullOrEmpty(surface))
                    existingXmlFirstTokens.Add(surface);
            }
        }

        excludeSet.UnionWith(existingXmlFirstTokens);

        await using var context = new JitenDbContext(_dbOptions);

        // Pre-load lookups and words
        var lookups = context.Lookups.AsNoTracking().ToList();
        var words = context.JMDictWords.AsNoTracking().ToList();

        // Candidate readings = all readings not in exclude list
        var allReadings = words.SelectMany(w => w.Readings).Distinct().ToList();
        var wordsToAdd = allReadings.Where(r => !excludeSet.Contains(r));

        var lookupDict = lookups
                         .GroupBy(l => l.LookupKey)
                         .ToDictionary(g => g.Key, g => g.First());

        var wordDict = words
                       .GroupBy(w => w.WordId)
                       .ToDictionary(g => g.Key, g => g.First());

        const int batchSize = 10000;
        var buffer = new List<string>(batchSize);
        int addedCount = 0;

        foreach (var reading in wordsToAdd)
        {
            // Ensure there is a lookup for this reading
            var readingInHiragana = WanaKana.ToHiragana(reading);
            if (!lookupDict.TryGetValue(readingInHiragana, out var lookup))
                continue;

            if (!wordDict.TryGetValue(lookup.WordId, out var word))
                continue;

            // Determine kana reading to convert to katakana
            var indexKana = word.ReadingTypes.IndexOf(JmDictReadingType.KanaReading);
            var kanas = indexKana >= 0 && indexKana < word.Readings.Count ? word.Readings[indexKana] : reading;

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
            {
                if (reading == "ていい" || reading == "からな")
                    continue;

                posKanji = "名詞";
            }
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
            else if (pos.Contains(PartOfSpeech.Suffix))
                posKanji = "接尾辞";
            else if (pos.Contains(PartOfSpeech.Counter))
                posKanji = "助数詞";
            else if (pos.Contains(PartOfSpeech.AdverbTo))
                posKanji = "副詞的と";
            else if (pos.Contains(PartOfSpeech.NounSuffix))
                posKanji = "名詞接尾辞";
            else if (pos.Contains(PartOfSpeech.PrenounAdjectival))
                posKanji = "連体詞";
            else if (pos.Contains(PartOfSpeech.Name))
            {
                // Remove problematic readings
                if (WanaKana.IsHiragana(reading) || reading == "イーノ" || reading == "ドーダ" || reading == "コトカ")
                    continue;
                posKanji = "名";
            }
            else if (pos.Contains(PartOfSpeech.Prefix))
                posKanji = "接頭詞";


            var xmlLine = $"{reading},5146,5146,5000,{reading},{posKanji},普通名詞,一般,*,*,*,{WanaKana.ToKatakana(kanas)},{reading},*,*,*,*,*";
            buffer.Add(xmlLine);
            // Update in-memory set to avoid duplicates within this run
            existingXmlFirstTokens.Add(reading);
            addedCount++;

            if (buffer.Count >= batchSize)
            {
                await File.AppendAllLinesAsync(userDicPath, buffer);
                Console.WriteLine($"Wrote {buffer.Count} lines to file");
                buffer.Clear();
            }
        }

        // Flush remaining lines
        if (buffer.Count > 0)
        {
            await File.AppendAllLinesAsync(userDicPath, buffer);
            Console.WriteLine($"Wrote {buffer.Count} lines to file");
            buffer.Clear();
        }

        Console.WriteLine($"Added {addedCount} entries.");
    }

    private static async Task RegisterAdmin(IConfigurationRoot config, string email, string username, string password)
    {
        var services = new ServiceCollection();
        services.AddDbContext<UserDbContext>(options => options.UseNpgsql(config.GetConnectionString("JitenDatabase"),
                                                                          o =>
                                                                          {
                                                                              o.UseQuerySplittingBehavior(QuerySplittingBehavior
                                                                                  .SplitQuery);
                                                                          }));

        services.AddLogging(configure => configure.AddConsole());

        var roleName = nameof(UserRole.Administrator);

        services.AddIdentity<User, IdentityRole>(options =>
                {
                    options.Password.RequireDigit = true;
                    options.Password.RequireLowercase = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = false;
                    options.Password.RequiredLength = 10;

                    options.User.RequireUniqueEmail = true;
                })
                .AddEntityFrameworkStores<UserDbContext>()
                .AddDefaultTokenProviders();

        var serviceProvider = services.BuildServiceProvider();

        using (var scope = serviceProvider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<UserDbContext>();

            Console.WriteLine($"Attempting to create user: {username} ({email}) with role: {roleName}");

            if (!await roleManager.RoleExistsAsync(roleName))
            {
                Console.WriteLine($"Role '{roleName}' does not exist. Creating it...");
                var roleResult = await roleManager.CreateAsync(new IdentityRole(roleName));
                if (!roleResult.Succeeded)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Failed to create role '{roleName}':");
                    foreach (var error in roleResult.Errors)
                    {
                        Console.WriteLine($"- {error.Description}");
                    }

                    Console.ResetColor();
                    return;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Role '{roleName}' created successfully.");
                Console.ResetColor();
            }

            // Check if user already exists
            var existingUserByUsername = await userManager.FindByNameAsync(username);
            if (existingUserByUsername != null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"User with username '{username}' already exists.");
                Console.ResetColor();
                return;
            }

            var existingUserByEmail = await userManager.FindByEmailAsync(email);
            if (existingUserByEmail != null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"User with email '{email}' already exists.");
                Console.ResetColor();
                return;
            }

            var user = new User() { UserName = username, Email = email, EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString() };

            var createUserResult = await userManager.CreateAsync(user, password);

            if (!createUserResult.Succeeded)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to create user '{username}':");
                foreach (var error in createUserResult.Errors)
                {
                    Console.WriteLine($"- {error.Description}");
                }

                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"User '{username}' created successfully with ID: {user.Id}");
            Console.ResetColor();

            // Add user to role
            var addToRoleResult = await userManager.AddToRoleAsync(user, roleName);
            if (!addToRoleResult.Succeeded)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to add user '{username}' to role '{roleName}':");
                foreach (var error in addToRoleResult.Errors)
                {
                    Console.WriteLine($"- {error.Description}");
                }

                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"User '{username}' successfully added to role '{roleName}'.");
            Console.ResetColor();
        }
    }


    /// <summary>
    /// TODO: fix, doesn't work correctly
    /// </summary>
    /// <returns></returns>
    private static async Task<bool> ExtractMorphemes()
    {
        var context = new JitenDbContext(_dbOptions);
        var allWords = await context.JMDictWords.Select(w => new { w.WordId, w.Readings, w.ReadingTypes }).ToListAsync();
        int error = 0;
        int noMorphemes = 0;
        int multipleMorphemes = 0;

        List<(int wordId, string reading, List<int> morphemes)> parsedMorphemes = new();
        foreach (var word in allWords)
        {
            string reading = "";
            for (int i = 0; i < word.Readings.Count; i++)
            {
                if (word.ReadingTypes[i] == JmDictReadingType.Reading)
                {
                    reading = word.Readings[i];
                    break;
                }
            }

            if (string.IsNullOrEmpty(reading))
            {
                Console.WriteLine($"Error: haven't found reading for word id {word.WordId}");
                continue;
            }

            parsedMorphemes.Add((word.WordId, reading, new List<int>()));
        }

        // Process morphemes in batches of 10,000 words to avoid memory issues
        const int BATCH_SIZE = 10000;
        int totalProcessed = 0;


        for (int batchStart = 0; batchStart < parsedMorphemes.Count; batchStart += BATCH_SIZE)
        {
            int currentBatchSize = Math.Min(BATCH_SIZE, parsedMorphemes.Count - batchStart);
            Console.WriteLine($"Processing batch {batchStart / BATCH_SIZE + 1} of {(parsedMorphemes.Count + BATCH_SIZE - 1) / BATCH_SIZE} ({batchStart}-{batchStart + currentBatchSize - 1})");


            var batchReadings = parsedMorphemes
                                .Skip(batchStart)
                                .Take(currentBatchSize)
                                .Select(p => p.reading);
            var batchText = String.Join(" \n", batchReadings);


            var results = await Jiten.Parser.Parser.ParseMorphenes(context, batchText);

            int currentMorphemeIndex = batchStart;
            bool lastWasNull = false;
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i] != null)
                {
                    parsedMorphemes[currentMorphemeIndex].morphemes.Add(results[i]!.WordId);
                    lastWasNull = false;
                }
                else
                {
                    // No word found for this morpheme, we skip to avoid running out of bounds
                    if (lastWasNull)
                        continue;

                    lastWasNull = true;
                    currentMorphemeIndex++;
                }
            }

            totalProcessed += currentBatchSize;
            Console.WriteLine($"Processed {totalProcessed} of {parsedMorphemes.Count} words ({totalProcessed * 100 / parsedMorphemes.Count}%)");
        }

        foreach (var morpheme in parsedMorphemes)
        {
            if (morpheme.morphemes.Count == 0)
            {
                Console.WriteLine($"Error: haven't found any results for reading {morpheme.reading}");
                error++;
            }
            else if (morpheme.morphemes.Count == 1)
            {
                noMorphemes++;
            }
            else
            {
                multipleMorphemes++;
            }
        }

        Console.WriteLine($"Error: {error}");
        Console.WriteLine($"No morphemes: {noMorphemes}");
        Console.WriteLine($"Multiple morphemes: {multipleMorphemes}");
        var totalMorphenes = parsedMorphemes.Select(p => p.morphemes.Count).Sum() + error + noMorphemes;
        Console.WriteLine($"Total: {totalMorphenes} for {parsedMorphemes.Count} words (Average: {totalMorphenes / parsedMorphemes.Count:0.00)}");
        return true;
    }

    private static async Task PruneSudachiCsvFiles(string folderPath)
    {
        await using var context = new JitenDbContext(_dbOptions);
        var allReadings = context.JMDictWords
                                 .SelectMany(w => w.Readings)
                                 .ToHashSet();

        Console.WriteLine($"Loaded {allReadings.Count} readings.");

        await SudachiDictionaryProcessor.PruneAndFixSudachiCsvFiles(folderPath, allReadings);

        Console.WriteLine($"--- Pruning and fixing complete. ---");
    }
}