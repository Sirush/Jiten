using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WanaKanaShaapu;

namespace Jiten.Core.Data.JMDict;

public static class JmDictHelper
{
    private static readonly Dictionary<string, string> _entities = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> _entitiesReverse = new Dictionary<string, string>();

    // Elements the sync parser recognises or deliberately defers; anything else surfaces in the unknown-element report.
    private static readonly HashSet<string> _knownSyncElements = new()
    {
        "entry", "ent_seq", "k_ele", "keb", "ke_inf", "ke_pri",
        "r_ele", "reb", "re_nokanji", "re_restr", "re_inf", "re_pri",
        "lsource", "info", "sense",
        "stagk", "stagr", "pos", "xref", "field", "misc", "s_inf", "dial", "gloss",
        "example", "ex_srce", "ex_text", "ex_sent" // <example> deliberately deferred (media sentences are richer)
    };
    private static readonly Dictionary<string, int> _unknownSyncElements = new();

    private static void NoteSyncElement(string name)
    {
        if (_knownSyncElements.Contains(name)) return;
        _unknownSyncElements[name] = _unknownSyncElements.GetValueOrDefault(name) + 1;
    }

    private static readonly Dictionary<string, string> _posDictionary = new()
                                                                        {
                                                                            { "bra", "Brazilian" }, { "hob", "Hokkaido-ben" },
                                                                            { "ksb", "Kansai-ben" }, { "ktb", "Kantou-ben" },
                                                                            { "kyb", "Kyoto-ben" }, { "kyu", "Kyuushuu-ben" },
                                                                            { "nab", "Nagano-ben" }, { "osb", "Osaka-ben" },
                                                                            { "rkb", "Ryuukyuu-ben" }, { "thb", "Touhoku-ben" },
                                                                            { "tsb", "Tosa-ben" }, { "tsug", "Tsugaru-ben" },
                                                                            { "agric", "agriculture" }, { "anat", "anatomy" },
                                                                            { "archeol", "archeology" }, { "archit", "architecture" },
                                                                            { "art", "art, aesthetics" }, { "astron", "astronomy" },
                                                                            { "audvid", "audiovisual" }, { "aviat", "aviation" },
                                                                            { "baseb", "baseball" }, { "biochem", "biochemistry" },
                                                                            { "biol", "biology" }, { "bot", "botany" },
                                                                            { "Buddh", "Buddhism" }, { "bus", "business" },
                                                                            { "cards", "card games" }, { "chem", "chemistry" },
                                                                            { "Christn", "Christianity" }, { "cloth", "clothing" },
                                                                            { "comp", "computing" }, { "cryst", "crystallography" },
                                                                            // Name types from JMNedict
                                                                            { "name", "name" }, { "name-fem", "female name" },
                                                                            { "name-male", "male name" }, { "name-given", "given name" },
                                                                            { "name-surname", "surname" }, { "name-place", "place name" },
                                                                            { "name-person", "person name" },
                                                                            { "name-unclass", "unclassified name" },
                                                                            { "name-station", "station name" },
                                                                            { "name-organization", "organization name" },
                                                                            { "name-company", "company name" },
                                                                            { "name-product", "product name" },
                                                                            { "name-work", "work name" }, { "dent", "dentistry" },
                                                                            { "ecol", "ecology" }, { "econ", "economics" },
                                                                            { "elec", "electricity, elec. eng." },
                                                                            { "electr", "electronics" }, { "embryo", "embryology" },
                                                                            { "engr", "engineering" }, { "ent", "entomology" },
                                                                            { "film", "film" }, { "finc", "finance" },
                                                                            { "fish", "fishing" }, { "food", "food, cooking" },
                                                                            { "gardn", "gardening, horticulture" }, { "genet", "genetics" },
                                                                            { "geogr", "geography" }, { "geol", "geology" },
                                                                            { "geom", "geometry" }, { "go", "go (game)" },
                                                                            { "golf", "golf" }, { "gramm", "grammar" },
                                                                            { "grmyth", "Greek mythology" }, { "hanaf", "hanafuda" },
                                                                            { "horse", "horse racing" }, { "kabuki", "kabuki" },
                                                                            { "law", "law" }, { "ling", "linguistics" },
                                                                            { "logic", "logic" }, { "MA", "martial arts" },
                                                                            { "mahj", "mahjong" }, { "manga", "manga" },
                                                                            { "math", "mathematics" }, { "mech", "mechanical engineering" },
                                                                            { "med", "medicine" }, { "met", "meteorology" },
                                                                            { "mil", "military" }, { "mining", "mining" },
                                                                            { "music", "music" }, { "noh", "noh" },
                                                                            { "ornith", "ornithology" }, { "paleo", "paleontology" },
                                                                            { "pathol", "pathology" }, { "pharm", "pharmacology" },
                                                                            { "phil", "philosophy" }, { "photo", "photography" },
                                                                            { "physics", "physics" }, { "physiol", "physiology" },
                                                                            { "politics", "politics" }, { "print", "printing" },
                                                                            { "psy", "psychiatry" }, { "psyanal", "psychoanalysis" },
                                                                            { "psych", "psychology" }, { "rail", "railway" },
                                                                            { "rommyth", "Roman mythology" }, { "Shinto", "Shinto" },
                                                                            { "shogi", "shogi" }, { "ski", "skiing" },
                                                                            { "sports", "sports" }, { "stat", "statistics" },
                                                                            { "stockm", "stock market" }, { "sumo", "sumo" },
                                                                            { "telec", "telecommunications" }, { "tradem", "trademark" },
                                                                            { "tv", "television" }, { "vidg", "video games" },
                                                                            { "zool", "zoology" }, { "abbr", "abbreviation" },
                                                                            { "arch", "archaic" }, { "char", "character" },
                                                                            { "chn", "children's language" }, { "col", "colloquial" },
                                                                            { "company", "company name" }, { "creat", "creature" },
                                                                            { "dated", "dated term" }, { "dei", "deity" },
                                                                            { "derog", "derogatory" }, { "doc", "document" },
                                                                            { "euph", "euphemistic" }, { "ev", "event" },
                                                                            { "fam", "familiar language" },
                                                                            { "fem", "female term or language" }, { "fict", "fiction" },
                                                                            { "form", "formal or literary term" },
                                                                            { "given", "given name or forename, gender not specified" },
                                                                            { "group", "group" }, { "hist", "historical term" },
                                                                            { "hon", "honorific or respectful (sonkeigo)" },
                                                                            { "hum", "humble (kenjougo)" },
                                                                            { "id", "idiomatic expression" },
                                                                            { "joc", "jocular, humorous term" }, { "leg", "legend" },
                                                                            { "m-sl", "manga slang" }, { "male", "male term or language" },
                                                                            { "myth", "mythology" }, { "net-sl", "Internet slang" },
                                                                            { "obj", "object" }, { "obs", "obsolete term" },
                                                                            { "on-mim", "onomatopoeic or mimetic" },
                                                                            { "organization", "organization name" }, { "oth", "other" },
                                                                            { "person", "full name of a particular person" },
                                                                            { "place", "place name" }, { "poet", "poetical term" },
                                                                            { "pol", "polite (teineigo)" }, { "product", "product name" },
                                                                            { "proverb", "proverb" }, { "quote", "quotation" },
                                                                            { "rare", "rare term" }, { "relig", "religion" },
                                                                            { "sens", "sensitive" }, { "serv", "service" },
                                                                            { "ship", "ship name" }, { "sl", "slang" },
                                                                            { "station", "railway station" },
                                                                            { "surname", "family or surname" },
                                                                            { "uk", "usually written using kana" },
                                                                            { "unclass", "unclassified name" }, { "vulg", "vulgar" },
                                                                            { "work", "work of art, literature, music, etc. name" },
                                                                            {
                                                                                "X",
                                                                                "rude or X-rated term (not displayed in educational software)"
                                                                            },
                                                                            { "yoji", "yojijukugo" },
                                                                            { "adj-f", "noun or verb acting prenominally" },
                                                                            { "adj-i", "adjective (keiyoushi)" },
                                                                            { "adj-ix", "adjective (keiyoushi) - yoi/ii class" },
                                                                            { "adj-kari", "'kari' adjective (archaic)" },
                                                                            { "adj-ku", "'ku' adjective (archaic)" },
                                                                            {
                                                                                "adj-na",
                                                                                "adjectival nouns or quasi-adjectives (keiyodoshi)"
                                                                            },
                                                                            { "adj-nari", "archaic/formal form of na-adjective" },
                                                                            {
                                                                                "adj-no",
                                                                                "nouns which may take the genitive case particle 'no'"
                                                                            },
                                                                            { "adj-pn", "pre-noun adjectival" },
                                                                            { "adj-shiku", "'shiku' adjective (archaic)" },
                                                                            { "adj-t", "'taru' adjective" }, { "adv", "adverb (fukushi)" },
                                                                            { "adv-to", "adverb taking the 'to' particle" },
                                                                            { "aux", "auxiliary" }, { "aux-adj", "auxiliary adjective" },
                                                                            { "aux-v", "auxiliary verb" }, { "conj", "conjunction" },
                                                                            { "cop", "copula" }, { "ctr", "counter" },
                                                                            { "exp", "expressions (phrases, clauses, etc.)" },
                                                                            { "int", "interjection (kandoushi)" },
                                                                            { "n", "noun" },
                                                                            { "n-adv", "adverbial noun" },
                                                                            { "n-pr", "proper noun" },
                                                                            { "n-pref", "noun, used as a prefix" },
                                                                            { "n-suf", "noun, used as a suffix" },
                                                                            { "n-t", "noun (temporal)" },
                                                                            { "num", "numeric" }, { "pn", "pronoun" }, { "pref", "prefix" },
                                                                            { "prt", "particle" }, { "suf", "suffix" },
                                                                            { "unc", "unclassified" }, { "v-unspec", "verb unspecified" },
                                                                            { "v1", "Ichidan verb" },
                                                                            { "v1-s", "Ichidan verb - kureru special class" },
                                                                            { "v2a-s", "Nidan verb with 'u' ending (archaic)" },
                                                                            {
                                                                                "v2b-k",
                                                                                "Nidan verb (upper class) with 'bu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2b-s",
                                                                                "Nidan verb (lower class) with 'bu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2d-k",
                                                                                "Nidan verb (upper class) with 'dzu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2d-s",
                                                                                "Nidan verb (lower class) with 'dzu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2g-k",
                                                                                "Nidan verb (upper class) with 'gu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2g-s",
                                                                                "Nidan verb (lower class) with 'gu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2h-k",
                                                                                "Nidan verb (upper class) with 'hu/fu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2h-s",
                                                                                "Nidan verb (lower class) with 'hu/fu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2k-k",
                                                                                "Nidan verb (upper class) with 'ku' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2k-s",
                                                                                "Nidan verb (lower class) with 'ku' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2m-k",
                                                                                "Nidan verb (upper class) with 'mu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2m-s",
                                                                                "Nidan verb (lower class) with 'mu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2n-s",
                                                                                "Nidan verb (lower class) with 'nu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2r-k",
                                                                                "Nidan verb (upper class) with 'ru' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2r-s",
                                                                                "Nidan verb (lower class) with 'ru' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2s-s",
                                                                                "Nidan verb (lower class) with 'su' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2t-k",
                                                                                "Nidan verb (upper class) with 'tsu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2t-s",
                                                                                "Nidan verb (lower class) with 'tsu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2w-s",
                                                                                "Nidan verb (lower class) with 'u' ending and 'we' conjugation (archaic)"
                                                                            },
                                                                            {
                                                                                "v2y-k",
                                                                                "Nidan verb (upper class) with 'yu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2y-s",
                                                                                "Nidan verb (lower class) with 'yu' ending (archaic)"
                                                                            },
                                                                            {
                                                                                "v2z-s",
                                                                                "Nidan verb (lower class) with 'zu' ending (archaic)"
                                                                            },
                                                                            { "v4b", "Yodan verb with 'bu' ending (archaic)" },
                                                                            { "v4g", "Yodan verb with 'gu' ending (archaic)" },
                                                                            { "v4h", "Yodan verb with 'hu/fu' ending (archaic)" },
                                                                            { "v4k", "Yodan verb with 'ku' ending (archaic)" },
                                                                            { "v4m", "Yodan verb with 'mu' ending (archaic)" },
                                                                            { "v4n", "Yodan verb with 'nu' ending (archaic)" },
                                                                            { "v4r", "Yodan verb with 'ru' ending (archaic)" },
                                                                            { "v4s", "Yodan verb with 'su' ending (archaic)" },
                                                                            { "v4t", "Yodan verb with 'tsu' ending (archaic)" },
                                                                            { "v5aru", "Godan verb - -aru special class" },
                                                                            { "v5b", "Godan verb with 'bu' ending" },
                                                                            { "v5g", "Godan verb with 'gu' ending" },
                                                                            { "v5k", "Godan verb with 'ku' ending" },
                                                                            { "v5k-s", "Godan verb - Iku/Yuku special class" },
                                                                            { "v5m", "Godan verb with 'mu' ending" },
                                                                            { "v5n", "Godan verb with 'nu' ending" },
                                                                            { "v5r", "Godan verb with 'ru' ending" },
                                                                            { "v5r-i", "Godan verb with 'ru' ending (irregular verb)" },
                                                                            { "v5s", "Godan verb with 'su' ending" },
                                                                            { "v5t", "Godan verb with 'tsu' ending" },
                                                                            { "v5u", "Godan verb with 'u' ending" },
                                                                            { "v5u-s", "Godan verb with 'u' ending (special class)" },
                                                                            {
                                                                                "v5uru", "Godan verb - Uru old class verb (old form of Eru)"
                                                                            },
                                                                            { "vi", "intransitive verb" },
                                                                            { "vk", "Kuru verb - special class" },
                                                                            { "vn", "irregular nu verb" },
                                                                            { "vr", "irregular ru verb, plain form ends with -ri" },
                                                                            { "vs", "noun or participle which takes the aux. verb suru" },
                                                                            { "vs-c", "su verb - precursor to the modern suru" },
                                                                            { "vs-i", "suru verb - included" },
                                                                            { "vs-s", "suru verb - special class" },
                                                                            { "vt", "transitive verb" },
                                                                            {
                                                                                "vz",
                                                                                "Ichidan verb - zuru verb (alternative form of -jiru verbs)"
                                                                            },
                                                                            {
                                                                                "gikun",
                                                                                "gikun (meaning as reading) or jukujikun (special kanji reading)"
                                                                            },
                                                                            { "ik", "irregular kana usage" },
                                                                            { "ok", "out-dated or obsolete kana usage" },
                                                                            { "sk", "search-only kana form" }, { "boxing", "boxing" },
                                                                            { "chmyth", "Chinese mythology" },
                                                                            { "civeng", "civil engineering" },
                                                                            { "figskt", "figure skating" }, { "internet", "Internet" },
                                                                            { "jpmyth", "Japanese mythology" }, { "min", "mineralogy" },
                                                                            { "motor", "motorsport" },
                                                                            { "prowres", "professional wrestling" }, { "surg", "surgery" },
                                                                            { "vet", "veterinary terms" },
                                                                            { "ateji", "ateji (phonetic) reading" },
                                                                            // { "ik", "word containing irregular kana usage" },
                                                                            { "iK", "word containing irregular kanji usage" },
                                                                            { "io", "irregular okurigana usage" },
                                                                            { "oK", "word containing out-dated kanji or kanji usage" },
                                                                            { "rK", "rarely used kanji form" },
                                                                            { "sK", "search-only kanji form" },
                                                                            { "rk", "rarely used kana form" },
                                                                        };

    public static async Task<List<JmDictWord>> LoadAllWords(JitenDbContext context)
    {
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var words = await context.JMDictWords
                                .AsNoTracking()
                                .Include(w => w.Forms.OrderBy(f => f.ReadingIndex))
                                .Include(w => w.Definitions)
                                .ToListAsync();
        return words;
    }

    /// <summary>
    /// Computes the set of WordIds where every English-bearing definition sense is tagged "arch".
    /// Only fetches the minimal data needed — does NOT load full definition meanings.
    /// </summary>
    public static async Task<HashSet<int>> LoadFullyArchaicWordIds(JitenDbContext context)
    {
        var archCandidateIds = await context.JMDictWords
            .AsNoTracking()
            .Where(w => w.PartsOfSpeech.Contains("arch"))
            .Select(w => w.WordId)
            .ToListAsync();

        if (archCandidateIds.Count == 0)
            return new HashSet<int>();

        var archWordsWithDefs = await context.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions)
            .Where(w => archCandidateIds.Contains(w.WordId))
            .ToListAsync();

