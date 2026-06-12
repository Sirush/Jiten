using CommandLine;

namespace Jiten.Cli;

public class CliOptions
{
    [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
    public bool Verbose { get; set; }

    [Option('i', "import", Required = false, HelpText = "Import the latest JMdict file.")]
    public bool Import { get; set; }

    [Option("xml", Required = false, HelpText = "Path to the JMDict dtd_xml file.")]
    public string? XmlPath { get; set; }

    [Option("dic", Required = false, HelpText = "Path to the JMdict dictionary file.")]
    public string? DictionaryPath { get; set; }

    [Option("namedic", Required = false, HelpText = "Path to the JMNedict dictionary file.")]
    public string? NameDictionaryPath { get; set; }

    [Option("furi", Required = false, HelpText = "Path to the JMDict Furigana dictionary file.")]
    public string? FuriganaPath { get; set; }

    [Option('e', "extract", Required = false, HelpText = "Extract text from a file or a folder and all its subfolders.")]
    public string? ExtractFilePath { get; set; }

    [Option('p', "parse", Required = false, HelpText = "Parse text in directory using metadata.json.")]
    public string? Parse { get; set; }

    [Option('t', "threads", Required = false, HelpText = "Number of threads to use.")]
    public int Threads { get; set; } = 1;

    [Option('s', "script", Required = false, HelpText = "Choose an available extraction script.")]
    public string? Script { get; set; }

    [Option('o', "output", Required = false, HelpText = "Output the operation to a file.")]
    public string? Output { get; set; }

    [Option('x', "extra", Required = false, HelpText = "Extra arguments for some operations.")]
    public string? Extra { get; set; }

    [Option('m', "metadata", Required = false, HelpText = "Download metadata for a folder.")]
    public string? Metadata { get; set; }

    [Option('a', "api", Required = false, HelpText = "API to retrieve metadata from.")]
    public string? Api { get; set; }

    [Option(longName: "deck-type", Required = false, HelpText = "Type of deck for the parser.")]
    public string? DeckType { get; set; }

    [Option(longName: "clean-subtitles", Required = false, HelpText = "Clean subtitles from extra info.")]
    public bool CleanSubtitles { get; set; }

    [Option(longName: "insert", Required = false, HelpText = "Insert the parsed deck.json into the database from a directory.")]
    public string? Insert { get; set; }

    [Option(longName: "update", Required = false,
            HelpText = "Update the parsed deck.json into the database from a directory if it's more recent'.")]
    public bool UpdateDecks { get; set; }

    [Option(longName: "compute-frequencies", Required = false, HelpText = "Compute global word frequencies")]
    public bool ComputeFrequencies { get; set; }

    [Option(longName: "debug-deck", Required = false, HelpText = "Debug a deck by id")]
    public int? DebugDeck { get; set; }

    [Option(longName: "user-dic-mass-add", Required = false,
            HelpText = "Add all JMDict words that are not in the list of word of this file")]
    public string? UserDicMassAdd { get; set; }

    [Option(longName: "apply-migrations", Required = false, HelpText = "Apply migrations to the database")]
    public bool ApplyMigrations { get; set; }

    [Option(longName: "import-pitch-accents", Required = false, HelpText = "Import pitch accents from a yomitan dictinoary directory.")]
    public string? ImportPitchAccents { get; set; }

    [Option("import-vocabulary-origin", Required = false, HelpText = "Path to the VocabularyOrigin CSV file.")]
    public string? ImportVocabularyOrigin { get; set; }

    [Option(longName: "extract-features", Required = false, HelpText = "Extract features from directory for ML.")]
    public string? ExtractFeatures { get; set; }

    [Option(longName: "extract-features-db", Required = false, HelpText = "Extract features from DB for ML.")]
    public string? ExtractFeaturesDb { get; set; }

    [Option(longName: "extract-morphemes", Required = false,
            HelpText = "Extract morphemes from words in the directionary and add them to the database.")]
    public bool ExtractMorphemes { get; set; }

    [Option(longName: "register-admin", Required = false, HelpText = "Register an admin, requires --email --username --password.")]
    public bool RegisterAdmin { get; set; }

    [Option(longName: "email", Required = false, HelpText = "Email for the admin.")]
    public string? Email { get; set; }

    [Option(longName: "username", Required = false, HelpText = "Username for the admin.")]
    public string? Username { get; set; }

    [Option(longName: "password", Required = false, HelpText = "Password for the admin.")]
    public string? Password { get; set; }

    [Option(longName: "compare-jmdict", Required = false, HelpText = "Compare two JMDict XML.")]
    public bool CompareJMDict { get; set; }

    [Option(longName: "prune-sudachi", Required = false, HelpText = "Prune CSV files from sudachi directory")]
    public string? PruneSudachiCsvDirectory { get; set; }

    [Option(longName: "import-word-composition", Required = false, HelpText = "Import word composition from SudachiDict CSVs at the given directory.")]
    public string? ImportWordCompositionDirectory { get; set; }

    [Option(longName: "split-type", Required = false, Default = "CB", HelpText = "SudachiDict split type to ingest (C, B, CB). Default CB.")]
    public string SplitType { get; set; } = "CB";

    [Option(longName: "sync-jmnedict", Required = false, HelpText = "Sync missing JMNedict entries and update partial entries with missing readings/definitions.")]
    public string? SyncJMNedict { get; set; }

    [Option(longName: "sync-jmdict", Required = false, HelpText = "Sync JMDict metadata on WordForms and Definitions from XML source.")]
    public bool SyncJmDict { get; set; }

    [Option(longName: "dry-run", Required = false, HelpText = "Preview sync changes without applying them. Exports a report to --output or jmdict-sync-changes.txt.")]
    public bool DryRun { get; set; }

    [Option(longName: "import-kanjidic", Required = false, HelpText = "Import KANJIDIC2 kanji dictionary from XML file.")]
    public string? ImportKanjidic { get; set; }

    [Option(longName: "populate-word-kanji", Required = false, HelpText = "Populate WordKanji junction table after kanji import.")]
    public bool PopulateWordKanji { get; set; }

    [Option(longName: "compute-kanji-readings", Required = false, HelpText = "Pre-compute kanji reading associations for the kanji grid.")]
    public bool ComputeKanjiReadings { get; set; }

    [Option(longName: "parse-test", Required = false, HelpText = "Parse text with verbose diagnostic output. Use @filepath to read from file.")]
    public string? ParseTest { get; set; }

    [Option(longName: "parse-deck-test", Required = false, HelpText = "Parse text in deck mode (ParseTextToDeck). Use @filepath to read from file. Combine with --watch-word to focus output.")]
    public string? ParseDeckTest { get; set; }

    [Option(longName: "watch-word", Required = false, HelpText = "Surface text to focus on during --parse-deck-test (e.g. さっき). Shows deck result + standalone sentence diagnostics.")]
    public string? WatchWord { get; set; }

    [Option(longName: "parse-test-names", Required = false, Separator = ',', HelpText = "Comma-separated person names to inject as user dictionary entries during parse-test (e.g. アーリャ,菰田重徳).")]
    public IEnumerable<string>? ParseTestNames { get; set; }

    [Option(longName: "parse-test-output", Required = false, HelpText = "Output file path for parse-test diagnostics. Defaults to stdout.")]
    public string? ParseTestOutput { get; set; }

    [Option(longName: "mine-margins", Required = false, HelpText = "Mine low-margin (uncertain segmentation) spans from a text file (or literal text). Uses Sudachi lattice margins; no DB needed. Files are streamed (multi-GB ok) with progress reports and incremental snapshots to --parse-test-output (default: <input>.margins.json).")]
    public string? MineMargins { get; set; }

    [Option(longName: "margin-threshold", Required = false, Default = 5000, HelpText = "Margin upper bound for --mine-margins; tokens below this are reported (lattice cost units).")]
    public int MarginThreshold { get; set; }

    [Option(longName: "margin-min", Required = false, Default = 0, HelpText = "Margin lower bound for --mine-margins; use 1 to exclude exact ties (compound-vs-parts).")]
    public int MarginMin { get; set; }

    [Option(longName: "margin-limit", Required = false, Default = 200, HelpText = "Maximum number of findings reported by --mine-margins.")]
    public int MarginLimit { get; set; }

    [Option(longName: "audit-user-dic", Required = false,
            HelpText = "Find user dictionary entries that capture across word boundaries by diffing tokenization with/without the user dic over a text file (or literal text). Streamed; findings to --parse-test-output (default: <input>.userdic-audit.json). Reuses --margin-limit for the report size.")]
    public string? AuditUserDic { get; set; }

    [Option(longName: "user-dic-xml", Required = false, HelpText = "Path to user_dic.xml for --audit-user-dic (default: resources/user_dic.xml next to the binary).")]
    public string? UserDicXmlPath { get; set; }

    [Option(longName: "run-parser-tests", Required = false, HelpText = "Run all parser tests with diagnostics and fix suggestions.")]
    public bool RunParserTests { get; set; }

    [Option(longName: "run-form-tests", Required = false, HelpText = "Run form selection tests (WordId/ReadingIndex correctness from FormSelectionTests).")]
    public bool RunFormTests { get; set; }

    [Option(longName: "search-word", Required = false, HelpText = "Search for a word in JMDict by reading or WordId.")]
    public string? SearchWord { get; set; }

    [Option(longName: "search-lookup", Required = false, HelpText = "Search the lookups table for a text and show all matching WordIds.")]
    public string? SearchLookup { get; set; }

    [Option(longName: "deconjugate-test", Required = false, HelpText = "Show all deconjugation results for a word.")]
    public string? DeconjugateTest { get; set; }

    [Option(longName: "flush-redis", Required = false, HelpText = "Flush the Redis cache (clears all cached parser results).")]
    public bool FlushRedis { get; set; }

    [Option(longName: "create-wordset-from-pos", Required = false, HelpText = "Create a WordSet from words with specific Part of Speech.")]
    public bool CreateWordSetFromPos { get; set; }

    [Option(longName: "create-wordset-from-csv", Required = false, HelpText = "Create a WordSet from a CSV file containing WordId,ReadingIndex pairs.")]
    public bool CreateWordSetFromCsv { get; set; }

    [Option(longName: "set-slug", Required = false, HelpText = "URL-friendly identifier for the WordSet.")]
    public string? SetSlug { get; set; }

    [Option(longName: "set-name", Required = false, HelpText = "Display name for the WordSet.")]
    public string? SetName { get; set; }

    [Option(longName: "set-description", Required = false, HelpText = "Description for the WordSet.")]
    public string? SetDescription { get; set; }

    [Option(longName: "pos", Required = false, HelpText = "Part of Speech to filter words by (e.g., 'name', 'place', 'proper noun').")]
    public string? Pos { get; set; }

    [Option(longName: "csv-file", Required = false, HelpText = "Path to CSV file containing WordId,ReadingIndex pairs.")]
    public string? CsvFile { get; set; }

    [Option(longName: "sync-kana", Required = false, Default = true, HelpText = "Sync kana readings when adding kanji readings to WordSet (default: true).")]
    public bool SyncKana { get; set; }

    [Option(longName: "import-deck-difficulty", Required = false, HelpText = "Import precomputed deck difficulty from a directory of JSON files named [DeckId].json.")]
    public string? ImportDeckDifficulty { get; set; }

    [Option(longName: "export-ml-tags", Required = false, HelpText = "Export genre/tag labels and raw text for ML training to the specified output directory.")]
    public string? ExportMlTags { get; set; }

    [Option(longName: "benchmark", Required = false, HelpText = "Run benchmark on txt files in a directory.")]
    public string? Benchmark { get; set; }

    [Option(longName: "benchmark-warmup", Required = false, Default = true, HelpText = "Run a warmup parse before benchmarking (default: true).")]
    public bool BenchmarkWarmup { get; set; }

    [Option(longName: "scan-confidence", Required = false, HelpText = "Scan a corpus file for low-confidence token resolutions. Requires --input.")]
    public bool ScanConfidence { get; set; }

    [Option(longName: "audit-conjugations", Required = false,
            HelpText = "Scan a corpus file for merged/conjugated tokens whose Conjugations chain is empty (S-G invariant). Requires --input.")]
    public bool AuditConjugations { get; set; }

    [Option(longName: "snapshot-tokens", Required = false,
            HelpText = "Dump every token's (WordId,ReadingIndex,Conjugations) for a corpus to --parse-test-output, for before/after refactor diffing. Requires --input.")]
    public bool SnapshotTokens { get; set; }

    [Option(longName: "input", Required = false, HelpText = "Input corpus file path (used with --scan-confidence).")]
    public string? Input { get; set; }

    [Option(longName: "threshold", Required = false, Default = 15, HelpText = "Confidence margin threshold for --scan-confidence (default: 15).")]
    public int Threshold { get; set; } = 15;

    [Option(longName: "cleanup-ghost-cards", Required = false, HelpText = "Delete kana-form ghost cards created by kana sync that have 0 review logs.")]
    public bool CleanupGhostCards { get; set; }

    [Option(longName: "cleanup-new-cards", Required = false, HelpText = "Delete legacy FsrsCards with State=New (pre-created cards that were never reviewed).")]
    public bool CleanupNewCards { get; set; }

    [Option(longName: "extract-ruby", Required = false, HelpText = "Extract ruby/furigana from EPUBs in comma-separated directories. Produces coverage report. Use with -t for threads, -o for detailed output.")]
    public string? ExtractRuby { get; set; }

    [Option(longName: "align-ruby", Required = false, HelpText = "Align ruby JSONL sentences to parser tokens and build n-gram reading prior tables. Use with -t for threads, -o for output path.")]
    public string? AlignRuby { get; set; }

    [Option(longName: "check-ruby-readings", Required = false, HelpText = "Compare Sudachi readings against ruby annotations to measure how often Sudachi already gets the reading right. Takes a JSONL path.")]
    public string? CheckRubyReadings { get; set; }

    [Option(longName: "ruby-changed-winners", Required = false, HelpText = "Run full parser on held-out ruby JSONL and report cases where ruby priors change the form selection winner. Use -o for JSONL output.")]
    public string? RubyChangedWinners { get; set; }

    [Option(longName: "ruby-reprune", Required = false, HelpText = "Re-prune an existing ruby_reading_priors.msgpack artifact with tighter thresholds. Shows size at various thresholds. Use -o to write the pruned output.")]
    public string? RubyReprune { get; set; }

    [Option(longName: "backfill-speech-stats", Required = false, HelpText = "Backfill speech stats from subtitle files in a directory. Each subdirectory must have metadata.json. Requires --deck-type.")]
    public string? BackfillSpeechStats { get; set; }

    [Option(longName: "backfill-speech-stats-jimaku", Required = false, HelpText = "Backfill speech stats from Jimaku API. Requires --deck-type.")]
    public bool BackfillSpeechStatsJimaku { get; set; }

    [Option(longName: "backfill-vndb-anime-relations", Required = false, HelpText = "Create VN -> anime Adaptation relationships from Shared/resources/vndb_anime_mal.json, matching anime decks by MAL id. Regenerate the map with scripts/build_vndb_anime_mal.py.")]
    public bool BackfillVndbAnimeRelations { get; set; }

    [Option(longName: "resume", Required = false, HelpText = "Skip items that already have results (e.g. decks with speech stats already computed).")]
    public bool Resume { get; set; }

    [Option(longName: "compute-vectors", Required = false, HelpText = "Compute dense FastText deck vectors and store them in Postgres. Needs --ft-model or FastTextModelPath config.")]
    public bool ComputeVectors { get; set; }

    [Option(longName: "similar-to", Required = false, HelpText = "Debug: print the most similar decks to the given deck id (loads embeddings from Postgres).")]
    public int? SimilarTo { get; set; }

    [Option(longName: "similar-limit", Required = false, Default = 20, HelpText = "Number of results for --similar-to (default: 20).")]
    public int SimilarLimit { get; set; }

    [Option(longName: "ft-model", Required = false, HelpText = "Path to the fastText .bin model (e.g. cc.ja.300.bin) for building deck vectors. Falls back to FastTextModelPath in config.")]
    public string? FtModel { get; set; }

    [Option(longName: "explain", Required = false, HelpText = "With --similar-to: also print shared-vocabulary overlap per match (diagnostic for spurious similarity).")]
    public bool Explain { get; set; }

    [Option(longName: "overlap-floor", Required = false, HelpText = "Override the gated overlap floor for --explain probing (e.g. 0.05).")]
    public float? OverlapFloor { get; set; }

    [Option(longName: "min-shared", Required = false, HelpText = "Override the gated minimum shared-distinctive-word count for --explain probing.")]
    public int? MinShared { get; set; }
}
