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

    public static async Task<bool> SyncMissingJMNedict(IDbContextFactory<JitenDbContext> contextFactory, string dtdPath, string jmneDictPath)
    {
        Console.WriteLine("Starting JMNedict sync...");

        await LoadEntities(dtdPath, jmneDictPath);

        var readerSettings = new XmlReaderSettings() { Async = true, DtdProcessing = DtdProcessing.Parse, MaxCharactersFromEntities = 0 };
        XmlReader reader = XmlReader.Create(jmneDictPath, readerSettings);

        await reader.MoveToContentAsync();

        await using var context = await contextFactory.CreateDbContextAsync();

        // Load existing entries from database with all related data
        Console.WriteLine("Loading existing JMDict entries from database...");
        var existingEntries = await context.JMDictWords
            .Include(w => w.Definitions)
            .Include(w => w.Lookups)
            .Include(w => w.Forms)
            .ToListAsync();

        var existingWordDict = existingEntries.ToDictionary(w => w.WordId);
        Console.WriteLine($"Loaded {existingEntries.Count} existing entries");

        // Statistics tracking
        int totalEntriesParsed = 0;
        int newEntriesToInsert = 0;
        int existingEntriesToUpdate = 0;
        int entriesUnchanged = 0;
        int readingsAdded = 0;
        int definitionsAdded = 0;
        int lookupsToRegenerate = 0;

        // PHASE 1: Parse XML and merge entries by kanji (before database comparison)
        Dictionary<string, JmDictWord> mergedEntriesByKeb = new();
        Dictionary<string, List<int>> kanjiToWordIds = new(); // Track all WordIds per kanji
        List<JmDictWord> entriesToUpdate = new();
        List<int> wordIdsNeedingLookupRegeneration = new();

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

        Console.WriteLine($"Parsed {totalEntriesParsed} XML entries, merged into {mergedEntriesByKeb.Count} unique kanji groups");

        // PHASE 2: Compare merged entries with database (database-aware matching)
        Console.WriteLine("Comparing merged entries with database...");

        Dictionary<string, JmDictWord> newNamesByKeb = new();

        foreach (var kvp in mergedEntriesByKeb)
        {
            string kanji = kvp.Key;
            JmDictWord mergedEntry = kvp.Value;
            List<int> allWordIds = kanjiToWordIds[kanji];

            // Find the FIRST WordId that exists in DB
            JmDictWord? existingDbEntry = null;
            foreach (int wordId in allWordIds)
            {
                if (existingWordDict.TryGetValue(wordId, out existingDbEntry))
                {
                    // Found existing entry in DB - use this one!
                    break;
                }
            }

            if (existingDbEntry != null)
            {
                // Update existing DB entry with merged readings
                bool needsUpdate = false;
                int formsBeforeCount = existingDbEntry.Forms.Count;

                // Merge forms from all XML entries
                foreach (var mergedForm in mergedEntry.Forms)
                {
                    if (!existingDbEntry.Forms.Any(f => f.Text == mergedForm.Text))
                    {
                        existingDbEntry.Forms.Add(NewForm(
                            existingDbEntry.WordId,
                            existingDbEntry.Forms.Count,
                            mergedForm.Text,
                            mergedForm.FormType));

                        needsUpdate = true;
                        readingsAdded++;
                    }
                }

                // Merge priorities
                if (mergedEntry.Priorities != null && mergedEntry.Priorities.Count > 0)
                {
                    existingDbEntry.Priorities ??= new List<string>();
                    foreach (var priority in mergedEntry.Priorities)
                    {
                        if (!existingDbEntry.Priorities.Contains(priority))
                        {
                            existingDbEntry.Priorities.Add(priority);
                            needsUpdate = true;
                        }
                    }
                }

                // Merge definitions
                foreach (var xmlDef in mergedEntry.Definitions)
                {
                    var existingDefWithSameMeanings = existingDbEntry.Definitions.FirstOrDefault(d => MeaningsEqual(d, xmlDef));

                    if (existingDefWithSameMeanings != null)
                    {
                        // Merge POS tags if meanings are identical
                        var posBeforeCount = existingDefWithSameMeanings.PartsOfSpeech.Count;
                        foreach (var pos in xmlDef.PartsOfSpeech)
                        {
                            if (!existingDefWithSameMeanings.PartsOfSpeech.Contains(pos))
                            {
                                existingDefWithSameMeanings.PartsOfSpeech.Add(pos);
                                needsUpdate = true;
                            }
                        }
                    }
                    else if (!existingDbEntry.Definitions.Any(d => DefinitionsEqual(d, xmlDef)))
                    {
                        // Add new definition if meanings are different
                        existingDbEntry.Definitions.Add(xmlDef);
                        needsUpdate = true;
                        definitionsAdded++;
                    }
                }

                if (needsUpdate)
                {
                    if (!entriesToUpdate.Contains(existingDbEntry))
                        entriesToUpdate.Add(existingDbEntry);

                    // If forms were added, mark for lookup regeneration
                    if (existingDbEntry.Forms.Count > formsBeforeCount)
                    {
                        if (!wordIdsNeedingLookupRegeneration.Contains(existingDbEntry.WordId))
                            wordIdsNeedingLookupRegeneration.Add(existingDbEntry.WordId);
                    }

                    existingEntriesToUpdate++;
                }
                else
                {
                    entriesUnchanged++;
                }
            }
            else
            {
                // None of the WordIds exist in DB - add as new entry
                newNamesByKeb[kanji] = mergedEntry;
            }
        }

        newEntriesToInsert = newNamesByKeb.Count;

        Console.WriteLine($"\n=== JMnedict Sync Statistics ===");
        Console.WriteLine($"Total entries parsed from XML: {totalEntriesParsed}");
        Console.WriteLine($"Existing entries in database: {existingEntries.Count}");
        Console.WriteLine($"\nCategorisation:");
        Console.WriteLine($"  New entries to insert: {newEntriesToInsert}");
        Console.WriteLine($"  Existing entries to update: {existingEntriesToUpdate}");
        Console.WriteLine($"  Entries unchanged: {entriesUnchanged}");
        Console.WriteLine($"\nChanges detected:");
        Console.WriteLine($"  Total readings added: {readingsAdded}");
        Console.WriteLine($"  Total definitions added: {definitionsAdded}");
        Console.WriteLine($"  Lookups to regenerate: {wordIdsNeedingLookupRegeneration.Count}");

        // Process new entries (same post-processing as ImportJMNedict)
        List<JmDictWord> nameWords = newNamesByKeb.Values.ToList();

        if (nameWords.Count > 0)
        {
            Console.WriteLine($"\nProcessing {nameWords.Count} new entries...");

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

            // Validate new entries before insertion
            int beforeValidation = nameWords.Count;
            nameWords = nameWords.Where(w =>
                w.Forms.Count > 0 &&
                w.Definitions.Count > 0
            ).ToList();
            int invalidEntries = beforeValidation - nameWords.Count;

            if (invalidEntries > 0)
            {
                Console.WriteLine($"Filtered out {invalidEntries} invalid new entries");
            }
        }

        // Process updated entries - regenerate furigana and lookups
        if (entriesToUpdate.Count > 0)
        {
            Console.WriteLine($"\nProcessing {entriesToUpdate.Count} updated entries...");

            foreach (var word in entriesToUpdate)
            {
                // Regenerate furigana for all forms
                foreach (var form in word.Forms)
                {
                    string r = form.Text;
                    if (r.Length == 1 && WanaKana.IsKanji(r))
                    {
                        var kanaForm = word.Forms.FirstOrDefault(f => WanaKana.IsKana(f.Text));
                        form.RubyText = kanaForm != null ? $"{r}[{kanaForm.Text}]" : r;
                    }
                    else
                    {
                        form.RubyText = r;
                    }
                }

                // Update PartsOfSpeech from definitions
                word.PartsOfSpeech = word.Definitions.SelectMany(d => d.PartsOfSpeech).Distinct().ToList();

                // Regenerate lookups if forms were added
                if (wordIdsNeedingLookupRegeneration.Contains(word.WordId))
                {
                    // Remove old lookups
                    context.Lookups.RemoveRange(word.Lookups);

                    // Generate new lookups
                    List<JmDictLookup> lookups = new();
                    var addedKeys = new HashSet<string>();
                    foreach (var form in word.Forms)
                    {
                        string r = form.Text;
                        var lookupKey = WanaKana.ToHiragana(r.Replace("ゎ", "わ").Replace("ヮ", "わ"),
                                                            new DefaultOptions() { ConvertLongVowelMark = false });
                        var lookupKeyWithoutLongVowelMark = WanaKana.ToHiragana(r.Replace("ゎ", "わ").Replace("ヮ", "わ"));

                        if (addedKeys.Add(lookupKey))
                        {
                            lookups.Add(new JmDictLookup { WordId = word.WordId, LookupKey = lookupKey });
                        }

                        if (lookupKeyWithoutLongVowelMark != lookupKey &&
                            addedKeys.Add(lookupKeyWithoutLongVowelMark))
                        {
                            lookups.Add(new JmDictLookup { WordId = word.WordId, LookupKey = lookupKeyWithoutLongVowelMark });
                        }

                        if (WanaKana.IsKatakana(r) && addedKeys.Add(r))
                            lookups.Add(new JmDictLookup { WordId = word.WordId, LookupKey = r });
                    }

                    word.Lookups = lookups;
                    lookupsToRegenerate++;
                }
            }
        }

        // Save all changes to database
        try
        {
            Console.WriteLine($"\nSaving changes to database...");

            if (nameWords.Count > 0)
            {
                context.JMDictWords.AddRange(nameWords);
            }

            await context.SaveChangesAsync();

            Console.WriteLine($"\n✓ Sync completed successfully!");
            Console.WriteLine($"\nFinal statistics:");
            Console.WriteLine($"  New entries inserted: {nameWords.Count}");
            Console.WriteLine($"  Existing entries updated: {entriesToUpdate.Count}");
            Console.WriteLine($"  Readings added: {readingsAdded}");
            Console.WriteLine($"  Definitions added: {definitionsAdded}");
            Console.WriteLine($"  Lookups regenerated: {lookupsToRegenerate}");
            Console.WriteLine($"=================================\n");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ Error saving to database: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return false;
        }
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
               def1.EnglishMeanings.SequenceEqual(def2.EnglishMeanings) &&
               def1.DutchMeanings.SequenceEqual(def2.DutchMeanings) &&
               def1.FrenchMeanings.SequenceEqual(def2.FrenchMeanings) &&
               def1.GermanMeanings.SequenceEqual(def2.GermanMeanings) &&
               def1.SpanishMeanings.SequenceEqual(def2.SpanishMeanings) &&
               def1.HungarianMeanings.SequenceEqual(def2.HungarianMeanings) &&
               def1.RussianMeanings.SequenceEqual(def2.RussianMeanings) &&
               def1.SlovenianMeanings.SequenceEqual(def2.SlovenianMeanings);
    }

    private static bool MeaningsEqual(JmDictDefinition def1, JmDictDefinition def2)
    {
        return def1.EnglishMeanings.SequenceEqual(def2.EnglishMeanings) &&
               def1.DutchMeanings.SequenceEqual(def2.DutchMeanings) &&
               def1.FrenchMeanings.SequenceEqual(def2.FrenchMeanings) &&
               def1.GermanMeanings.SequenceEqual(def2.GermanMeanings) &&
               def1.SpanishMeanings.SequenceEqual(def2.SpanishMeanings) &&
               def1.HungarianMeanings.SequenceEqual(def2.HungarianMeanings) &&
               def1.RussianMeanings.SequenceEqual(def2.RussianMeanings) &&
               def1.SlovenianMeanings.SequenceEqual(def2.SlovenianMeanings);
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
            if (definition.EnglishMeanings.Count > 0 ||
                definition.DutchMeanings.Count > 0 ||
                definition.FrenchMeanings.Count > 0 ||
                definition.GermanMeanings.Count > 0 ||
                definition.SpanishMeanings.Count > 0 ||
                definition.HungarianMeanings.Count > 0 ||
                definition.RussianMeanings.Count > 0 ||
                definition.SlovenianMeanings.Count > 0)
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
                if (reader.Name == "stagr")
                {
                    restrictions.Add(await reader.ReadElementContentAsStringAsync());
                }

                // check the language attribute
                if (reader is { Name: "gloss", HasAttributes: true })
                {
                    var attribute = reader.GetAttribute("xml:lang");
                    switch (attribute)
                    {
                        case "eng":
                            sense.EnglishMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                        case "dut":
                            sense.DutchMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                        case "fre":
                            sense.FrenchMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                        case "ger":
                            sense.GermanMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                        case "spa":
                            sense.SpanishMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                        case "hun":
                            sense.HungarianMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                        case "rus":
                            sense.RussianMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                        case "slv":
                            sense.SlovenianMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                        default:
                            //sense.EnglishMeanings.Add(await reader.ReadElementContentAsStringAsync());
                            break;
                    }
                }

                if (reader.Name == "pos")
                {
                    var el = reader.ReadElementString();

                    sense.PartsOfSpeech.Add(ElToPos(el));
                }

                if (reader.Name == "misc")
                {
                    var el = reader.ReadElementString();

                    sense.PartsOfSpeech.Add(ElToPos(el));
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
                                        bool dryRun = false, string? reportPath = null)
    {
        if (dryRun)
            Console.WriteLine("=== DRY RUN MODE — no changes will be saved ===");

        Console.WriteLine("Parsing JMDict XML...");
        var syncEntries = await ParseSyncEntries(dtdPath, dictionaryPath);
        Console.WriteLine($"Parsed {syncEntries.Count} entries from XML.");

        var syncEntriesById = syncEntries.ToDictionary(e => e.WordId);
        var xmlWordIds = new HashSet<int>(syncEntriesById.Keys);

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
                        if (dryRun)
                        {
                            oldDefFingerprints = dbWord.Definitions
                                .Where(d => d.SenseIndex < 1000)
                                .Select(d => $"{d.SenseIndex}|{string.Join(";", d.EnglishMeanings)}|{string.Join(",", d.Pos)}")
                                .ToHashSet();
                            oldActiveForms = dbWord.Forms
                                .Where(f => f.IsActiveInLatestSource)
                                .Select(f => (f.FormType, f.Text))
                                .ToHashSet();
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
                            var newDefFingerprints = dbWord.Definitions
                                .Where(d => d.SenseIndex < 1000)
                                .Select(d => $"{d.SenseIndex}|{string.Join(";", d.EnglishMeanings)}|{string.Join(",", d.Pos)}")
                                .ToHashSet();
                            if (!oldDefFingerprints!.SetEquals(newDefFingerprints))
                            {
                                changes.Add($"  ~ Definitions changed ({oldDefFingerprints.Count} -> {newDefFingerprints.Count} senses)");
                                wordsWithDefChanges++;
                            }

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

        // Sync definitions (delete-and-recreate)
        var (defsDeleted, defsCreated, unresolvedCount) = SyncDefinitions(context, dbWord, entry, formMap);

        return new SyncWordResult(formsMatched, formsCreated, formsDeactivated,
            defsDeleted, defsCreated, lookupsCreated, unresolvedCount);
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
                RestrictedToReadingIndices = restrictedIndices,
                IsActiveInLatestSource = true,
                PartsOfSpeech = sense.Pos.Concat(sense.Misc).Distinct().ToList(),
                EnglishMeanings = sense.EnglishMeanings,
                DutchMeanings = sense.DutchMeanings,
                FrenchMeanings = sense.FrenchMeanings,
                GermanMeanings = sense.GermanMeanings,
                SpanishMeanings = sense.SpanishMeanings,
                HungarianMeanings = sense.HungarianMeanings,
                RussianMeanings = sense.RussianMeanings,
                SlovenianMeanings = sense.SlovenianMeanings
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
                RestrictedToReadingIndices = restrictedIndices,
                IsActiveInLatestSource = true,
                PartsOfSpeech = sense.Pos.Concat(sense.Misc).Distinct().ToList(),
                EnglishMeanings = sense.EnglishMeanings,
                DutchMeanings = sense.DutchMeanings,
                FrenchMeanings = sense.FrenchMeanings,
                GermanMeanings = sense.GermanMeanings,
                SpanishMeanings = sense.SpanishMeanings,
                HungarianMeanings = sense.HungarianMeanings,
                RussianMeanings = sense.RussianMeanings,
                SlovenianMeanings = sense.SlovenianMeanings
            });
        }

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

    private static async Task<List<SyncEntry>> ParseSyncEntries(string dtdPath, string dictionaryPath)
    {
        await LoadEntities(dtdPath, dictionaryPath);

        var readerSettings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Parse, MaxCharactersFromEntities = 0 };
        XmlReader reader = XmlReader.Create(dictionaryPath, readerSettings);
        await reader.MoveToContentAsync();

        var entries = new List<SyncEntry>();

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
                        case "sense":
                            entry.Senses.Add(await ParseSyncSense(reader, senseIndex++));
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
        return entries;
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
                    case "gloss" when reader.HasAttributes:
                    {
                        var lang = reader.GetAttribute("xml:lang");
                        var text = await reader.ReadElementContentAsStringAsync();
                        switch (lang)
                        {
                            case "eng": sense.EnglishMeanings.Add(text); break;
                            case "dut": sense.DutchMeanings.Add(text); break;
                            case "fre": sense.FrenchMeanings.Add(text); break;
                            case "ger": sense.GermanMeanings.Add(text); break;
                            case "spa": sense.SpanishMeanings.Add(text); break;
                            case "hun": sense.HungarianMeanings.Add(text); break;
                            case "rus": sense.RussianMeanings.Add(text); break;
                            case "slv": sense.SlovenianMeanings.Add(text); break;
                        }
                        break;
                    }
                }
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "sense")
                break;
        }

        return sense;
    }

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