        var result = new HashSet<int>();
        foreach (var word in archWordsWithDefs)
        {
            var englishDefs = word.Definitions.Where(d => d.EnglishMeanings.Count > 0).ToList();
            if (englishDefs.Count > 0 && englishDefs.All(d => d.PartsOfSpeech.Contains("arch")))
                result.Add(word.WordId);
        }
        return result;
    }

    /// <summary>
    /// Streams all JmDictWords (without Definitions) in ordered batches, calling the processor
    /// for each batch. Keeps peak memory proportional to batchSize rather than the full corpus.
    /// </summary>
    public static async Task StreamWordBatchesAsync(
        JitenDbContext context, int batchSize, Func<List<JmDictWord>, Task> processor)
    {
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var allIds = await context.JMDictWords
            .AsNoTracking()
            .OrderBy(w => w.WordId)
            .Select(w => w.WordId)
            .ToListAsync();

        for (int i = 0; i < allIds.Count; i += batchSize)
        {
            var batchIds = allIds.Skip(i).Take(batchSize).ToList();
            var batch = await context.JMDictWords
                .AsNoTracking()
                .Include(w => w.Forms.OrderBy(f => f.ReadingIndex))
                .Where(w => batchIds.Contains(w.WordId))
                .ToListAsync();

            await processor(batch);
        }
    }


    public static async Task<Dictionary<string, List<int>>> LoadLookupTable(JitenDbContext context)
    {
        // GROUP BY in Postgres is faster than transferring every (wordId, lookupKey) row and grouping in C#.
        // array_agg returns a native int[] which Npgsql reads via binary protocol.
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = conn.State == ConnectionState.Closed;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(
                """SELECT "LookupKey", array_agg("WordId") FROM jmdict."Lookups" GROUP BY "LookupKey" """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new Dictionary<string, List<int>>();
            while (await reader.ReadAsync())
            {
                var ids = (int[])reader.GetValue(1);
                result[reader.GetString(0)] = new List<int>(ids);
            }
            return result;
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }

    public static async Task<Dictionary<int, int>> LoadWordFrequencyRanks(JitenDbContext context)
    {
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = conn.State == ConnectionState.Closed;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(
                """SELECT "WordId", "FrequencyRank" FROM jmdict."WordFrequencies" """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new Dictionary<int, int>();
            while (await reader.ReadAsync())
                result[reader.GetInt32(0)] = reader.GetInt32(1);
            return result;
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }

    /// Word-level observed corpus frequencies (probabilities summing to ~1). Only words with data.
    public static async Task<Dictionary<int, double>> LoadWordObservedFrequencies(JitenDbContext context)
    {
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = conn.State == ConnectionState.Closed;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(
                """SELECT "WordId", "ObservedFrequency" FROM jmdict."WordFrequencies" WHERE "ObservedFrequency" > 0 """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new Dictionary<int, double>();
            while (await reader.ReadAsync())
                result[reader.GetInt32(0)] = reader.GetDouble(1);
            return result;
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }

    public static async Task<HashSet<int>> LoadNameOnlyWordIds(JitenDbContext context)
    {
        // Filter entirely in Postgres — avoids transferring all PartsOfSpeech text[] arrays to C#.
        // Mirrors PosMapper.FromJmDict: name if tag starts with "name-" or is in the explicit name list.
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = conn.State == ConnectionState.Closed;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT "WordId"
                FROM jmdict."Words"
                WHERE array_length("PartsOfSpeech", 1) > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM unnest("PartsOfSpeech") p(pos)
                      WHERE pos NOT LIKE 'name-%'
                        AND pos NOT IN (
                            'company','given','place','person','product','ship','surname',
                            'unclass','station','group','char','creat','dei','doc','ev',
                            'fem','fict','leg','masc','myth','obj','organization','oth',
                            'relig','serv','work','unc'
                        )
                  )
                """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new HashSet<int>();
            while (await reader.ReadAsync())
                result.Add(reader.GetInt32(0));
            return result;
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }

    public static async Task<HashSet<int>> LoadExpressionWordIds(JitenDbContext context)
    {
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = conn.State == ConnectionState.Closed;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT "WordId"
                FROM jmdict."Words"
                WHERE "PartsOfSpeech" && ARRAY['exp']::text[]
                """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new HashSet<int>();
            while (await reader.ReadAsync())
                result.Add(reader.GetInt32(0));
            return result;
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }

    public static async Task<List<(int WordId, string[] PartsOfSpeech, string[]? Priorities, WordOrigin Origin)>>
        LoadWordMetadataRaw(JitenDbContext context)
    {
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = conn.State == ConnectionState.Closed;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(
                """SELECT "WordId", "PartsOfSpeech", "Priorities", "Origin" FROM jmdict."Words" """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new List<(int, string[], string[]?, WordOrigin)>(220_000);
            while (await reader.ReadAsync())
            {
                var wordId = reader.GetInt32(0);
                var pos = (string[])reader.GetValue(1);
                var pri = reader.IsDBNull(2) ? null : (string[])reader.GetValue(2);
                var origin = reader.IsDBNull(3) ? WordOrigin.Unknown : (WordOrigin)reader.GetInt32(3);
                result.Add((wordId, pos, pri, origin));
            }
            return result;
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }

    public static List<string> ToHumanReadablePartsOfSpeech(this List<string> pos)
    {
        List<string> humanReadablePos = new();
        foreach (var p in pos)
        {
            humanReadablePos.Add(_posDictionary.GetValueOrDefault(p, p));
        }

        return humanReadablePos;
    }


    public static async Task<bool> Import(IDbContextFactory<JitenDbContext> contextFactory, string dtdPath, string dictionaryPath,
                                          string furiganaPath)
    {
        var wordInfos = await GetWordInfos(dtdPath, dictionaryPath);

        wordInfos.AddRange(GetCustomWords());

        var furiganas = await JsonSerializer.DeserializeAsync<List<JMDictFurigana>>(File.OpenRead(furiganaPath));
        Dictionary<string, List<JMDictFurigana>> furiganaDict = new();
        foreach (var f in furiganas!)
        {
            // Store all furiganas with the same key
            if (!furiganaDict.TryGetValue(f.Text, out var list))
            {
                list = new List<JMDictFurigana>();
                furiganaDict.Add(f.Text, list);
            }

            list.Add(f);
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        foreach (var reading in wordInfos)
        {
            List<JmDictLookup> lookups = new();
            var addedLookupKeys = new HashSet<string>();

            for (var i = 0; i < reading.Forms.Count; i++)
            {
                var form = reading.Forms[i];
                string r = form.Text;
                var lookupKey = WanaKana.ToHiragana(r.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                                                    new DefaultOptions() { ConvertLongVowelMark = false });
                var lookupKeyWithoutLongVowelMark = WanaKana.ToHiragana(r.Replace("ゎ", "わ").Replace("ヮ", "わ"));

                if (addedLookupKeys.Add(lookupKey))
                {
                    lookups.Add(new JmDictLookup { WordId = reading.WordId, LookupKey = lookupKey });
                }

                if (lookupKeyWithoutLongVowelMark != lookupKey &&
                    addedLookupKeys.Add(lookupKeyWithoutLongVowelMark))
                {
                    lookups.Add(new JmDictLookup { WordId = reading.WordId, LookupKey = lookupKeyWithoutLongVowelMark });
                }

                if (WanaKana.IsKatakana(r) && addedLookupKeys.Add(r))
                    lookups.Add(new JmDictLookup { WordId = reading.WordId, LookupKey = r });

                if (r.Length == 1 && WanaKana.IsKanji(r))
                {
                    form.RubyText = $"{r}[{reading.Forms.First(f => WanaKana.IsKana(f.Text)).Text}]";
                }
                else
                {
                    string? furiReading = null;

                    if (furiganaDict.TryGetValue(r, out var furiList) && furiList.Count > 0)
                    {
                        foreach (var furi in furiList)
                        {
                            if (reading.Forms.Any(f => f.Text == furi.Reading))
                            {
                                furiReading = furi.Parse();
                                form.RubyText = furiReading ?? r;
                                break;
                            }
                        }

                        if (furiReading == null)
                        {
                            Console.WriteLine($"No furigana found for reading {r}");
                            form.RubyText = r;
                        }
                    }
                    else
                    {
                        form.RubyText = r;
                    }
                }
            }

            reading.PartsOfSpeech = reading.Definitions.SelectMany(d => d.PartsOfSpeech).Distinct().ToList();
            reading.Lookups = lookups;
        }

        // custom priorities
        var wordInfosById = new Dictionary<int, JmDictWord>();
        int duplicateWordIdCount = 0;
        foreach (var wordInfo in wordInfos)
        {
            if (!wordInfosById.TryAdd(wordInfo.WordId, wordInfo))
                duplicateWordIdCount++;
        }

        if (duplicateWordIdCount > 0)
            Console.WriteLine($"Warning: encountered {duplicateWordIdCount} duplicate WordIds while importing JMDict.");

        int[] jitenPriorityIds =
        [
            1332650, 2848543, 1160790, 1203260, 1397260, 1499720, 1315130, 1550190,
            1191730, 2844190, 2207630, 1442490, 1423310, 1502390, 1343100, 1610040,
            2059630, 1495580, 1288850, 1392580, 1511350, 1648450, 1534790, 2105530,
            1223615, 1421850, 1020650, 1310640, 1495770, 1375610, 1334590,
            1609980, 1579260, 1351580, 1983760, 1207510, 1266890,
            1163940, 1625330, 1416220, 1356690, 2020520, 2084840, 2603500,
            1522150, 1591970, 1920245, 1177490, 1582430, 1310670, 1577120, 1352570,
            1604800, 1581310, 2720360, 1318950, 2541230, 1288500, 1121740, 1074630,
            1111330, 1116190, 2815290, 1157170, 2855934, 1245290, 1075810, 1314600,
            1020910, 1430230, 1349380, 1347580, 1311110, 1154770, 1282790, 1478060,
            2068450, 1169250, 1598460, 1144510, 1282970, 1982860, 1609715
        ];

        foreach (var id in jitenPriorityIds)
        {
            if (!wordInfosById.TryGetValue(id, out var wordInfo))
            {
                Console.WriteLine($"Warning: custom priority WordId {id} not found in import set.");
                continue;
            }

            wordInfo.Priorities ??= new List<string>();
            if (!wordInfo.Priorities.Contains("jiten"))
                wordInfo.Priorities.Add("jiten");
        }

        if (wordInfosById.TryGetValue(2029110, out var indicatesNaAdj))
            indicatesNaAdj.Definitions.Add(new JmDictDefinition { PartsOfSpeech = ["prt"], EnglishMeanings = ["indicates na-adjective"] });
        else
            Console.WriteLine("Warning: custom definition WordId 2029110 not found in import set.");

        if (wordInfosById.TryGetValue(1524610, out var asNoun))
        {
            if (!asNoun.PartsOfSpeech.Contains("n"))
                asNoun.PartsOfSpeech.Add("n");
        }
        else
        {
            Console.WriteLine("Warning: custom POS WordId 1524610 not found in import set.");
        }

        context.JMDictWords.AddRange(wordInfos);

        await context.SaveChangesAsync();

        return true;
    }

    public static async Task<bool> ImportJMNedict(IDbContextFactory<JitenDbContext> contextFactory, string jmneDictPath)
    {
        Console.WriteLine("Starting JMNedict import...");

        var readerSettings = new XmlReaderSettings() { Async = true, DtdProcessing = DtdProcessing.Parse, MaxCharactersFromEntities = 0 };
        XmlReader reader = XmlReader.Create(jmneDictPath, readerSettings);

        await reader.MoveToContentAsync();

        // Dictionary to store entries by kanji element (keb) to combine entries with the same kanji
        Dictionary<string, JmDictWord> namesByKeb = new();

        await using var context = await contextFactory.CreateDbContextAsync();

        // Load existing entries from JMDict to check for duplicate WordIds
        Console.WriteLine("Loading existing JMDict entries to check for duplicate WordIds...");
        var existingEntries = await LoadAllWords(context);
        var existingWordIds = new HashSet<int>(existingEntries.Select(e => e.WordId));
        Console.WriteLine($"Loaded {existingEntries.Count} existing entries with {existingWordIds.Count} unique WordIds");

        // Tracking statistics
        int totalEntriesParsed = 0;
        int skippedDuplicateWordId = 0;
        int skippedEmptyReadings = 0;

        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name != "entry") continue;

            var nameEntry = new JmDictWord();
            string? primaryKeb = null;

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "ent_seq")
                        nameEntry.WordId = reader.ReadElementContentAsInt();

                    // Parse kanji elements (k_ele)
                    if (reader.Name == "k_ele")
                    {
                        await ParseNameKEle(reader, nameEntry);
                        // Save the first kanji element as the primary key for grouping
                        if (primaryKeb == null && nameEntry.Forms.Count > 0)
                        {
                            primaryKeb = nameEntry.Forms[0].Text;
                        }
                    }

                    // Parse reading elements (r_ele)
                    if (reader.Name == "r_ele")
                    {
                        await ParseNameREle(reader, nameEntry);
                    }

                    // Parse translation elements (trans)
                    if (reader.Name == "trans")
                    {
                        await ParseNameTrans(reader, nameEntry);
                    }
                }

                if (reader.NodeType != XmlNodeType.EndElement) continue;
                if (reader.Name != "entry") continue;

                totalEntriesParsed++;

                foreach (var form in nameEntry.Forms)
                    form.Text = form.Text.Replace("ゎ", "わ").Replace("ヮ", "わ");

                // Check if this entry's WordId already exists in JMDict (true duplicate)
                if (existingWordIds.Contains(nameEntry.WordId))
                {
                    // Skip this entry as the WordId already exists
                    skippedDuplicateWordId++;
                    break;
                }

                // Check if entry has no readings (would be invalid)
                if (nameEntry.Forms.Count == 0)
                {
                    skippedEmptyReadings++;
                    break;
                }

                // If we have a primary kanji, check if we need to merge with an existing entry
                if (primaryKeb != null && nameEntry.Forms.Count > 0)
                {
                    if (namesByKeb.TryGetValue(primaryKeb, out var existingEntry))
                    {
                        // Merge this entry with the existing one
                        MergeNameEntries(existingEntry, nameEntry);
                    }
                    else
                    {
                        // Add as a new entry
                        namesByKeb[primaryKeb] = nameEntry;
                    }
                }
                else if (nameEntry.Forms.Count > 0)
                {
                    // If no kanji but has readings, use the first reading as key
                    string readingKey = nameEntry.Forms[0].Text;
                    if (namesByKeb.TryGetValue(readingKey, out var existingEntry))
                    {
                        MergeNameEntries(existingEntry, nameEntry);
                    }
                    else
                    {
                        namesByKeb[readingKey] = nameEntry;
                    }
                }

                break;
            }
        }

        reader.Close();

        Console.WriteLine($"\n=== JMNedict Import Statistics ===");
        Console.WriteLine($"Total entries parsed from XML: {totalEntriesParsed}");
        Console.WriteLine($"Entries skipped (duplicate WordId): {skippedDuplicateWordId}");
        Console.WriteLine($"Entries skipped (empty readings): {skippedEmptyReadings}");
        Console.WriteLine($"Unique name entries after merging: {namesByKeb.Count}");

        // Process the merged name entries
        List<JmDictWord> nameWords = namesByKeb.Values.ToList();
        foreach (var nameWord in nameWords)
        {
            // Create lookups for searching
            List<JmDictLookup> lookups = new();
            var addedLookupKeys = new HashSet<string>();

            for (var i = 0; i < nameWord.Forms.Count; i++)
            {
                var form = nameWord.Forms[i];
                string r = form.Text;
                var lookupKey = WanaKana.ToHiragana(r.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                                                    new DefaultOptions() { ConvertLongVowelMark = false });
                var lookupKeyWithoutLongVowelMark = WanaKana.ToHiragana(r.Replace("ゎ", "わ").Replace("ヮ", "わ"));

                if (addedLookupKeys.Add(lookupKey))
                {
                    lookups.Add(new JmDictLookup { WordId = nameWord.WordId, LookupKey = lookupKey });
                }

                if (lookupKeyWithoutLongVowelMark != lookupKey &&
                    addedLookupKeys.Add(lookupKeyWithoutLongVowelMark))
                {
                    lookups.Add(new JmDictLookup { WordId = nameWord.WordId, LookupKey = lookupKeyWithoutLongVowelMark });
                }

                if (WanaKana.IsKatakana(r) && addedLookupKeys.Add(r))
                    lookups.Add(new JmDictLookup { WordId = nameWord.WordId, LookupKey = r });

                if (r.Length == 1 && WanaKana.IsKanji(r))
                {
                    var kanaForm = nameWord.Forms.FirstOrDefault(f => WanaKana.IsKana(f.Text));
                    form.RubyText = kanaForm != null ? $"{r}[{kanaForm.Text}]" : r;
                }
                else
                {
                    form.RubyText = r;
                }
            }

            // Set parts of speech from definitions (name types)
            nameWord.PartsOfSpeech = nameWord.Definitions.SelectMany(d => d.PartsOfSpeech).Distinct().ToList();
            nameWord.Lookups = lookups;

            // Add "name" priority to indicate it's from JMNedict
            if (nameWord.Priorities == null)
                nameWord.Priorities = new List<string>();
            nameWord.Priorities.Add("name");
        }

        var nameWordsById = nameWords.ToDictionary(w => w.WordId);

        if (nameWordsById.TryGetValue(5060001, out var customPriority))
        {
            customPriority.Priorities ??= new List<string>();
            if (!customPriority.Priorities.Contains("jiten"))
                customPriority.Priorities.Add("jiten");
        }
        else
        {
            Console.WriteLine("Warning: custom priority WordId 5060001 not found in JMNedict import set.");
        }

        if (nameWordsById.TryGetValue(5141615, out var stationStreet))
        {
            if (!stationStreet.PartsOfSpeech.Contains("n"))
                stationStreet.PartsOfSpeech.Add("n");

            stationStreet.Definitions.Add(new JmDictDefinition { PartsOfSpeech = ["n"], EnglishMeanings = ["street in front of station"] });
        }
        else
        {
            Console.WriteLine("Warning: custom definition WordId 5141615 not found in JMNedict import set.");
        }

        // Validate entries before database insertion
        int beforeValidation = nameWords.Count;
        nameWords = nameWords.Where(w =>
            w.Forms.Count > 0 &&
            w.Definitions.Count > 0
        ).ToList();
        int invalidEntries = beforeValidation - nameWords.Count;

        if (invalidEntries > 0)
        {
            Console.WriteLine($"Filtered out {invalidEntries} invalid entries (empty readings or definitions)");
        }

        Console.WriteLine($"Final entries to be inserted: {nameWords.Count}");

        if (nameWords.Count > 0)
        {
            try
            {
                // Add the processed name entries to the database
                context.JMDictWords.AddRange(nameWords);
                await context.SaveChangesAsync();

                Console.WriteLine($"✓ Successfully added {nameWords.Count} name entries to the database");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error saving to database: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return false;
            }
        }
        else
        {
            Console.WriteLine("No new name entries to add to the database");
        }

        Console.WriteLine($"=================================\n");
        return true;
    }

    /// <summary>
    /// Delta-syncs JMnedict (proper names, WordId range 5,000,000–7,999,999) against the database.
    /// Unlike the old additive sync, this rebuilds each merged name group's non-custom content from the
    /// XML on every run (delete-recreate semantics mirroring <see cref="SyncExistingWord"/>), runs a
    /// name-range soft-delete pass for retired names, and stamps a version sentinel (WordId 9999990).
    /// Custom data is preserved: definitions with SenseIndex &gt;= 1000 and the "jiten" priority survive.
    /// Forms are matched by (FormType, Text) and deactivated (never hard-deleted) so DeckWords that
    /// reference (WordId, ReadingIndex) keep a stable index. The pass touches ONLY 5,000,000–7,999,999
    /// rows tagged "name"; JMdict vocabulary (&lt; 5,000,000) and custom words (&gt;= 8,000,000) are
    /// never read or modified.
    /// </summary>
    public static async Task<bool> SyncMissingJMNedict(IDbContextFactory<JitenDbContext> contextFactory, string dtdPath,
                                                       string jmneDictPath, bool dryRun = false, string? reportPath = null)
    {
        const int NameRangeStart = 5000000;
        const int NameRangeEnd = 8000000;   // exclusive upper bound of the JMnedict WordId range
        const int NameSentinelId = 9999990; // the file's own version-marker entry (custom range)

        Console.WriteLine(dryRun ? "Starting JMNedict sync (DRY RUN — no writes)..." : "Starting JMNedict sync...");

        await LoadEntities(dtdPath, jmneDictPath);

        var readerSettings = new XmlReaderSettings() { Async = true, DtdProcessing = DtdProcessing.Parse, MaxCharactersFromEntities = 0 };
        XmlReader reader = XmlReader.Create(jmneDictPath, readerSettings);
        await reader.MoveToContentAsync();

        int totalEntriesParsed = 0;
        string? nameVersionDate = null;
        string? nameVersionGloss = null;

        // PHASE 1: parse XML and merge entries by keb / first-reading (one JmDictWord per surface group)
        Dictionary<string, JmDictWord> mergedEntriesByKeb = new();
        Dictionary<string, List<int>> kanjiToWordIds = new(); // every member ent_seq per surface group

        Console.WriteLine("Parsing JMnedict XML file and merging entries by kanji...");

        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name != "entry") continue;

            var nameEntry = new JmDictWord();
            string? primaryKeb = null;

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "ent_seq")
                        nameEntry.WordId = reader.ReadElementContentAsInt();

                    // Parse kanji elements (k_ele)
                    if (reader.Name == "k_ele")
                    {
                        await ParseNameKEle(reader, nameEntry);
                        // Save the first kanji element as the primary key for grouping
                        if (primaryKeb == null && nameEntry.Forms.Count > 0)
                        {
                            primaryKeb = nameEntry.Forms[0].Text;
                        }
                    }

                    // Parse reading elements (r_ele)
                    if (reader.Name == "r_ele")
                    {
                        await ParseNameREle(reader, nameEntry);
                    }

                    // Parse translation elements (trans)
                    if (reader.Name == "trans")
                    {
                        await ParseNameTrans(reader, nameEntry);
                    }
                }

                if (reader.NodeType != XmlNodeType.EndElement) continue;
                if (reader.Name != "entry") continue;

                totalEntriesParsed++;

                foreach (var form in nameEntry.Forms)
                    form.Text = form.Text.Replace("ゎ", "わ").Replace("ヮ", "わ");

                // The file's own version-marker entry (9999990) and anything in the custom range
                // (>= 8000000) are not real names — capture the date, then keep them out of the merge.
                if (nameEntry.WordId >= NameRangeEnd)
                {
                    if (nameEntry.WordId == NameSentinelId)
                    {
                        var meanings = nameEntry.Definitions.SelectMany(d => d.EnglishMeanings).ToList();
                        // Keep the file's real gloss verbatim (e.g. "Japanese-Multilingual Named Entity
                        // Dictionary Project - Creation Date: …"), mirroring the JMdict 9999999 sentinel.
                        nameVersionGloss = meanings.FirstOrDefault();
                        var dateMatch = meanings
                            .Select(m => Regex.Match(m, @"Creation Date:\s*(\d{4}-\d{2}-\d{2})"))
                            .FirstOrDefault(m => m.Success);
                        if (dateMatch != null)
                            nameVersionDate = dateMatch.Groups[1].Value;
                    }
                    break;
                }

                // Skip if entry has no readings (invalid)
                if (nameEntry.Forms.Count == 0)
                {
                    break;
                }

                // Merge entries by kanji (same logic as ImportJMNedict, but track WordIds)
                if (primaryKeb != null && nameEntry.Forms.Count > 0)
                {
                    // Track this WordId for this kanji
                    if (!kanjiToWordIds.ContainsKey(primaryKeb))
                        kanjiToWordIds[primaryKeb] = new List<int>();
                    kanjiToWordIds[primaryKeb].Add(nameEntry.WordId);

                    if (mergedEntriesByKeb.TryGetValue(primaryKeb, out var existingEntry))
                    {
                        // Merge this entry with the existing one
                        MergeNameEntries(existingEntry, nameEntry);
                    }
                    else
                    {
                        // Add as a new entry
                        mergedEntriesByKeb[primaryKeb] = nameEntry;
                    }
                }
                else if (nameEntry.Forms.Count > 0)
                {
                    // If no kanji but has readings, use the first reading as key
                    string readingKey = nameEntry.Forms[0].Text;

                    if (!kanjiToWordIds.ContainsKey(readingKey))
                        kanjiToWordIds[readingKey] = new List<int>();
                    kanjiToWordIds[readingKey].Add(nameEntry.WordId);

                    if (mergedEntriesByKeb.TryGetValue(readingKey, out var existingEntry))
                    {
                        MergeNameEntries(existingEntry, nameEntry);
                    }
                    else
                    {
                        mergedEntriesByKeb[readingKey] = nameEntry;
                    }
                }

                break;
            }
        }

        reader.Close();

        Console.WriteLine($"Parsed {totalEntriesParsed} XML entries, merged into {mergedEntriesByKeb.Count} unique name groups");
        if (nameVersionDate != null)
            Console.WriteLine($"JMnedict creation date (from WordId {NameSentinelId}): {nameVersionDate}");

        // PHASE 2: assign each merged group a canonical WordId, then rebuild against the database.
        // Canonical choice (Step 0 decision — keep existing pins, determinism prospective only):
        //   - if any member ent_seq already exists as a name row, reuse it (its pin → zero churn);
        //   - otherwise the group is new → canonical = min(member ent_seq) (deterministic across runs).
        Console.WriteLine("Resolving canonical WordIds for each name group...");

        HashSet<int> existingNameIds;
        await using (var idContext = await contextFactory.CreateDbContextAsync())
        {
            existingNameIds = (await idContext.JMDictWords
                    .AsNoTracking()
                    .Where(w => w.WordId >= NameRangeStart && w.WordId < NameRangeEnd)
                    .Select(w => w.WordId)
                    .ToListAsync())
                .ToHashSet();
        }
        Console.WriteLine($"Found {existingNameIds.Count} existing name rows in the database.");

        var groupByCanonical = new Dictionary<int, JmDictWord>();
        var liveWordIds = new HashSet<int>();
        foreach (var kvp in mergedEntriesByKeb)
        {
            var members = kanjiToWordIds[kvp.Key];
            var membersInDb = members.Where(existingNameIds.Contains).ToList();
            int canonical = membersInDb.Count > 0 ? membersInDb.Min() : members.Min();
            // If two historical groups now collapse onto one canonical, the first wins; the loser drops
            // out of liveWordIds and is soft-deleted below.
            if (groupByCanonical.TryAdd(canonical, kvp.Value))
                liveWordIds.Add(canonical);
        }

        var existingCanonicalIds = groupByCanonical.Keys.Where(existingNameIds.Contains).ToList();
        var newCanonicalIds = groupByCanonical.Keys.Where(id => !existingNameIds.Contains(id)).ToList();
        Console.WriteLine($"  {existingCanonicalIds.Count} groups map to existing rows, {newCanonicalIds.Count} are new.");

        // Range-safety backstop: every canonical must sit inside the JMnedict range (guaranteed by
        // construction — members are 5M-range ent_seqs and >= 8M entries were skipped during parse).
        var outOfRange = groupByCanonical.Keys.Where(id => id < NameRangeStart || id >= NameRangeEnd).ToList();
        if (outOfRange.Count > 0)
        {
            Console.WriteLine($"✗ ABORT: {outOfRange.Count} canonical WordIds fall outside " +
                              $"[{NameRangeStart}, {NameRangeEnd}). First: {outOfRange[0]}.");
            return false;
        }

        int wordsRebuilt = 0, wordsCreated = 0, wordsDeactivated = 0, wordsFailed = 0;
        int formsMatched = 0, formsCreated = 0, formsDeactivated = 0;
        int defsDeleted = 0, defsCreated = 0;

        var newWordEntries = dryRun ? new List<string>() : null;
        var updatedWordEntries = dryRun ? new List<string>() : null;
        var deactivatedWordEntries = dryRun ? new List<string>() : null;

        // Reset the Definitions identity sequence so the delete-recreate can't collide on DefinitionId.
        if (!dryRun)
        {
            await using var seqContext = await contextFactory.CreateDbContextAsync();
            await seqContext.Database.ExecuteSqlRawAsync(
                """SELECT setval(pg_get_serial_sequence('jmdict."Definitions"', 'DefinitionId'), GREATEST((SELECT MAX("DefinitionId") FROM jmdict."Definitions"), 1))""");
        }

        const int batchSize = 5000;

        // --- Rebuild existing name groups in batches (delete-recreate of non-custom content) ---
        for (int batchStart = 0; batchStart < existingCanonicalIds.Count; batchStart += batchSize)
        {
            var batchIds = existingCanonicalIds.Skip(batchStart).Take(batchSize).ToList();

            await using var context = await contextFactory.CreateDbContextAsync();
            var dbWords = await context.JMDictWords
                .Include(w => w.Forms)
                .Include(w => w.Definitions)
                .Include(w => w.Lookups)
                .Where(w => batchIds.Contains(w.WordId))
                .ToListAsync();
            var dbWordDict = dbWords.ToDictionary(w => w.WordId);

            foreach (var canonical in batchIds)
            {
                if (!dbWordDict.TryGetValue(canonical, out var dbWord)) { wordsFailed++; continue; }
                var merged = groupByCanonical[canonical];
                try
                {
                    HashSet<string>? oldActiveForms = null;
                    HashSet<string>? oldDefFps = null;
                    if (dryRun)
                    {
                        oldActiveForms = dbWord.Forms.Where(f => f.IsActiveInLatestSource).Select(f => f.Text).ToHashSet();
                        oldDefFps = dbWord.Definitions.Where(d => d.SenseIndex < 1000).Select(NameDefFingerprint).ToHashSet();
                    }

                    var r = RebuildExistingNameWord(context, dbWord, merged);
                    formsMatched += r.FormsMatched;
                    formsCreated += r.FormsCreated;
                    formsDeactivated += r.FormsDeactivated;
                    defsDeleted += r.DefsDeleted;
                    defsCreated += r.DefsCreated;
                    wordsRebuilt++;

                    if (dryRun)
                    {
                        var changes = new List<string>();
                        var added = dbWord.Forms.Where(f => f.IsActiveInLatestSource && !oldActiveForms!.Contains(f.Text)).Select(f => f.Text).ToList();
                        if (added.Count > 0) changes.Add($"  + Forms added: {string.Join(", ", added)}");
                        var removed = dbWord.Forms.Where(f => !f.IsActiveInLatestSource && oldActiveForms!.Contains(f.Text)).Select(f => f.Text).ToList();
                        if (removed.Count > 0) changes.Add($"  - Forms deactivated: {string.Join(", ", removed)}");
                        var newDefFps = dbWord.Definitions.Where(d => d.SenseIndex < 1000).Select(NameDefFingerprint).ToHashSet();
                        if (!oldDefFps!.SetEquals(newDefFps)) changes.Add($"  ~ Definitions/name-types changed ({oldDefFps.Count} -> {newDefFps.Count} senses)");

                        if (changes.Count > 0)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine($"WordId {canonical} -- {dbWord.Forms.FirstOrDefault()?.Text ?? "?"}");
                            foreach (var c in changes) sb.AppendLine(c);
                            updatedWordEntries!.Add(sb.ToString());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error rebuilding name WordId {canonical}: {ex.Message}");
                    wordsFailed++;
                }
            }

            if (!dryRun) await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Console.WriteLine($"  Rebuilt {Math.Min(batchStart + batchSize, existingCanonicalIds.Count)}/{existingCanonicalIds.Count} existing name groups...");
        }

        // --- Insert new name groups in batches ---
        for (int batchStart = 0; batchStart < newCanonicalIds.Count; batchStart += batchSize)
        {
            var batchIds = newCanonicalIds.Skip(batchStart).Take(batchSize).ToList();
            await using var context = await contextFactory.CreateDbContextAsync();

            if (!dryRun)
            {
                // Clear any orphaned lookups for these ids to avoid PK conflicts (mirror JMdict sync).
                var orphaned = await context.Set<JmDictLookup>().Where(l => batchIds.Contains(l.WordId)).ToListAsync();
                if (orphaned.Count > 0) context.Set<JmDictLookup>().RemoveRange(orphaned);
            }

            var toAdd = new List<JmDictWord>();
            foreach (var canonical in batchIds)
            {
                try
                {
                    var word = BuildNewNameWord(canonical, groupByCanonical[canonical]);
                    if (word.Forms.Count == 0 || word.Definitions.Count == 0) { wordsFailed++; continue; }
                    formsCreated += word.Forms.Count;
                    defsCreated += word.Definitions.Count;
                    wordsCreated++;
                    toAdd.Add(word);

                    if (dryRun)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"WordId {canonical} -- {word.Forms.First().Text}");
                        sb.AppendLine($"  Forms: {string.Join(", ", word.Forms.Select(f => f.Text))}");
                        sb.AppendLine($"  Name types: {string.Join(", ", word.PartsOfSpeech)}");
                        newWordEntries!.Add(sb.ToString());
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error building new name WordId {canonical}: {ex.Message}");
                    wordsFailed++;
                }
            }

            if (!dryRun && toAdd.Count > 0)
            {
                context.JMDictWords.AddRange(toAdd);
                await context.SaveChangesAsync();
            }
            context.ChangeTracker.Clear();
            Console.WriteLine($"  Inserted {Math.Min(batchStart + batchSize, newCanonicalIds.Count)}/{newCanonicalIds.Count} new name groups...");
        }

        // --- Soft-delete pass: names in the DB no longer present upstream ---
        // HARD INVARIANT (range isolation): the query is bounded to 5M-8M AND tagged "name", so JMdict
        // vocabulary (< 5M) and custom words (>= 8M, incl. the 9999990 sentinel) can never be touched.
        Console.WriteLine("Running soft-delete pass for retired names...");
        await using (var deactivateContext = await contextFactory.CreateDbContextAsync())
        {
            var toDeactivate = await deactivateContext.JMDictWords
                .Include(w => w.Forms)
                .Include(w => w.Definitions)
                .Where(w => w.WordId >= NameRangeStart && w.WordId < NameRangeEnd
                            && w.Priorities!.Contains("name")
                            && !liveWordIds.Contains(w.WordId))
                .ToListAsync();

            foreach (var word in toDeactivate)
            {
                if (!dryRun)
                {
                    foreach (var form in word.Forms) form.IsActiveInLatestSource = false;
                    foreach (var def in word.Definitions.Where(d => d.SenseIndex < 1000)) def.IsActiveInLatestSource = false;
                }
                wordsDeactivated++;
                if (dryRun)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"WordId {word.WordId} -- {word.Forms.FirstOrDefault()?.Text ?? "?"}");
                    sb.AppendLine($"  Forms: {string.Join(", ", word.Forms.Select(f => f.Text))}");
                    deactivatedWordEntries!.Add(sb.ToString());
                }
            }

            if (!dryRun && toDeactivate.Count > 0) await deactivateContext.SaveChangesAsync();
            Console.WriteLine($"  {(dryRun ? "Would deactivate" : "Deactivated")} {toDeactivate.Count} retired names.");
        }

        // --- Re-apply custom touch-ups (stored as custom data so future rebuilds preserve them) ---
        if (!dryRun)
        {
            await using var customContext = await contextFactory.CreateDbContextAsync();

            var customPriority = await customContext.JMDictWords.FirstOrDefaultAsync(w => w.WordId == 5060001);
            if (customPriority != null)
            {
                customPriority.Priorities ??= new List<string>();
                if (!customPriority.Priorities.Contains("jiten")) customPriority.Priorities.Add("jiten");
            }
            else { Console.WriteLine("  Warning: custom priority WordId 5060001 not found."); }

            var stationStreet = await customContext.JMDictWords
                .Include(w => w.Definitions)
                .FirstOrDefaultAsync(w => w.WordId == 5141615);
            if (stationStreet != null)
            {
                if (!stationStreet.PartsOfSpeech.Contains("n")) stationStreet.PartsOfSpeech.Add("n");
                // Store as a custom sense (SenseIndex >= 1000) so the delete-recreate keeps it on later syncs.
                if (!stationStreet.Definitions.Any(d => d.EnglishMeanings.Contains("street in front of station")))
                {
                    stationStreet.Definitions.Add(new JmDictDefinition
                    {
                        WordId = 5141615, SenseIndex = 1000,
                        PartsOfSpeech = ["n"], EnglishMeanings = ["street in front of station"],
                        IsActiveInLatestSource = true
                    });
                }
            }
            else { Console.WriteLine("  Warning: custom definition WordId 5141615 not found."); }

            await customContext.SaveChangesAsync();
            
            await UpsertNameVersionSentinel(contextFactory, nameVersionGloss, nameVersionDate);
        }

        // --- Report / stats ---
        if (dryRun)
        {
            int outRangeTotal = groupByCanonical.Keys.Count(id => id < NameRangeStart || id >= NameRangeEnd);

            Console.WriteLine();
            Console.WriteLine("=== JMNedict Sync Dry Run Complete ===");
            Console.WriteLine($"Name groups: {wordsRebuilt} rebuilt, {wordsCreated} new, {wordsDeactivated} to deactivate, {wordsFailed} failed");
            Console.WriteLine($"  Rebuilt groups with visible changes: {updatedWordEntries!.Count}");
            Console.WriteLine($"Forms: {formsMatched} matched, {formsCreated} to add, {formsDeactivated} to deactivate");

            reportPath ??= "jmnedict-sync-changes.txt";
            var report = new StringBuilder();
            report.AppendLine("JMNedict Sync -- Dry Run Report");
            report.AppendLine($"Source: {totalEntriesParsed} entries parsed, {mergedEntriesByKeb.Count} merged name groups");
            report.AppendLine($"JMnedict version (WordId {NameSentinelId}): {nameVersionDate ?? "(not in file)"}");
            report.AppendLine();
            report.AppendLine("=== Summary ===");
            report.AppendLine($"New name groups:        {wordsCreated}");
            report.AppendLine($"Rebuilt (with changes): {updatedWordEntries.Count}");
            report.AppendLine($"Deactivated names:      {wordsDeactivated}");
            report.AppendLine($"Forms to add:           {formsCreated}");
            report.AppendLine($"Forms to deactivate:    {formsDeactivated}");
            report.AppendLine();
            report.AppendLine("=== Range safety (HARD INVARIANT) ===");
            report.AppendLine($"Canonical WordIds outside [{NameRangeStart}, {NameRangeEnd}): {outRangeTotal}");
            report.AppendLine($"All rebuilds/inserts/deactivations confined to 5,000,000-7,999,999, tagged \"name\": " +
                              $"{(outRangeTotal == 0 ? "CONFIRMED" : "*** VIOLATION ***")}");
            report.AppendLine("JMdict vocabulary (< 5,000,000) and custom words (>= 8,000,000): untouched by construction (query-bounded).");
            report.AppendLine();

            void Dump(string title, List<string> items)
            {
                if (items.Count == 0) return;
                report.AppendLine($"=== {title} ({items.Count}) ===");
                report.AppendLine();
                for (int i = 0; i < items.Count; i++) { report.Append($"[{i + 1}] {items[i]}"); report.AppendLine(); }
            }
            Dump("New Name Groups", newWordEntries!);
            Dump("Updated Name Groups", updatedWordEntries);
            Dump("Deactivated Names", deactivatedWordEntries!);

            await File.WriteAllTextAsync(reportPath, report.ToString());
            Console.WriteLine($"\nReport written to: {reportPath}");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("=== JMNedict Sync Complete ===");
            Console.WriteLine($"Name groups: {wordsRebuilt} rebuilt, {wordsCreated} created, {wordsDeactivated} deactivated, {wordsFailed} failed");
            Console.WriteLine($"Forms: {formsMatched} matched, {formsCreated} created, {formsDeactivated} deactivated");
            Console.WriteLine($"Definitions: {defsDeleted} deleted, {defsCreated} created");
            Console.WriteLine($"JMnedict version sentinel (WordId {NameSentinelId}): {nameVersionDate ?? "(not in file)"}");
            Console.WriteLine("=================================\n");
        }

        return true;
    }

    private record NameRebuildResult(int FormsMatched, int FormsCreated, int FormsDeactivated, int DefsDeleted, int DefsCreated);

    /// <summary>Per-name fingerprint for dry-run change detection: sorted name-types + ordered glosses.</summary>
    private static string NameDefFingerprint(JmDictDefinition d) =>
        $"{string.Join(",", d.PartsOfSpeech.OrderBy(p => p, StringComparer.Ordinal))}|{string.Join(";", d.EnglishMeanings)}";

    private static string NameRubyFor(string text, string? firstKana) =>
        text.Length == 1 && WanaKana.IsKanji(text) && firstKana != null ? $"{text}[{firstKana}]" : text;

    /// <summary>Rebuilds an existing name word's non-custom content from its merged XML group.
    /// Forms are matched by (FormType, Text) and deactivated (never hard-deleted) to keep ReadingIndex
    /// stable for DeckWord references; definitions/lookups are delete-recreated; custom senses
    /// (SenseIndex &gt;= 1000) and the "jiten" priority are preserved.</summary>
    private static NameRebuildResult RebuildExistingNameWord(JitenDbContext context, JmDictWord dbWord, JmDictWord merged)
    {
        int formsMatched = 0, formsCreated = 0, formsDeactivated = 0;
        var firstKana = merged.Forms.FirstOrDefault(f => WanaKana.IsKana(f.Text))?.Text;

        var formMap = new Dictionary<(JmDictFormType, string), JmDictWordForm>();
        short maxIndex = -1;
        foreach (var f in dbWord.Forms)
        {
            formMap[(f.FormType, f.Text)] = f;
            if (f.ReadingIndex > maxIndex) maxIndex = f.ReadingIndex;
        }

        var mergedKeys = new HashSet<(JmDictFormType, string)>();
        foreach (var mf in merged.Forms)
        {
            var key = (mf.FormType, mf.Text);
            mergedKeys.Add(key);
            if (formMap.TryGetValue(key, out var dbForm))
            {
                dbForm.IsActiveInLatestSource = true;
                dbForm.RubyText = NameRubyFor(mf.Text, firstKana);
                formsMatched++;
            }
            else
            {
                if (maxIndex >= 255) continue;
                maxIndex++;
                var nf = new JmDictWordForm
                {
                    WordId = dbWord.WordId, ReadingIndex = maxIndex, Text = mf.Text,
                    RubyText = NameRubyFor(mf.Text, firstKana), FormType = mf.FormType,
                    IsActiveInLatestSource = true
                };
                dbWord.Forms.Add(nf);
                formMap[key] = nf;
                formsCreated++;
            }
        }

        foreach (var f in dbWord.Forms)
        {
            if (f.IsActiveInLatestSource && !mergedKeys.Contains((f.FormType, f.Text)))
            {
                f.IsActiveInLatestSource = false;
                formsDeactivated++;
            }
        }

        // Lookups: delete-recreate from all forms (active + inactive), mirroring SyncExistingWord.
        context.Set<JmDictLookup>().RemoveRange(dbWord.Lookups);
        dbWord.Lookups.Clear();
        var lookupKeys = new HashSet<string>();
        foreach (var f in dbWord.Forms)
            foreach (var lk in GenerateLookupsForForm(dbWord.WordId, f.Text))
                if (lookupKeys.Add(lk.LookupKey)) dbWord.Lookups.Add(lk);

        // Definitions: keep custom (SenseIndex >= 1000); delete-recreate non-custom from the XML group.
        var toRemove = dbWord.Definitions.Where(d => d.SenseIndex < 1000).ToList();
        context.Definitions.RemoveRange(toRemove);
        foreach (var d in toRemove) dbWord.Definitions.Remove(d);
        int defsDeleted = toRemove.Count, defsCreated = 0;
        short senseIndex = 0;
        foreach (var md in merged.Definitions)
        {
            dbWord.Definitions.Add(new JmDictDefinition
            {
                WordId = dbWord.WordId, SenseIndex = senseIndex++,
                PartsOfSpeech = md.PartsOfSpeech.ToList(),
                EnglishMeanings = md.EnglishMeanings.ToList(),
                IsActiveInLatestSource = true
            });
            defsCreated++;
        }

        dbWord.PartsOfSpeech = dbWord.Definitions.SelectMany(d => d.PartsOfSpeech).Distinct().ToList();
        var priorities = (merged.Priorities ?? new List<string>()).ToList();
        if (!priorities.Contains("name")) priorities.Add("name");
        if (dbWord.Priorities != null && dbWord.Priorities.Contains("jiten") && !priorities.Contains("jiten"))
            priorities.Add("jiten");
        dbWord.Priorities = priorities;

        return new NameRebuildResult(formsMatched, formsCreated, formsDeactivated, defsDeleted, defsCreated);
    }

    /// <summary>Builds a brand-new name word from its merged XML group, under the chosen canonical WordId.</summary>
    private static JmDictWord BuildNewNameWord(int wordId, JmDictWord merged)
    {
        var firstKana = merged.Forms.FirstOrDefault(f => WanaKana.IsKana(f.Text))?.Text;
        var word = new JmDictWord { WordId = wordId };

        short idx = 0;
        foreach (var mf in merged.Forms)
        {
            word.Forms.Add(new JmDictWordForm
            {
                WordId = wordId, ReadingIndex = idx++, Text = mf.Text,
                RubyText = NameRubyFor(mf.Text, firstKana), FormType = mf.FormType,
                IsActiveInLatestSource = true
            });
        }

        short senseIndex = 0;
        foreach (var md in merged.Definitions)
        {
            word.Definitions.Add(new JmDictDefinition
            {
                WordId = wordId, SenseIndex = senseIndex++,
                PartsOfSpeech = md.PartsOfSpeech.ToList(),
                EnglishMeanings = md.EnglishMeanings.ToList(),
                IsActiveInLatestSource = true
            });
        }

        var lookupKeys = new HashSet<string>();
        foreach (var f in word.Forms)
            foreach (var lk in GenerateLookupsForForm(wordId, f.Text))
                if (lookupKeys.Add(lk.LookupKey)) word.Lookups.Add(lk);

        word.PartsOfSpeech = word.Definitions.SelectMany(d => d.PartsOfSpeech).Distinct().ToList();
        var priorities = (merged.Priorities ?? new List<string>()).ToList();
        if (!priorities.Contains("name")) priorities.Add("name");
        word.Priorities = priorities;

        return word;
    }

    private static async Task UpsertNameVersionSentinel(IDbContextFactory<JitenDbContext> contextFactory,
                                                        string? fileGloss, string? date)
    {
        const int sentinelId = 9999990;
        await using var context = await contextFactory.CreateDbContextAsync();

        var existing = await context.JMDictWords
            .Include(w => w.Definitions).Include(w => w.Forms).Include(w => w.Lookups)
            .FirstOrDefaultAsync(w => w.WordId == sentinelId);
        if (existing != null)
        {
            context.Definitions.RemoveRange(existing.Definitions);
            context.Lookups.RemoveRange(existing.Lookups);
            context.WordForms.RemoveRange(existing.Forms);
            context.JMDictWords.Remove(existing);
            await context.SaveChangesAsync();
        }

        // Store the file's real gloss verbatim (matches the JMdict 9999999 sentinel); fall back to a
        // synthesized marker only if the file entry carried no trans_det.
        var gloss = fileGloss ?? $"JMnedict — loaded {date ?? "unknown date"}";
        var word = new JmDictWord
        {
            WordId = sentinelId,
            PartsOfSpeech = ["name"],
            Priorities = ["name"],
            Forms = { NewForm(sentinelId, 0, "ＪＭｎｅｄｉｃｔ", JmDictFormType.KanjiForm) },
            Definitions =
            {
                new JmDictDefinition
                {
                    WordId = sentinelId, SenseIndex = 0,
                    PartsOfSpeech = ["name"], EnglishMeanings = [gloss], IsActiveInLatestSource = true
                }
            }
        };
        var lookupKeys = new HashSet<string>();
        foreach (var form in word.Forms)
            foreach (var lk in GenerateLookupsForForm(sentinelId, form.Text))
                if (lookupKeys.Add(lk.LookupKey)) word.Lookups.Add(lk);

        context.JMDictWords.Add(word);
        await context.SaveChangesAsync();
        Console.WriteLine($"  Version sentinel (WordId {sentinelId}) set: {gloss}");
    }

    public static async Task CompareJMDicts(string dtdPath, string dictionaryPathOld, string dictionaryPathNew)
    {
        var oldWordInfos = await GetWordInfos(dtdPath, dictionaryPathOld);
        var newWordInfos = await GetWordInfos(dtdPath, dictionaryPathNew);

        Console.WriteLine($"Words - Old dictionary: {oldWordInfos.Count}, New dictionary: {newWordInfos.Count}, difference (new - old): {newWordInfos.Count - oldWordInfos.Count}");

        // Check for duplicate WordIds in new dictionary and log them
        var duplicateWordIds = newWordInfos.GroupBy(w => w.WordId)
                                           .Where(g => g.Count() > 1)
                                           .Select(g => g.Key)
                                           .ToList();

        if (duplicateWordIds.Any())
        {
            Console.WriteLine($"Warning: Found {duplicateWordIds.Count} duplicate WordIds in the new dictionary.");
            foreach (var dupId in duplicateWordIds.Take(5))
            {
                var entries = newWordInfos.Where(w => w.WordId == dupId).ToList();
                Console.WriteLine($"  Duplicate ID: {dupId}, Readings: {string.Join(", ", entries.SelectMany(e => e.Forms.Select(f => f.Text)))}");
            }

            if (duplicateWordIds.Count > 5)
                Console.WriteLine($"  ... and {duplicateWordIds.Count - 5} more");
        }

        // Create dictionaries with WordId as key for easier lookup, handling duplicates
        var oldWordDict = oldWordInfos.GroupBy(w => w.WordId)
                                      .ToDictionary(g => g.Key, g => g.First());

        var newWordDict = newWordInfos.GroupBy(w => w.WordId)
                                      .ToDictionary(g => g.Key, g => g.First());

        // Find added, removed, and changed words
        var addedWordIds = newWordDict.Keys.Except(oldWordDict.Keys).ToList();
        var removedWordIds = oldWordDict.Keys.Except(newWordDict.Keys).ToList();
        var commonWordIds = oldWordDict.Keys.Intersect(newWordDict.Keys).ToList();

        // Words with changes
        var changedWordIds = new List<int>();
        var readingChanges = new List<(int WordId, List<string> Added, List<string> Removed)>();
        var posChanges = new List<(int WordId, List<string> Added, List<string> Removed)>();
        var priorityChanges = new List<(int WordId, List<string> Added, List<string> Removed)>();

        // Check for changes in common words
        foreach (var wordId in commonWordIds)
        {
            var oldWord = oldWordDict[wordId];
            var newWord = newWordDict[wordId];
            bool isChanged = false;

            // Check for reading changes
            var oldReadings = oldWord.Forms.Select(f => f.Text).ToList();
            var newReadings = newWord.Forms.Select(f => f.Text).ToList();
            var addedReadings = newReadings.Except(oldReadings).ToList();
            var removedReadings = oldReadings.Except(newReadings).ToList();

            if (addedReadings.Any() || removedReadings.Any())
            {
                isChanged = true;
                readingChanges.Add((wordId, addedReadings, removedReadings));
            }


            // Check for parts of speech changes
            var oldPos = oldWord.Definitions.SelectMany(d => d.PartsOfSpeech).Distinct().ToList();
            ;
            var newPos = newWord.Definitions.SelectMany(d => d.PartsOfSpeech).Distinct().ToList();
            var addedPos = newPos.Except(oldPos).ToList();
            var removedPos = oldPos.Except(newPos).ToList();

            if (addedPos.Any() || removedPos.Any())
            {
                isChanged = true;
                posChanges.Add((wordId, addedPos, removedPos));
            }


            // Check for priority changes
            var oldPriorities = oldWord.Priorities ?? new List<string>();
            var newPriorities = newWord.Priorities ?? new List<string>();
            var addedPriorities = newPriorities.Except(oldPriorities).ToList();
            var removedPriorities = oldPriorities.Except(newPriorities).ToList();

            if (addedPriorities.Any() || removedPriorities.Any())
            {
                isChanged = true;
                priorityChanges.Add((wordId, addedPriorities, removedPriorities));
            }

            if (isChanged)
            {
                changedWordIds.Add(wordId);
            }
        }

        // Output the summary
        Console.WriteLine($"\nSummary of Changes:");
        Console.WriteLine($"Added words: {addedWordIds.Count}");
        Console.WriteLine($"Removed words: {removedWordIds.Count}");
        Console.WriteLine($"Changed words: {changedWordIds.Count}");

        // Detailed breakdown of changes
        Console.WriteLine($"\nDetailed Changes:");
        Console.WriteLine($"Words with reading changes: {readingChanges.Count}");
        Console.WriteLine($"Words with parts of speech changes: {posChanges.Count}");
        Console.WriteLine($"Words with priority changes: {priorityChanges.Count}");

        // List removed words
        Console.WriteLine($"\nRemoved Words:");
        foreach (var wordId in removedWordIds)
        {
            var word = oldWordDict[wordId];
            Console.WriteLine($"  WordId: {wordId}, Readings: {string.Join(", ", word.Forms.Select(f => f.Text))}");
        }
    }

    private static void MergeNameEntries(JmDictWord target, JmDictWord source)
    {
        // Merge forms (avoiding duplicates)
        foreach (var form in source.Forms)
        {
            if (!target.Forms.Any(f => f.Text == form.Text))
            {
                target.Forms.Add(NewForm(target.WordId, target.Forms.Count, form.Text, form.FormType));
            }
        }

        // Merge definitions (avoiding duplicates)
        foreach (var sourceDef in source.Definitions)
        {
            if (!target.Definitions.Any(targetDef => DefinitionsEqual(targetDef, sourceDef)))
            {
                target.Definitions.Add(sourceDef);
            }
        }

        // Merge priorities
        if (source.Priorities != null && source.Priorities.Count > 0)
        {
            if (target.Priorities == null)
                target.Priorities = new List<string>();

            foreach (var priority in source.Priorities)
            {
                if (!target.Priorities.Contains(priority))
                    target.Priorities.Add(priority);
            }
        }
    }

    private static bool DefinitionsEqual(JmDictDefinition def1, JmDictDefinition def2)
    {
        return def1.PartsOfSpeech.SequenceEqual(def2.PartsOfSpeech) &&
               def1.EnglishMeanings.SequenceEqual(def2.EnglishMeanings);
    }

    private static bool MeaningsEqual(JmDictDefinition def1, JmDictDefinition def2)
    {
        return def1.EnglishMeanings.SequenceEqual(def2.EnglishMeanings);
    }

    private static async Task LoadEntities(string dtdPath, string? dictionaryXmlPath = null)
    {
        _entities.Clear();
        _entitiesReverse.Clear();

        Regex reg = new Regex(@"<!ENTITY (.*) ""(.*)"">");

        var dtdLines = await File.ReadAllLinesAsync(dtdPath);
        dtdLines = dtdLines.Concat([
            "<!ENTITY name-char \"character\">", "<!ENTITY name-company \"company name\">",
            "<!ENTITY name-creat \"creature\">", "<!ENTITY name-dei \"deity\">",
            "<!ENTITY name-doc \"document\">", "<!ENTITY name-ev \"event\">",
            "<!ENTITY name-fem \"female given name or forename\">", "<!ENTITY name-fict \"fiction\">",
            "<!ENTITY name-given \"given name or forename, gender not specified\">",
            "<!ENTITY name-group \"group\">", "<!ENTITY name-leg \"legend\">",
            "<!ENTITY name-masc \"male given name or forename\">", "<!ENTITY name-myth \"mythology\">",
            "<!ENTITY name-obj \"object\">", "<!ENTITY name-organization \"organization name\">",
            "<!ENTITY name-oth \"other\">", "<!ENTITY name-person \"full name of a particular person\">",
            "<!ENTITY name-place \"place name\">", "<!ENTITY name-product \"product name\">",
            "<!ENTITY name-relig \"religion\">", "<!ENTITY name-serv \"service\">",
            "<!ENTITY name-ship \"ship name\">", "<!ENTITY name-station \"railway station\">",
            "<!ENTITY name-surname \"family or surname\">", "<!ENTITY name-unclass \"unclassified name\">",
            "<!ENTITY name-work \"work of art, literature, music, etc. name\">"
        ]).ToArray();

        foreach (var line in dtdLines)
        {
            var matches = reg.Match(line);
            if (matches.Length > 0 && !_entities.ContainsKey(matches.Groups[1].Value))
            {
                _entities.Add(matches.Groups[1].Value, matches.Groups[2].Value);
                _entitiesReverse.TryAdd(matches.Groups[2].Value, matches.Groups[1].Value);
            }
        }

        if (dictionaryXmlPath == null) return;

        using var reader = new StreamReader(dictionaryXmlPath);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith('<') && !line.StartsWith("<!", StringComparison.Ordinal) && !line.StartsWith("<?", StringComparison.Ordinal))
                break;

            var matches = reg.Match(line);
            if (matches.Length > 0)
            {
                _entities.TryAdd(matches.Groups[1].Value, matches.Groups[2].Value);
                _entitiesReverse.TryAdd(matches.Groups[2].Value, matches.Groups[1].Value);
            }
        }
    }

    private static async Task<List<JmDictWord>> GetWordInfos(string dtdPath, string dictionaryPath)
    {
        await LoadEntities(dtdPath, dictionaryPath);

        var readerSettings = new XmlReaderSettings() { Async = true, DtdProcessing = DtdProcessing.Parse, MaxCharactersFromEntities = 0 };
        XmlReader reader = XmlReader.Create(dictionaryPath, readerSettings);

        await reader.MoveToContentAsync();

        List<JmDictWord> wordInfos = new();

        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name != "entry") continue;

            var wordInfo = new JmDictWord();

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "ent_seq")
                        wordInfo.WordId = reader.ReadElementContentAsInt();

                    wordInfo = await ParseKEle(reader, wordInfo);
                    wordInfo = await ParseREle(reader, wordInfo);
                    wordInfo = await ParseSense(reader, wordInfo);
                }

                if (reader.NodeType != XmlNodeType.EndElement) continue;
                if (reader.Name != "entry") continue;

                foreach (var form in wordInfo.Forms)
                    form.Text = form.Text.Replace("ゎ", "わ").Replace("ヮ", "わ");

                wordInfos.Add(wordInfo);

                break;
            }
        }

        reader.Close();

        return wordInfos;
    }

    private static JmDictWordForm NewForm(int wordId, int index, string text, JmDictFormType formType, string? rubyText = null)
        => new() { WordId = wordId, ReadingIndex = (short)index, Text = text,
                   RubyText = rubyText ?? text, FormType = formType, IsActiveInLatestSource = true };

    private static async Task<JmDictWord> ParseNameKEle(XmlReader reader, JmDictWord wordInfo)
    {
        if (reader.Name != "k_ele") return wordInfo;

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "keb")
                {
                    var keb = await reader.ReadElementContentAsStringAsync();
                    wordInfo.Forms.Add(NewForm(wordInfo.WordId, wordInfo.Forms.Count, keb, JmDictFormType.KanjiForm));
                }

                if (reader.Name == "ke_pri")
                {
                    var pri = await reader.ReadElementContentAsStringAsync();
                    if (!wordInfo.Priorities!.Contains(pri))
                        wordInfo.Priorities!.Add(pri);
                }
            }

            if (reader.NodeType != XmlNodeType.EndElement) continue;
            if (reader.Name != "k_ele") continue;

            break;
        }

        return wordInfo;
    }

    private static async Task<JmDictWord> ParseNameREle(XmlReader reader, JmDictWord wordInfo)
    {
        if (reader.Name != "r_ele") return wordInfo;

        string reb = "";
        List<string> restrictions = new List<string>();
        bool isObsolete = false;

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "reb")
                {
                    reb = await reader.ReadElementContentAsStringAsync();
                }

                if (reader.Name == "re_restr")
                {
                    restrictions.Add(await reader.ReadElementContentAsStringAsync());
                }

                if (reader.Name == "re_inf")
                {
                    var inf = await reader.ReadElementContentAsStringAsync();
                    if (inf.ToLower() == "&ok")
                        isObsolete = true;
                }

                if (reader.Name == "re_pri")
                {
                    var pri = await reader.ReadElementContentAsStringAsync();
                    if (!wordInfo.Priorities!.Contains(pri))
                        wordInfo.Priorities!.Add(pri);
                }
            }

            if (reader.NodeType != XmlNodeType.EndElement) continue;
            if (reader.Name != "r_ele") continue;

            if (restrictions.Count == 0 || wordInfo.Forms.Any(f => restrictions.Contains(f.Text)))
            {
                if (!isObsolete)
                {
                    wordInfo.Forms.Add(NewForm(wordInfo.WordId, wordInfo.Forms.Count, reb, JmDictFormType.KanaForm));
                }
            }

            break;
        }

        return wordInfo;
    }

    private static async Task<JmDictWord> ParseNameTrans(XmlReader reader, JmDictWord wordInfo)
    {
        if (reader.Name != "trans") return wordInfo;

        var definition = new JmDictDefinition();

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "name_type")
                {
                    var nameType = reader.ReadElementString();
                    definition.PartsOfSpeech.Add(ElToPos(nameType));
                }

                if (reader.Name == "trans_det")
                    definition.EnglishMeanings.Add(await reader.ReadElementContentAsStringAsync());
            }

            if (reader.NodeType != XmlNodeType.EndElement) continue;
            if (reader.Name != "trans") continue;

            // Add a general "name" part of speech if no specific type was provided
            if (definition.PartsOfSpeech.Count == 0)
                definition.PartsOfSpeech.Add("name");

            // Add the definition only if it has translations
            if (definition.EnglishMeanings.Count > 0)
            {
                wordInfo.Definitions.Add(definition);
            }

            break;
        }

        return wordInfo;
    }

    private static async Task<JmDictWord> ParseKEle(XmlReader reader, JmDictWord wordInfo)
    {
        if (reader.Name != "k_ele") return wordInfo;

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "keb")
                {
                    var keb = await reader.ReadElementContentAsStringAsync();
                    wordInfo.Forms.Add(NewForm(wordInfo.WordId, wordInfo.Forms.Count, keb, JmDictFormType.KanjiForm));
                }

                if (reader.Name == "ke_pri")
                {
                    var pri = await reader.ReadElementContentAsStringAsync();
                    if (!wordInfo.Priorities!.Contains(pri))
                        wordInfo.Priorities!.Add(pri);
                }
            }

            if (reader.NodeType != XmlNodeType.EndElement) continue;
            if (reader.Name != "k_ele") continue;

            break;
        }

        return wordInfo;
    }

    private static async Task<JmDictWord> ParseREle(XmlReader reader, JmDictWord wordInfo)
    {
        if (reader.Name != "r_ele") return wordInfo;

        string reb = "";
        List<string> restrictions = new List<string>();
        bool isObsolete = false;
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "reb")
                {
                    reb = await reader.ReadElementContentAsStringAsync();
                }

                if (reader.Name == "re_restr")
                {
                    restrictions.Add(await reader.ReadElementContentAsStringAsync());
                }

                if (reader.Name == "re_inf")
                {
                    var inf = await reader.ReadElementContentAsStringAsync();
                    if (inf.ToLower() == "&ok")
                        isObsolete = true;
                }

                if (reader.Name == "re_pri")
                {
                    var pri = await reader.ReadElementContentAsStringAsync();
                    if (!wordInfo.Priorities!.Contains(pri))
                        wordInfo.Priorities!.Add(pri);
                }
            }

            if (reader.NodeType != XmlNodeType.EndElement) continue;
            if (reader.Name != "r_ele") continue;

            if (restrictions.Count == 0 || wordInfo.Forms.Any(f => restrictions.Contains(f.Text)))
            {
                if (!isObsolete)
                {
                    wordInfo.Forms.Add(NewForm(wordInfo.WordId, wordInfo.Forms.Count, reb, JmDictFormType.KanaForm));
                }
            }

            break;
        }

        return wordInfo;
    }

    private static async Task<JmDictWord> ParseSense(XmlReader reader, JmDictWord wordInfo)
    {
        if (reader.Name != "sense") return wordInfo;

        var sense = new JmDictDefinition();
        List<string> restrictions = new List<string>();

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "stagr":
                    case "stagk":
                        restrictions.Add(await reader.ReadElementContentAsStringAsync());
                        break;

                    // English-only cutover: only <gloss xml:lang="eng"> (incl. DTD-default) is kept.
                    case "gloss" when reader.HasAttributes:
                    {
                        var lang = reader.GetAttribute("xml:lang");
                        var gType = reader.GetAttribute("g_type");
                        var text = await reader.ReadElementContentAsStringAsync();
                        if (lang == "eng")
                        {
                            sense.EnglishMeanings.Add(text);
                            sense.GlossTypes.Add(gType ?? "");
                        }
                        break;
                    }

                    case "pos":
                    {
                        var el = ElToPos(reader.ReadElementString());
                        sense.Pos.Add(el);
                        sense.PartsOfSpeech.Add(el);
                        break;
                    }

                    // misc stays dual-written into PartsOfSpeech (parser POS-matching reads "uk" etc.)
                    case "misc":
                    {
                        var el = ElToPos(reader.ReadElementString());
                        sense.Misc.Add(el);
                        sense.PartsOfSpeech.Add(el);
                        break;
                    }

                    case "field":
                        sense.Field.Add(ElToPos(reader.ReadElementString()));
                        break;
                    case "dial":
                        sense.Dial.Add(ElToPos(reader.ReadElementString()));
                        break;
                    case "s_inf":
                        sense.SenseInfo.Add(await reader.ReadElementContentAsStringAsync());
                        break;
                }
            }

            if (reader.NodeType != XmlNodeType.EndElement) continue;
            if (reader.Name != "sense") continue;

            if (restrictions.Count == 0 || wordInfo.Forms.Any(f => restrictions.Contains(f.Text)))
                wordInfo.Definitions.Add(sense);

            break;
        }

        return wordInfo;
    }

    private static string ElToPos(string el)
    {
        return _entitiesReverse.GetValueOrDefault(el, el);
    }

    private static List<JmDictWord> GetCustomWords()
    {
        var customWordInfos = new List<JmDictWord>();

        customWordInfos.Add(new JmDictWord
                            {
                                WordId = 8000000,
                                Forms = [NewForm(8000000, 0, "でした", JmDictFormType.KanaForm)],
                                Definitions =
                                [
                                    new JmDictDefinition { EnglishMeanings = ["was, were"], PartsOfSpeech = ["exp"] }
                                ]
                            });

        customWordInfos.Add(new JmDictWord
                            {
                                WordId = 8000001,
                                Forms = [NewForm(8000001, 0, "イクシオトキシン", JmDictFormType.KanaForm)],
                                Definitions =
                                [
                                    new JmDictDefinition { EnglishMeanings = ["ichthyotoxin"], PartsOfSpeech = ["n"] }
                                ]
                            });

        customWordInfos.Add(new JmDictWord
                            {
                                WordId = 8000002,
                                Forms =
                                [
                                    NewForm(8000002, 0, "逢魔", JmDictFormType.KanjiForm, "逢[おう]魔[ま]"),
                                    NewForm(8000002, 1, "おうま", JmDictFormType.KanaForm)
                                ],
                                PitchAccents = [0],
                                Definitions =
                                [
                                    new JmDictDefinition
                                    {
                                        EnglishMeanings =
                                        [
                                            "meeting with evil spirits; encounter with demons or monsters",
                                            "(esp. in compounds) reference to the supernatural or ominous happenings at twilight (逢魔が時 \"the time to meet demons\")"
                                        ],
                                        PartsOfSpeech = ["exp"]
                                    }
                                ]
                            });

        customWordInfos.Add(new JmDictWord
                            {
                                WordId = 8000003,
                                Forms = [NewForm(8000003, 0, "こうする", JmDictFormType.KanaForm)],
                                Priorities = ["jiten"],
                                Definitions =
                                [
                                    new JmDictDefinition { EnglishMeanings = ["to do like this; to do in this way"], PartsOfSpeech = ["exp", "vs-i"] }
                                ]
                            });

        return customWordInfos;
    }

    public static async Task SyncJmDict(IDbContextFactory<JitenDbContext> contextFactory,
                                        string dtdPath, string dictionaryPath, string furiganaPath,
                                        bool dryRun = false, string? reportPath = null, bool rebuildKanjiTables = true)
    {
        if (dryRun)
            Console.WriteLine("=== DRY RUN MODE — no changes will be saved ===");

        Console.WriteLine("Parsing JMDict XML...");
        var parseResult = await ParseSyncEntries(dtdPath, dictionaryPath);
        var syncEntries = parseResult.Entries;
        Console.WriteLine($"Parsed {syncEntries.Count} entries from XML (created={parseResult.Created ?? "?"}, version={parseResult.Version ?? "?"}).");

        var syncEntriesById = syncEntries.ToDictionary(e => e.WordId);
        var xmlWordIds = new HashSet<int>(syncEntriesById.Keys);

        // Aggregate NG-field stats from the parsed file (for reporting + xref resolution).
        int xrefTotal = syncEntries.Sum(e => e.Senses.Sum(s => s.Xrefs.Count));
        int xrefResolved = syncEntries.Sum(e => e.Senses.Sum(s =>
            s.Xrefs.Count(x => x.Seq.HasValue && xmlWordIds.Contains(x.Seq.Value))));
        int lsourceWords = syncEntries.Count(e => e.LanguageSources.Count > 0);
        int waseiWords = syncEntries.Count(e => e.LanguageSources.Any(ls => ls.IsWasei));
        int entryInfoWords = syncEntries.Count(e => e.EntryInfos.Count > 0);
        Console.WriteLine($"NG fields: {xrefTotal} xrefs ({xrefResolved} resolvable), " +
                          $"{lsourceWords} lsource words ({waseiWords} wasei), {entryInfoWords} info words.");

        // Load furigana dictionary
        Console.WriteLine("Loading furigana data...");
        var furiganas = await JsonSerializer.DeserializeAsync<List<JMDictFurigana>>(File.OpenRead(furiganaPath));
        var furiganaDict = new Dictionary<string, List<JMDictFurigana>>();
        foreach (var f in furiganas!)
        {
            if (!furiganaDict.TryGetValue(f.Text, out var list))
            {
                list = new List<JMDictFurigana>();
                furiganaDict[f.Text] = list;
            }
            list.Add(f);
        }

        // Statistics
        int wordsUpdated = 0, wordsCreated = 0, wordsFailed = 0;
        int formsMatched = 0, formsCreated = 0, formsDeactivated = 0;
        int definitionsDeleted = 0, definitionsCreated = 0;
        int lookupsCreated = 0;
        int unresolvedRestrictions = 0;
        int wordsWithDefChanges = 0;

        // Dry-run backfill counters: words newly gaining a field that was previously absent.
        int backfillMisc = 0, backfillField = 0, backfillDial = 0, backfillSenseInfo = 0, backfillGlossType = 0;

        // Dry-run change tracking
        var newWordEntries = dryRun ? new List<string>() : null;
        var updatedWordEntries = dryRun ? new List<string>() : null;
        var deactivatedWordEntries = dryRun ? new List<string>() : null;

        // Pre-mark custom senses with high SenseIndex so they survive delete-recreate
        if (!dryRun)
        {
            Console.WriteLine("Pre-marking custom senses...");
            await using var preContext = await contextFactory.CreateDbContextAsync();
            var customDef = await preContext.Definitions
                .FirstOrDefaultAsync(d => d.WordId == 2029110 &&
                    d.EnglishMeanings.Contains("indicates na-adjective") &&
                    d.SenseIndex < 1000);
            if (customDef != null)
            {
                customDef.SenseIndex = 1000;
                await preContext.SaveChangesAsync();
                Console.WriteLine("  Marked custom sense on WordId 2029110 with SenseIndex=1000.");
            }
        }

        // Reset identity sequence to avoid PK conflicts during delete-recreate
        if (!dryRun)
        {
            await using var seqContext = await contextFactory.CreateDbContextAsync();
            await seqContext.Database.ExecuteSqlRawAsync(
                """SELECT setval(pg_get_serial_sequence('jmdict."Definitions"', 'DefinitionId'), GREATEST((SELECT MAX("DefinitionId") FROM jmdict."Definitions"), 1))""");
        }

        // Process in batches
        var allXmlWordIds = syncEntriesById.Keys.Where(id => id < 8000000).ToList();
        const int batchSize = 5000;

        for (int batchStart = 0; batchStart < allXmlWordIds.Count; batchStart += batchSize)
        {
            var batchIds = allXmlWordIds.Skip(batchStart).Take(batchSize).ToList();

            await using var context = await contextFactory.CreateDbContextAsync();
            context.ChangeTracker.AutoDetectChangesEnabled = true;

            var existingWords = await context.JMDictWords
                .Include(w => w.Forms)
                .Include(w => w.Definitions)
                .Include(w => w.Lookups)
                .Where(w => batchIds.Contains(w.WordId))
                .ToListAsync();

            var existingWordDict = existingWords.ToDictionary(w => w.WordId);

            // Delete orphaned lookups for words that are new (not in DB) to avoid PK conflicts
            if (!dryRun)
            {
                var newWordIds = batchIds.Where(id => !existingWordDict.ContainsKey(id)).ToList();
                if (newWordIds.Count > 0)
                {
                    var orphanedLookups = await context.Set<JmDictLookup>()
                        .Where(l => newWordIds.Contains(l.WordId))
                        .ToListAsync();
                    if (orphanedLookups.Count > 0)
                    {
                        context.Set<JmDictLookup>().RemoveRange(orphanedLookups);
                        Console.WriteLine($"  Removed {orphanedLookups.Count} orphaned lookups for {newWordIds.Count} new words.");
                    }
                }
            }

            foreach (var xmlWordId in batchIds)
            {
                if (!syncEntriesById.TryGetValue(xmlWordId, out var entry))
                    continue;

                try
                {
                    if (existingWordDict.TryGetValue(xmlWordId, out var dbWord))
                    {
                        // Snapshot state for dry-run comparison
                        HashSet<string>? oldDefFingerprints = null;
                        HashSet<(JmDictFormType, string)>? oldActiveForms = null;
                        bool oldHadMisc = false, oldHadField = false, oldHadDial = false, oldHadSenseInfo = false, oldHadGlossType = false;
                        if (dryRun)
                        {
                            oldDefFingerprints = dbWord.Definitions
                                .Where(d => d.SenseIndex < 1000)
                                .Select(DefFingerprint)
                                .ToHashSet();
                            oldActiveForms = dbWord.Forms
                                .Where(f => f.IsActiveInLatestSource)
                                .Select(f => (f.FormType, f.Text))
                                .ToHashSet();
                            var oldDefs = dbWord.Definitions.Where(d => d.SenseIndex < 1000).ToList();
                            oldHadMisc = oldDefs.Any(d => d.Misc.Count > 0);
                            oldHadField = oldDefs.Any(d => d.Field.Count > 0);
                            oldHadDial = oldDefs.Any(d => d.Dial.Count > 0);
                            oldHadSenseInfo = oldDefs.Any(d => d.SenseInfo.Count > 0);
                            oldHadGlossType = oldDefs.Any(d => d.GlossTypes.Any(g => g.Length > 0));
                        }

                        // UPDATE existing word
                        var result = SyncExistingWord(context, dbWord, entry, furiganaDict);
                        formsMatched += result.FormsMatched;
                        formsCreated += result.FormsCreated;
                        formsDeactivated += result.FormsDeactivated;
                        definitionsDeleted += result.DefinitionsDeleted;
                        definitionsCreated += result.DefinitionsCreated;
                        lookupsCreated += result.LookupsCreated;
                        unresolvedRestrictions += result.UnresolvedRestrictions;
                        wordsUpdated++;

                        if (dryRun)
                        {
                            var changes = new List<string>();

                            // Detect added forms
                            var addedForms = dbWord.Forms
                                .Where(f => !oldActiveForms!.Contains((f.FormType, f.Text)) && f.IsActiveInLatestSource)
                                .Select(f => f.Text)
                                .ToList();
                            if (addedForms.Count > 0)
                                changes.Add($"  + Forms added: {string.Join(", ", addedForms)}");

                            // Detect deactivated forms
                            var removedForms = dbWord.Forms
                                .Where(f => !f.IsActiveInLatestSource && oldActiveForms!.Contains((f.FormType, f.Text)))
                                .Select(f => f.Text)
                                .ToList();
                            if (removedForms.Count > 0)
                                changes.Add($"  - Forms deactivated: {string.Join(", ", removedForms)}");

                            // Detect definition changes
                            var newDefs = dbWord.Definitions.Where(d => d.SenseIndex < 1000).ToList();
                            var newDefFingerprints = newDefs.Select(DefFingerprint).ToHashSet();
                            if (!oldDefFingerprints!.SetEquals(newDefFingerprints))
                            {
                                changes.Add($"  ~ Definitions changed ({oldDefFingerprints.Count} -> {newDefFingerprints.Count} senses)");
                                wordsWithDefChanges++;
                            }

                            // Backfill detection: a field newly appearing where it was previously absent.
                            if (!oldHadMisc && newDefs.Any(d => d.Misc.Count > 0)) backfillMisc++;
                            if (!oldHadField && newDefs.Any(d => d.Field.Count > 0)) backfillField++;
                            if (!oldHadDial && newDefs.Any(d => d.Dial.Count > 0)) backfillDial++;
                            if (!oldHadSenseInfo && newDefs.Any(d => d.SenseInfo.Count > 0)) backfillSenseInfo++;
                            if (!oldHadGlossType && newDefs.Any(d => d.GlossTypes.Any(g => g.Length > 0))) backfillGlossType++;

                            if (changes.Count > 0)
                            {
                                var displayText = entry.KanjiForms.FirstOrDefault()?.Text
                                                  ?? entry.KanaForms.FirstOrDefault()?.Text ?? "?";
                                var sb = new StringBuilder();
                                sb.AppendLine($"WordId {entry.WordId} -- {displayText}");
                                foreach (var c in changes)
                                    sb.AppendLine(c);
                                updatedWordEntries!.Add(sb.ToString());
                            }
                        }
                    }
                    else
                    {
                        // CREATE new word
                        var newWord = CreateNewWord(entry, furiganaDict);
                        if (!dryRun)
                            context.JMDictWords.Add(newWord);
                        formsCreated += newWord.Forms.Count;
                        definitionsCreated += newWord.Definitions.Count;
                        lookupsCreated += newWord.Lookups.Count;
                        wordsCreated++;

                        if (dryRun)
                        {
                            var displayText = entry.KanjiForms.FirstOrDefault()?.Text
                                              ?? entry.KanaForms.FirstOrDefault()?.Text ?? "?";
                            var allForms = entry.KanjiForms.Concat(entry.KanaForms).Select(f => f.Text).ToList();
                            var sb = new StringBuilder();
                            sb.AppendLine($"WordId {entry.WordId} -- {displayText}");
                            sb.AppendLine($"  Forms: {string.Join(", ", allForms)}");
                            foreach (var sense in entry.Senses)
                            {
                                var pos = sense.Pos.Count > 0 ? $"({string.Join(", ", sense.Pos)}) " : "";
                                var meanings = string.Join("; ", sense.EnglishMeanings);
                                sb.AppendLine($"  {sense.SenseIndex + 1}. {pos}{meanings}");
                            }
                            newWordEntries!.Add(sb.ToString());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error processing WordId {xmlWordId}: {ex.Message}");
                    wordsFailed++;
                }
            }

            if (!dryRun)
                await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var processed = Math.Min(batchStart + batchSize, allXmlWordIds.Count);
            Console.WriteLine($"  Processed {processed}/{allXmlWordIds.Count} entries...");
        }

        // Soft-delete pass: find words in DB but not in XML
        Console.WriteLine("Running soft-delete pass for removed entries...");
        await using (var deactivateContext = await contextFactory.CreateDbContextAsync())
        {
            // Only deactivate JMDict-range words (not JMNedict 5000000+ or custom 8000000+)
            var wordsToDeactivate = await deactivateContext.JMDictWords
                .Include(w => w.Forms)
                .Include(w => w.Definitions)
                .Where(w => w.WordId < 5000000 && !xmlWordIds.Contains(w.WordId))
                .ToListAsync();

            foreach (var word in wordsToDeactivate)
            {
                if (!dryRun)
                {
                    foreach (var form in word.Forms)
                        form.IsActiveInLatestSource = false;
                    foreach (var def in word.Definitions.Where(d => d.SenseIndex < 1000))
                        def.IsActiveInLatestSource = false;
                }
                formsDeactivated += word.Forms.Count;

                if (dryRun)
                {
                    var displayText = word.Forms.FirstOrDefault()?.Text ?? $"WordId {word.WordId}";
                    var formTexts = word.Forms.Select(f => f.Text).ToList();
                    var sb = new StringBuilder();
                    sb.AppendLine($"WordId {word.WordId} -- {displayText}");
                    sb.AppendLine($"  Forms: {string.Join(", ", formTexts)}");
                    deactivatedWordEntries!.Add(sb.ToString());
                }
            }

            if (wordsToDeactivate.Count > 0)
            {
                if (!dryRun)
                    await deactivateContext.SaveChangesAsync();
                Console.WriteLine($"  {(dryRun ? "Would deactivate" : "Deactivated")} {wordsToDeactivate.Count} words not found in XML.");
            }
        }

        if (!dryRun)
        {
            // Re-apply custom data
            Console.WriteLine("Re-applying custom priorities and POS...");
            await using var postContext = await contextFactory.CreateDbContextAsync();

            int[] jitenPriorityIds =
            [
                1332650, 2848543, 1160790, 1203260, 1397260, 1499720, 1315130, 1550190,
                1191730, 2844190, 2207630, 1442490, 1423310, 1502390, 1343100, 1610040,
                2059630, 1495580, 1288850, 1392580, 1511350, 1648450, 1534790, 2105530,
                1223615, 1421850, 1020650, 1310640, 1495770, 1375610, 1334590,
                1609980, 1579260, 1351580, 1983760, 1207510, 1266890,
                1163940, 1625330, 1416220, 1356690, 2020520, 2084840, 2603500,
                1522150, 1591970, 1920245, 1177490, 1582430, 1310670, 1577120, 1352570,
                1604800, 1581310, 2720360, 1318950, 2541230, 1288500, 1121740, 1074630,
                1111330, 1116190, 2815290, 1157170, 2855934, 1245290, 1075810, 1314600,
                1020910, 1430230, 1349380, 1347580, 1311110, 1154770, 1282790, 1478060,
                2068450, 1169250, 1598460, 1144510, 1282970, 1982860, 1609715,
                5060001, 8000003
            ];

            var jitenWords = await postContext.JMDictWords
                .Where(w => jitenPriorityIds.Contains(w.WordId))
                .ToListAsync();

            foreach (var word in jitenWords)
            {
                word.Priorities ??= [];
                if (!word.Priorities.Contains("jiten"))
                    word.Priorities.Add("jiten");
            }

            // Re-apply custom POS for WordId 1524610
            var asNoun = await postContext.JMDictWords.FirstOrDefaultAsync(w => w.WordId == 1524610);
            if (asNoun != null && !asNoun.PartsOfSpeech.Contains("n"))
                asNoun.PartsOfSpeech.Add("n");

            // Verify custom sense for WordId 2029110
            var naAdj = await postContext.JMDictWords
                .Include(w => w.Definitions)
                .FirstOrDefaultAsync(w => w.WordId == 2029110);
            if (naAdj != null && !naAdj.Definitions.Any(d => d.EnglishMeanings.Contains("indicates na-adjective")))
            {
                postContext.Definitions.Add(new JmDictDefinition
                {
                    WordId = 2029110,
                    SenseIndex = 1000,
                    PartsOfSpeech = ["prt"],
                    Pos = ["prt"],
                    EnglishMeanings = ["indicates na-adjective"],
                    IsActiveInLatestSource = true
                });
                Console.WriteLine("  Re-added custom sense for WordId 2029110.");
            }

            // Update word-level Priorities from per-form priorities
            var allSyncedWords = await postContext.JMDictWords
                .Include(w => w.Forms)
                .Where(w => w.WordId < 8000000)
                .ToListAsync();

            foreach (var word in allSyncedWords)
            {
                var formPriorities = word.Forms
                    .Where(f => f.Priorities != null && f.Priorities.Count > 0)
                    .SelectMany(f => f.Priorities!)
                    .Distinct()
                    .ToList();

                var customPriorities = (word.Priorities ?? [])
                    .Where(p => p is "jiten" or "name")
                    .ToList();

                var merged = formPriorities.Union(customPriorities).Distinct().ToList();
                word.Priorities = merged.Count > 0 ? merged : null;
            }

            await postContext.SaveChangesAsync();

            // Rebuild cross-reference join table (truncate + insert from parsed xrefs)
            await RebuildCrossReferences(contextFactory, syncEntries, xmlWordIds);

            // Refresh the dictionary-version sentinel (WordId 9999999) from the file's own entry
            await UpsertVersionSentinel(contextFactory, syncEntries, furiganaDict);

            if (rebuildKanjiTables)
                await RebuildKanjiDerivedTables(contextFactory);

            Console.WriteLine("Rebuilding derivation links...");
            DerivationBuilder.PrintSummary(await DerivationBuilder.Build(contextFactory));
        }

        // Print statistics
        Console.WriteLine();
        if (dryRun)
        {
            Console.WriteLine("=== JMDict Sync Dry Run Complete ===");
            Console.WriteLine($"Words: {wordsUpdated} existing, {wordsCreated} new, {wordsFailed} failed");
            Console.WriteLine($"  Updated words with changes: {updatedWordEntries!.Count}");
            Console.WriteLine($"  Words to deactivate: {deactivatedWordEntries!.Count}");
            Console.WriteLine($"Forms: {formsMatched} matched, {formsCreated} to add, {formsDeactivated} to deactivate");
            Console.WriteLine($"Definitions: {wordsWithDefChanges} words with definition changes");

            // Write report
            reportPath ??= "jmdict-sync-changes.txt";
            var report = new StringBuilder();
            report.AppendLine("JMDict Sync -- Dry Run Report");
            report.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            report.AppendLine($"Source: {syncEntries.Count} entries parsed from XML");
            report.AppendLine();
            report.AppendLine("=== Summary ===");
            report.AppendLine($"New words:              {wordsCreated}");
            report.AppendLine($"Updated words:          {updatedWordEntries.Count}");
            report.AppendLine($"Deactivated words:      {deactivatedWordEntries.Count}");
            report.AppendLine($"Unchanged words:        {wordsUpdated - updatedWordEntries.Count}");
            report.AppendLine($"Forms to add:           {formsCreated}");
            report.AppendLine($"Forms to deactivate:    {formsDeactivated}");
            report.AppendLine($"Definition changes:     {wordsWithDefChanges} words affected");
            report.AppendLine();
            report.AppendLine("=== Field backfill (words newly gaining a previously-absent field) ===");
            report.AppendLine($"misc:    {backfillMisc}");
            report.AppendLine($"field:   {backfillField}");
            report.AppendLine($"dial:    {backfillDial}");
            report.AppendLine($"s_inf:   {backfillSenseInfo}");
            report.AppendLine($"g_type:  {backfillGlossType}");
            report.AppendLine($"xref:    {xrefTotal} parsed ({xrefResolved} resolved to a WordId, {xrefTotal - xrefResolved} unresolved)");
            report.AppendLine($"lsource: {lsourceWords} words ({waseiWords} wasei)");
            report.AppendLine($"info:    {entryInfoWords} words");
            var sentinelGloss = syncEntries.FirstOrDefault(e => e.WordId == 9999999)?.Senses
                .FirstOrDefault()?.EnglishMeanings.FirstOrDefault();
            report.AppendLine($"Dictionary version sentinel (WordId 9999999): {sentinelGloss ?? "(not in file)"}");
            report.AppendLine();

            if (newWordEntries!.Count > 0)
            {
                report.AppendLine($"=== New Words ({newWordEntries.Count}) ===");
                report.AppendLine();
                for (int i = 0; i < newWordEntries.Count; i++)
                {
                    report.Append($"[{i + 1}] {newWordEntries[i]}");
                    report.AppendLine();
                }
            }

            if (updatedWordEntries.Count > 0)
            {
                report.AppendLine($"=== Updated Words ({updatedWordEntries.Count}) ===");
                report.AppendLine();
                for (int i = 0; i < updatedWordEntries.Count; i++)
                {
                    report.Append($"[{i + 1}] {updatedWordEntries[i]}");
                    report.AppendLine();
                }
            }

            if (deactivatedWordEntries.Count > 0)
            {
                report.AppendLine($"=== Deactivated Words ({deactivatedWordEntries.Count}) ===");
                report.AppendLine();
                for (int i = 0; i < deactivatedWordEntries.Count; i++)
                {
                    report.Append($"[{i + 1}] {deactivatedWordEntries[i]}");
                    report.AppendLine();
                }
            }

            await File.WriteAllTextAsync(reportPath, report.ToString());
            Console.WriteLine($"\nReport written to: {reportPath}");
        }
        else
        {
            Console.WriteLine("=== JMDict Sync Complete ===");
            Console.WriteLine($"Words: {wordsUpdated} updated, {wordsCreated} created, {wordsFailed} failed");
            Console.WriteLine($"Forms: {formsMatched} matched, {formsCreated} created, {formsDeactivated} deactivated");
            Console.WriteLine($"Definitions: {definitionsDeleted} deleted, {definitionsCreated} created");
            Console.WriteLine($"Lookups: {lookupsCreated} created");
            if (unresolvedRestrictions > 0)
                Console.WriteLine($"Warnings: {unresolvedRestrictions} unresolved stagk/stagr restrictions");

            // Verification stats
            await using var verifyContext = await contextFactory.CreateDbContextAsync();

            var formStats = await verifyContext.WordForms.AsNoTracking()
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    Active = g.Count(f => f.IsActiveInLatestSource),
                    WithPriorities = g.Count(f => f.Priorities != null && f.Priorities.Count > 0),
                    WithInfoTags = g.Count(f => f.InfoTags != null && f.InfoTags.Count > 0),
                    Obsolete = g.Count(f => f.IsObsolete),
                    NoKanji = g.Count(f => f.IsNoKanji),
                    SearchOnly = g.Count(f => f.IsSearchOnly)
                })
                .FirstOrDefaultAsync();

            var defStats = await verifyContext.Definitions.AsNoTracking()
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    WithPos = g.Count(d => d.Pos.Count > 0),
                    WithMisc = g.Count(d => d.Misc.Count > 0),
                    WithField = g.Count(d => d.Field.Count > 0),
                    WithDial = g.Count(d => d.Dial.Count > 0),
                    WithRestrictions = g.Count(d => d.RestrictedToReadingIndices != null)
                })
                .FirstOrDefaultAsync();

            if (formStats != null)
            {
                Console.WriteLine();
                Console.WriteLine($"Form stats: {formStats.Total} total ({formStats.Active} active)");
                Console.WriteLine($"  With priorities: {formStats.WithPriorities}");
                Console.WriteLine($"  With info tags: {formStats.WithInfoTags}");
                Console.WriteLine($"  Obsolete: {formStats.Obsolete}, NoKanji: {formStats.NoKanji}, SearchOnly: {formStats.SearchOnly}");
            }

            if (defStats != null)
            {
                Console.WriteLine($"Definition stats: {defStats.Total} total");
                Console.WriteLine($"  With Pos: {defStats.WithPos}, Misc: {defStats.WithMisc}, Field: {defStats.WithField}, Dial: {defStats.WithDial}");
                Console.WriteLine($"  With restrictions: {defStats.WithRestrictions}");
            }
        }
    }

    private record SyncWordResult(
        int FormsMatched, int FormsCreated, int FormsDeactivated,
        int DefinitionsDeleted, int DefinitionsCreated,
        int LookupsCreated, int UnresolvedRestrictions);

    private static SyncWordResult SyncExistingWord(JitenDbContext context, JmDictWord dbWord,
                                                   SyncEntry entry, Dictionary<string, List<JMDictFurigana>> furiganaDict)
    {
        int formsMatched = 0, formsCreated = 0, formsDeactivated = 0;
        int lookupsCreated = 0;

        // Build form map from existing DB forms
        var formMap = new Dictionary<(JmDictFormType, string), JmDictWordForm>();
        short maxIndex = -1;
        foreach (var form in dbWord.Forms)
        {
            formMap[(form.FormType, form.Text)] = form;
            if (form.ReadingIndex > maxIndex)
                maxIndex = form.ReadingIndex;
        }

        // Track which existing forms were matched
        var matchedFormKeys = new HashSet<(JmDictFormType, string)>();

        // Collect all sync forms (kanji first, then kana — same order as original import)
        var allSyncForms = entry.KanjiForms.Concat(entry.KanaForms).ToList();

        // Collect all kana texts for furigana resolution
        var kanaTexts = entry.KanaForms.Select(f => f.Text).ToList();

        foreach (var syncForm in allSyncForms)
        {
            var key = (syncForm.FormType, syncForm.Text);

            if (formMap.TryGetValue(key, out var dbForm))
            {
                // Update metadata on existing form
                dbForm.Priorities = syncForm.Priorities.Count > 0 ? syncForm.Priorities : null;
                dbForm.InfoTags = syncForm.InfoTags.Count > 0 ? syncForm.InfoTags : null;
                dbForm.IsObsolete = syncForm.InfoTags.Any(t => t is "ok" or "oK");
                dbForm.IsSearchOnly = syncForm.InfoTags.Any(t => t is "sK" or "sk");
                dbForm.IsNoKanji = syncForm.IsNoKanji;
                dbForm.IsActiveInLatestSource = true;
                matchedFormKeys.Add(key);
                formsMatched++;
            }
            else
            {
                // Append new form
                maxIndex++;
                if (maxIndex > 255)
                {
                    Console.WriteLine($"  Warning: WordId {entry.WordId} exceeded 255 forms, skipping new form '{syncForm.Text}'.");
                    maxIndex--;
                    continue;
                }

                var rubyText = ResolveFurigana(syncForm, kanaTexts, furiganaDict);

                var newForm = new JmDictWordForm
                {
                    WordId = entry.WordId,
                    ReadingIndex = maxIndex,
                    Text = syncForm.Text,
                    RubyText = rubyText,
                    FormType = syncForm.FormType,
                    Priorities = syncForm.Priorities.Count > 0 ? syncForm.Priorities : null,
                    InfoTags = syncForm.InfoTags.Count > 0 ? syncForm.InfoTags : null,
                    IsObsolete = syncForm.InfoTags.Any(t => t is "ok" or "oK"),
                    IsSearchOnly = syncForm.InfoTags.Any(t => t is "sK" or "sk"),
                    IsNoKanji = syncForm.IsNoKanji,
                    IsActiveInLatestSource = true
                };

                dbWord.Forms.Add(newForm);
                formMap[key] = newForm;
                formsCreated++;
            }
        }

        // Mark unmatched existing forms as inactive
        foreach (var form in dbWord.Forms)
        {
            if (!matchedFormKeys.Contains((form.FormType, form.Text)) &&
                !allSyncForms.Any(sf => sf.FormType == form.FormType && sf.Text == form.Text))
            {
                form.IsActiveInLatestSource = false;
                formsDeactivated++;
            }
        }

        // Sync lookups (delete-and-recreate from all forms, including deactivated ones
        // so the parser can still find words whose JMDict forms were removed — the scorer
        // handles deprioritisation via a -30 penalty on IsActiveInLatestSource=false forms)
        context.Set<JmDictLookup>().RemoveRange(dbWord.Lookups);
        dbWord.Lookups.Clear();

        var lookupKeys = new HashSet<string>();
        foreach (var syncForm in allSyncForms)
        {
            foreach (var lookup in GenerateLookupsForForm(entry.WordId, syncForm.Text))
            {
                if (lookupKeys.Add(lookup.LookupKey))
                {
                    dbWord.Lookups.Add(lookup);
                    lookupsCreated++;
                }
            }
        }

        foreach (var form in dbWord.Forms)
        {
            if (form.IsActiveInLatestSource) continue;
            foreach (var lookup in GenerateLookupsForForm(entry.WordId, form.Text))
            {
                if (lookupKeys.Add(lookup.LookupKey))
                {
                    dbWord.Lookups.Add(lookup);
                    lookupsCreated++;
                }
            }
        }

        // Entry-level NG metadata (lsource etymology/wasei + <info> notes)
        ApplyEntryMetadata(dbWord, entry);

        // Sync definitions (delete-and-recreate)
        var (defsDeleted, defsCreated, unresolvedCount) = SyncDefinitions(context, dbWord, entry, formMap);

        return new SyncWordResult(formsMatched, formsCreated, formsDeactivated,
            defsDeleted, defsCreated, lookupsCreated, unresolvedCount);
    }

    /// <summary>Stable fingerprint of a definition for dry-run change detection. Includes every
    /// annotation field the sync rewrites, so backfills (misc/field/dial/s_inf/g_type) register as changes.</summary>
    private static string DefFingerprint(JmDictDefinition d) =>
        $"{d.SenseIndex}|{string.Join(";", d.EnglishMeanings)}|{string.Join(",", d.Pos)}" +
        $"|{string.Join(",", d.Misc)}|{string.Join(",", d.Field)}|{string.Join(",", d.Dial)}" +
        $"|{string.Join(";", d.SenseInfo)}|{string.Join(",", d.GlossTypes)}";

    /// <summary>Truncates and rebuilds WordKanji and KanjiReadingWords, both pure functions of the
    /// Kanji table and the WordForms (text + RubyText) the sync just rewrote.</summary>
    private static async Task RebuildKanjiDerivedTables(IDbContextFactory<JitenDbContext> contextFactory)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // Both rebuilds truncate first and validate against the Kanji table; with no kanji imported
        // they would leave the derived tables empty instead of unchanged.
        if (!await context.Kanjis.AnyAsync())
        {
            Console.WriteLine("Skipping kanji-derived rebuild: the Kanji table is empty (run --import-kanjidic first).");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Rebuilding WordKanji...");
        await KanjidicHelper.PopulateWordKanji(contextFactory);

        Console.WriteLine("Rebuilding KanjiReadingWords...");
        await KanjidicHelper.ComputeKanjiReadings(contextFactory);
    }

    /// <summary>Truncates and rebuilds the xref join table from the parsed entries (two-pass: words are
    /// all parsed in memory, so seq# targets resolve directly to WordIds).</summary>
    private static async Task RebuildCrossReferences(IDbContextFactory<JitenDbContext> contextFactory,
                                                     List<SyncEntry> syncEntries, HashSet<int> xmlWordIds)
    {
        Console.WriteLine("Rebuilding cross-references...");
        await using (var truncateContext = await contextFactory.CreateDbContextAsync())
        {
            await truncateContext.Database.ExecuteSqlRawAsync(
                """TRUNCATE TABLE jmdict."CrossReferences" RESTART IDENTITY""");
        }

        var rows = new List<JmDictCrossReference>();
        foreach (var entry in syncEntries)
        {
            foreach (var sense in entry.Senses)
            {
                foreach (var x in sense.Xrefs)
                {
                    int? target = null;
                    var dictKind = CrossReferenceDict.JMdict;
                    if (x.Dict == "jmnedict")
                    {
                        dictKind = CrossReferenceDict.JMnedict;
                        target = x.Seq; // JMnedict ids live in their own range; store as-is
                    }
                    else if (x.Seq.HasValue && xmlWordIds.Contains(x.Seq.Value))
                    {
                        target = x.Seq;
                    }

                    rows.Add(new JmDictCrossReference
                    {
                        FromWordId = entry.WordId,
                        FromSenseIndex = sense.SenseIndex,
                        Type = ParseXrefType(x.Type),
                        TargetWordId = target,
                        TargetDict = dictKind,
                        TargetSenseIndex = x.Sno,
                        TargetKanji = x.Xk,
                        TargetReading = x.Xr,
                        RawText = x.RawText
                    });
                }
            }
        }

        const int batch = 10000;
        for (int i = 0; i < rows.Count; i += batch)
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            context.JmDictCrossReferences.AddRange(rows.Skip(i).Take(batch));
            await context.SaveChangesAsync();
        }
        Console.WriteLine($"  Wrote {rows.Count} cross-references.");
    }

    /// <summary>Imports JMdict's own WordId 9999999 sentinel entry verbatim from the file (keb ＪＭｄｉｃｔ,
    /// gloss "Japanese-Multilingual Dictionary Project - Creation Date: …"). The normal batch loop skips it
    /// (id ≥ 8000000), so it's handled here and refreshed on every sync. Lookups are kept (generated by
    /// <see cref="CreateNewWord"/> from its forms) so the entry stays searchable: the JMdict/EDRDG licence
    /// requires the project's own attribution/version entry to remain accessible to users.</summary>
    private static async Task UpsertVersionSentinel(IDbContextFactory<JitenDbContext> contextFactory,
                                                    List<SyncEntry> syncEntries,
                                                    Dictionary<string, List<JMDictFurigana>> furiganaDict)
    {
        const int sentinelId = 9999999;
        var entry = syncEntries.FirstOrDefault(e => e.WordId == sentinelId);
        if (entry == null)
        {
            Console.WriteLine($"  Version sentinel (WordId {sentinelId}) not present in source file — skipped.");
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        var existing = await context.JMDictWords
            .Include(w => w.Definitions)
            .Include(w => w.Forms)
            .Include(w => w.Lookups)
            .FirstOrDefaultAsync(w => w.WordId == sentinelId);

        if (existing != null)
        {
            context.Definitions.RemoveRange(existing.Definitions);
            context.Lookups.RemoveRange(existing.Lookups);
            context.WordForms.RemoveRange(existing.Forms);
            context.JMDictWords.Remove(existing);
            await context.SaveChangesAsync();
        }

        var word = CreateNewWord(entry, furiganaDict); // keep its lookups — see summary (licence attribution must be searchable)
        context.JMDictWords.Add(word);
        await context.SaveChangesAsync();

        var gloss = word.Definitions.FirstOrDefault()?.EnglishMeanings.FirstOrDefault() ?? "(no gloss)";
        Console.WriteLine($"  Version sentinel (WordId {sentinelId}) set from file: {gloss}");
    }

    /// <summary>Maps entry-level NG metadata (lsource etymology/wasei, &lt;info&gt; notes) onto the word
    /// and infers Gairaigo origin from lsource when the CSV left it Unknown.</summary>
    private static void ApplyEntryMetadata(JmDictWord word, SyncEntry entry)
    {
        word.LanguageSources = entry.LanguageSources
            .Select(ls => new JmDictLanguageSource
            {
                Lang = ls.Lang, Text = ls.Text, IsWasei = ls.IsWasei, IsPartial = ls.IsPartial
            })
            .ToList();

        word.EntryInfo = entry.EntryInfos
            .Select(i => new JmDictEntryInfo { Type = i.Type, Text = i.Text })
            .ToList();

        // CSV-sourced origin wins (wago/kango); only fill Gairaigo when lsource is present and origin is unknown.
        if (entry.LanguageSources.Count > 0 && word.Origin == WordOrigin.Unknown)
            word.Origin = WordOrigin.Gairaigo;
    }

    private static (int Deleted, int Created, int UnresolvedRestrictions) SyncDefinitions(
        JitenDbContext context, JmDictWord dbWord, SyncEntry entry,
        Dictionary<(JmDictFormType, string), JmDictWordForm> formMap)
    {
        int unresolvedRestrictions = 0;

        // Snapshot custom definitions (SenseIndex >= 1000)
        var customDefs = dbWord.Definitions.Where(d => d.SenseIndex >= 1000).ToList();

        // Remove all non-custom definitions
        var toRemove = dbWord.Definitions.Where(d => d.SenseIndex < 1000).ToList();
        context.Definitions.RemoveRange(toRemove);
        foreach (var def in toRemove)
            dbWord.Definitions.Remove(def);
        int deleted = toRemove.Count;

        // Apply POS inheritance across senses
        List<string> inheritedPos = [];
        foreach (var sense in entry.Senses)
        {
            if (sense.Pos.Count > 0)
                inheritedPos = sense.Pos;
            else
                sense.Pos = new List<string>(inheritedPos);
        }

        // Build text-to-index maps for restriction resolution
        var kanjiTextToIndex = new Dictionary<string, short>();
        var kanaTextToIndex = new Dictionary<string, short>();
        foreach (var kvp in formMap)
        {
            if (kvp.Key.Item1 == JmDictFormType.KanjiForm)
                kanjiTextToIndex.TryAdd(kvp.Key.Item2, kvp.Value.ReadingIndex);
            else
                kanaTextToIndex.TryAdd(kvp.Key.Item2, kvp.Value.ReadingIndex);
        }

        // Create new definitions from sync senses
        int created = 0;
        foreach (var sense in entry.Senses)
        {
            // Resolve restrictions
            List<short>? restrictedIndices = null;
            if (sense.StagK.Count > 0 || sense.StagR.Count > 0)
            {
                var indices = new List<short>();
                foreach (var stagk in sense.StagK)
                {
                    if (kanjiTextToIndex.TryGetValue(stagk, out short idx))
                        indices.Add(idx);
                    else
                        unresolvedRestrictions++;
                }
                foreach (var stagr in sense.StagR)
                {
                    if (kanaTextToIndex.TryGetValue(stagr, out short idx))
                        indices.Add(idx);
                    else
                        unresolvedRestrictions++;
                }
                if (indices.Count > 0)
                    restrictedIndices = indices.Distinct().OrderBy(x => x).ToList();
            }

            var def = new JmDictDefinition
            {
                WordId = dbWord.WordId,
                SenseIndex = sense.SenseIndex,
                Pos = sense.Pos,
                Misc = sense.Misc,
                Field = sense.Field,
                Dial = sense.Dial,
                SenseInfo = sense.SenseInfo,
                GlossTypes = sense.GlossTypes,
                RestrictedToReadingIndices = restrictedIndices,
                IsActiveInLatestSource = true,
                PartsOfSpeech = sense.Pos.Concat(sense.Misc).Distinct().ToList(),
                EnglishMeanings = sense.EnglishMeanings
            };

            dbWord.Definitions.Add(def);
            created++;
        }

        // Update word-level PartsOfSpeech
        dbWord.PartsOfSpeech = dbWord.Definitions
            .SelectMany(d => d.PartsOfSpeech)
            .Distinct()
            .ToList();

        return (deleted, created, unresolvedRestrictions);
    }

    private static JmDictWord CreateNewWord(SyncEntry entry, Dictionary<string, List<JMDictFurigana>> furiganaDict)
    {
        // Apply POS inheritance
        List<string> inheritedPos = [];
        foreach (var sense in entry.Senses)
        {
            if (sense.Pos.Count > 0)
                inheritedPos = sense.Pos;
            else
                sense.Pos = new List<string>(inheritedPos);
        }

        var kanaTexts = entry.KanaForms.Select(f => f.Text).ToList();
        var allSyncForms = entry.KanjiForms.Concat(entry.KanaForms).ToList();

        var word = new JmDictWord
        {
            WordId = entry.WordId,
            PartsOfSpeech = entry.Senses.SelectMany(s => s.Pos.Concat(s.Misc)).Distinct().ToList(),
            Origin = WordOrigin.Unknown,
            Forms = [],
            Definitions = [],
            Lookups = []
        };

        // Create forms
        short readingIndex = 0;
        var formMap = new Dictionary<(JmDictFormType, string), JmDictWordForm>();
        var existingLookupKeys = new HashSet<string>();

        foreach (var syncForm in allSyncForms)
        {
            var rubyText = ResolveFurigana(syncForm, kanaTexts, furiganaDict);

            var form = new JmDictWordForm
            {
                WordId = entry.WordId,
                ReadingIndex = readingIndex,
                Text = syncForm.Text,
                RubyText = rubyText,
                FormType = syncForm.FormType,
                Priorities = syncForm.Priorities.Count > 0 ? syncForm.Priorities : null,
                InfoTags = syncForm.InfoTags.Count > 0 ? syncForm.InfoTags : null,
                IsObsolete = syncForm.InfoTags.Any(t => t is "ok" or "oK"),
                IsSearchOnly = syncForm.InfoTags.Any(t => t is "sK" or "sk"),
                IsNoKanji = syncForm.IsNoKanji,
                IsActiveInLatestSource = true
            };

            word.Forms.Add(form);
            formMap[(syncForm.FormType, syncForm.Text)] = form;

            // Generate lookups
            foreach (var lookup in GenerateLookupsForForm(entry.WordId, syncForm.Text))
            {
                if (existingLookupKeys.Add(lookup.LookupKey))
                    word.Lookups.Add(lookup);
            }

            readingIndex++;
        }

        // Create definitions
        var kanjiTextToIndex = new Dictionary<string, short>();
        var kanaTextToIndex = new Dictionary<string, short>();
        foreach (var kvp in formMap)
        {
            if (kvp.Key.Item1 == JmDictFormType.KanjiForm)
                kanjiTextToIndex.TryAdd(kvp.Key.Item2, kvp.Value.ReadingIndex);
            else
                kanaTextToIndex.TryAdd(kvp.Key.Item2, kvp.Value.ReadingIndex);
        }

        foreach (var sense in entry.Senses)
        {
            List<short>? restrictedIndices = null;
            if (sense.StagK.Count > 0 || sense.StagR.Count > 0)
            {
                var indices = new List<short>();
                foreach (var stagk in sense.StagK)
                    if (kanjiTextToIndex.TryGetValue(stagk, out short idx))
                        indices.Add(idx);
                foreach (var stagr in sense.StagR)
                    if (kanaTextToIndex.TryGetValue(stagr, out short idx))
                        indices.Add(idx);
                if (indices.Count > 0)
                    restrictedIndices = indices.Distinct().OrderBy(x => x).ToList();
            }

            word.Definitions.Add(new JmDictDefinition
            {
                WordId = entry.WordId,
                SenseIndex = sense.SenseIndex,
                Pos = sense.Pos,
                Misc = sense.Misc,
                Field = sense.Field,
                Dial = sense.Dial,
                SenseInfo = sense.SenseInfo,
                GlossTypes = sense.GlossTypes,
                RestrictedToReadingIndices = restrictedIndices,
                IsActiveInLatestSource = true,
                PartsOfSpeech = sense.Pos.Concat(sense.Misc).Distinct().ToList(),
                EnglishMeanings = sense.EnglishMeanings
            });
        }

        ApplyEntryMetadata(word, entry);

        // Merge per-form priorities into word-level, preserving non-form-derived tags
        var customPri = (word.Priorities ?? [])
            .Where(p => p is "jiten" or "name")
            .ToList();
        var formPri = word.Forms
            .Where(f => f.Priorities != null)
            .SelectMany(f => f.Priorities!)
            .Distinct()
            .ToList();
        var allPri = formPri.Union(customPri).Distinct().ToList();
        word.Priorities = allPri.Count > 0 ? allPri : null;

        return word;
    }

    private static string ResolveFurigana(SyncForm syncForm, List<string> kanaTexts,
                                          Dictionary<string, List<JMDictFurigana>> furiganaDict)
    {
        if (syncForm.FormType == JmDictFormType.KanaForm)
            return syncForm.Text;

        // Single kanji shortcut
        if (syncForm.Text.Length == 1 && WanaKana.IsKanji(syncForm.Text))
        {
            var firstKana = kanaTexts.FirstOrDefault(WanaKana.IsKana);
            return firstKana != null ? $"{syncForm.Text}[{firstKana}]" : syncForm.Text;
        }

        // Look up in furigana dictionary
        if (furiganaDict.TryGetValue(syncForm.Text, out var furiList) && furiList.Count > 0)
        {
            foreach (var furi in furiList)
            {
                if (kanaTexts.Contains(furi.Reading))
                    return furi.Parse() ?? syncForm.Text;
            }
        }

        return syncForm.Text;
    }

    private static List<JmDictLookup> GenerateLookupsForForm(int wordId, string formText)
    {
        var lookups = new List<JmDictLookup>();
        var normalised = formText.Replace("ゎ", "わ").Replace("ヮ", "わ");

        var lookupKey = WanaKana.ToHiragana(normalised, new DefaultOptions { ConvertLongVowelMark = false });
        lookups.Add(new JmDictLookup { WordId = wordId, LookupKey = lookupKey });

        var lookupKeyNoLvm = WanaKana.ToHiragana(normalised);
        if (lookupKeyNoLvm != lookupKey)
            lookups.Add(new JmDictLookup { WordId = wordId, LookupKey = lookupKeyNoLvm });

        if (WanaKana.IsKatakana(formText))
            lookups.Add(new JmDictLookup { WordId = wordId, LookupKey = formText });

        return lookups;
    }

    internal static async Task<SyncParseResult> ParseSyncEntries(string dtdPath, string dictionaryPath)
    {
        await LoadEntities(dtdPath, dictionaryPath);
        _unknownSyncElements.Clear();

        var readerSettings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Parse, MaxCharactersFromEntities = 0 };
        XmlReader reader = XmlReader.Create(dictionaryPath, readerSettings);
        await reader.MoveToContentAsync();

        var result = new SyncParseResult
        {
            Created = reader.GetAttribute("created"),
            Version = reader.GetAttribute("version")
        };
        var entries = result.Entries;

        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "entry")
                continue;

            var entry = new SyncEntry();
            int senseIndex = 0;

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "ent_seq":
                            entry.WordId = reader.ReadElementContentAsInt();
                            break;
                        case "k_ele":
                            entry.KanjiForms.Add(await ParseSyncKEle(reader));
                            break;
                        case "r_ele":
                            entry.KanaForms.Add(await ParseSyncREle(reader));
                            break;
                        case "lsource":
                            entry.LanguageSources.Add(ReadLanguageSource(reader));
                            break;
                        case "info":
                            entry.EntryInfos.Add(ReadEntryInfo(reader));
                            break;
                        case "sense":
                            entry.Senses.Add(await ParseSyncSense(reader, senseIndex++));
                            break;
                        default:
                            NoteSyncElement(reader.Name);
                            break;
                    }
                }

                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "entry")
                {
                    foreach (var kf in entry.KanjiForms)
                        kf.Text = kf.Text.Replace("ゎ", "わ").Replace("ヮ", "わ");
                    foreach (var rf in entry.KanaForms)
                        rf.Text = rf.Text.Replace("ゎ", "わ").Replace("ヮ", "わ");

                    entries.Add(entry);
                    break;
                }
            }
        }

        reader.Close();

        if (_unknownSyncElements.Count > 0)
        {
            Console.WriteLine("WARNING: unrecognized XML elements encountered (not imported):");
            foreach (var kv in _unknownSyncElements.OrderByDescending(k => k.Value))
                Console.WriteLine($"  <{kv.Key}> x{kv.Value}");
        }

        return result;
    }

    private static SyncLanguageSource ReadLanguageSource(XmlReader reader)
    {
        var lang = reader.GetAttribute("xml:lang") ?? "eng";
        var lsType = reader.GetAttribute("ls_type");
        var wasei = reader.GetAttribute("ls_wasei");
        var text = reader.ReadElementString().Trim();
        return new SyncLanguageSource
        {
            Lang = lang,
            Text = text,
            IsWasei = wasei == "y",
            IsPartial = lsType == "part"
        };
    }

    private static SyncEntryInfo ReadEntryInfo(XmlReader reader)
    {
        var type = reader.GetAttribute("inf_type") ?? "note";
        var text = reader.ReadElementString().Trim();
        return new SyncEntryInfo { Type = type, Text = text };
    }

    private static async Task<SyncForm> ParseSyncKEle(XmlReader reader)
    {
        var form = new SyncForm { FormType = JmDictFormType.KanjiForm };

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "keb":
                        form.Text = await reader.ReadElementContentAsStringAsync();
                        break;
                    case "ke_pri":
                        form.Priorities.Add(await reader.ReadElementContentAsStringAsync());
                        break;
                    case "ke_inf":
                        form.InfoTags.Add(ElToPos(reader.ReadElementString()));
                        break;
                }
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "k_ele")
                break;
        }

        return form;
    }

    private static async Task<SyncForm> ParseSyncREle(XmlReader reader)
    {
        var form = new SyncForm { FormType = JmDictFormType.KanaForm };

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "reb":
                        form.Text = await reader.ReadElementContentAsStringAsync();
                        break;
                    case "re_restr":
                        form.Restrictions.Add(await reader.ReadElementContentAsStringAsync());
                        break;
                    case "re_pri":
                        form.Priorities.Add(await reader.ReadElementContentAsStringAsync());
                        break;
                    case "re_inf":
                        form.InfoTags.Add(ElToPos(reader.ReadElementString()));
                        break;
                    case "re_nokanji":
                        form.IsNoKanji = true;
                        break;
                }
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "r_ele")
                break;
        }

        return form;
    }

    private static async Task<SyncSense> ParseSyncSense(XmlReader reader, int senseIndex)
    {
        var sense = new SyncSense { SenseIndex = senseIndex };

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "stagk":
                        sense.StagK.Add(await reader.ReadElementContentAsStringAsync());
                        break;
                    case "stagr":
                        sense.StagR.Add(await reader.ReadElementContentAsStringAsync());
                        break;
                    case "pos":
                        sense.Pos.Add(ElToPos(reader.ReadElementString()));
                        break;
                    case "misc":
                        sense.Misc.Add(ElToPos(reader.ReadElementString()));
                        break;
                    case "field":
                        sense.Field.Add(ElToPos(reader.ReadElementString()));
                        break;
                    case "dial":
                        sense.Dial.Add(ElToPos(reader.ReadElementString()));
                        break;
                    case "s_inf":
                        sense.SenseInfo.Add(await reader.ReadElementContentAsStringAsync());
                        break;
                    case "xref":
                    {
                        var xref = ReadXref(reader);
                        if (xref != null)
                            sense.Xrefs.Add(xref);
                        break;
                    }
                    case "gloss" when reader.HasAttributes:
                    {
                        var lang = reader.GetAttribute("xml:lang");
                        var gType = reader.GetAttribute("g_type");
                        var text = await reader.ReadElementContentAsStringAsync();
                        // English-only cutover: non-English glosses are dropped.
                        if (lang == "eng")
                        {
                            sense.EnglishMeanings.Add(text);
                            sense.GlossTypes.Add(gType ?? "");
                        }
                        break;
                    }
                    default:
                        NoteSyncElement(reader.Name);
                        break;
                }
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "sense")
                break;
        }

        return sense;
    }

    /// <summary>Reads a single &lt;xref&gt; element's attributes + display text into a SyncXref.</summary>
    private static SyncXref? ReadXref(XmlReader reader)
    {
        var type = reader.GetAttribute("type") ?? "see";
        var seqStr = reader.GetAttribute("seq");
        var snoStr = reader.GetAttribute("sno");
        var xk = reader.GetAttribute("xk");
        var xr = reader.GetAttribute("xr");
        var dict = reader.GetAttribute("dict");
        var raw = reader.ReadElementString().Trim();

        var xref = new SyncXref
        {
            Type = type,
            Xk = string.IsNullOrEmpty(xk) ? null : xk,
            Xr = string.IsNullOrEmpty(xr) ? null : xr,
            Dict = string.IsNullOrEmpty(dict) ? null : dict,
            RawText = raw
        };
        if (int.TryParse(seqStr, out var seq)) xref.Seq = seq;
        if (short.TryParse(snoStr, out var sno)) xref.Sno = sno;
        return xref;
    }

    private static CrossReferenceType ParseXrefType(string type) => type switch
    {
        "ant" => CrossReferenceType.Antonym,
        "syn" => CrossReferenceType.Synonym,
        _ => CrossReferenceType.SeeAlso
    };

    public static async Task<bool> ImportPitchAccents(bool verbose, IDbContextFactory<JitenDbContext> contextFactory,
                                                      string pitchAcentsDirectoryPath)
    {
        if (!Directory.Exists(pitchAcentsDirectoryPath))
        {
            Console.WriteLine($"Directory {pitchAcentsDirectoryPath} does not exist.");
            return false;
        }

        var pitchAccentFiles = Directory.GetFiles(pitchAcentsDirectoryPath, "term_meta_bank_*.json");

        if (pitchAccentFiles.Length == 0)
        {
            Console.WriteLine($"No pitch accent files found in {pitchAcentsDirectoryPath}. The files should be named term_meta_bank_*.json");
            return false;
        }

        var pitchAccentDict = new Dictionary<string, List<int>>();

        foreach (var file in pitchAccentFiles)
        {
            string jsonContent = await File.ReadAllTextAsync(file);
            using JsonDocument doc = JsonDocument.Parse(jsonContent);

            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string? word = item[0].GetString();

                if (word == null)
                    continue;

                string? type = item[1].GetString();

                JsonElement pitchInfo = item[2];
                string? reading = pitchInfo.GetProperty("reading").GetString();

                List<int> positions = new();
                foreach (JsonElement pitch in pitchInfo.GetProperty("pitches").EnumerateArray())
                {
                    positions.Add(pitch.GetProperty("position").GetInt32());
                }

                pitchAccentDict.TryAdd(word, positions);
            }
        }

        if (verbose)
            Console.WriteLine($"Found {pitchAccentDict.Count()} pitch accent records.");

        await using var context = await contextFactory.CreateDbContextAsync();
        var allWords = await context.JMDictWords.Include(w => w.Forms).ToListAsync();
        int wordsUpdated = 0;

        for (var i = 0; i < allWords.Count; i++)
        {
            if (verbose && i % 10000 == 0)
                Console.WriteLine($"Processing word {i + 1}/{allWords.Count} ({(i + 1) * 100 / allWords.Count}%)");

            var word = allWords[i];

            foreach (var form in word.Forms.OrderBy(f => f.ReadingIndex))
            {
                if (pitchAccentDict.TryGetValue(form.Text, out var pitchAccents))
                {
                    word.PitchAccents = pitchAccents;

                    wordsUpdated++;
                    break;
                }
            }
        }

        if (verbose)
            Console.WriteLine($"Updated pitch accents for {wordsUpdated} words. Saving to database...");

        await context.SaveChangesAsync();
        return true;
    }

    public static async Task<bool> ImportVocabularyOrigin(bool verbose, IDbContextFactory<JitenDbContext> contextFactory,
                                                          string vocabularyOriginFilePath)
    {
        if (!File.Exists(vocabularyOriginFilePath))
        {
            Console.WriteLine($"File {vocabularyOriginFilePath} does not exist.");
            return false;
        }

        var wordOriginMap = new Dictionary<string, WordOrigin>();

        using (var reader = new StreamReader(vocabularyOriginFilePath))
        using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
        {
            var anonymousTypeDefinition = new { word = string.Empty, origin = string.Empty };
            var records = csv.GetRecords(anonymousTypeDefinition);

            foreach (var record in records)
            {
                WordOrigin origin = WordOrigin.Unknown;

                switch (record.origin.Trim().ToLowerInvariant())
                {
                    case "和":
                        origin = WordOrigin.Wago;
                        break;
                    case "漢":
                        origin = WordOrigin.Kango;
                        break;
                    case "外":
                        origin = WordOrigin.Gairaigo;
                        break;
                }

                wordOriginMap[record.word] = origin;
            }
        }

        if (verbose)
            Console.WriteLine($"Loaded {wordOriginMap.Count} word origins from CSV file");

        await using var context = await contextFactory.CreateDbContextAsync();
        var jmdictWords = await context.JMDictWords.Include(w => w.Forms).ToListAsync();
        int updatedCount = 0;

        foreach (var word in jmdictWords)
        {
            string? matchedReading = null;

            // Try kanji forms first
            foreach (var form in word.Forms.OrderBy(f => f.ReadingIndex))
            {
                if (!wordOriginMap.ContainsKey(form.Text) || form.FormType != JmDictFormType.KanjiForm) continue;
                matchedReading = form.Text;
                break;
            }

            // If no kanji form matched, try kana forms
            if (matchedReading == null)
            {
                foreach (var form in word.Forms.OrderBy(f => f.ReadingIndex))
                {
                    if (!wordOriginMap.ContainsKey(form.Text)) continue;
                    matchedReading = form.Text;
                    break;
                }
            }

            if (matchedReading == null) continue;

            word.Origin = wordOriginMap[matchedReading];
            updatedCount++;

            if (verbose && updatedCount % 1000 == 0)
                Console.WriteLine($"Updated {updatedCount} words so far");
        }

        if (verbose)
            Console.WriteLine($"Updated origins for {updatedCount} words. Saving changes to database...");

        await context.SaveChangesAsync();

        return true;
    }
}
