namespace Jiten.Core.Data;

/// <summary>
/// Unified POS (Part of Speech) mapping and validation service.
/// Handles conversions between Sudachi POS, JMDict tags, and deconjugator tags.
/// </summary>
public static class PosMapper
{
    #region POS Category Sets

    /// <summary>
    /// POS values that can form the base of an inflection (verbs, adjectives).
    /// Used in MorphologicalAnalyser for combining tokens.
    /// </summary>
    public static IReadOnlySet<PartOfSpeech> InflectableBasePOS { get; } = new HashSet<PartOfSpeech>
    {
        PartOfSpeech.Verb,
        PartOfSpeech.IAdjective,
        PartOfSpeech.NaAdjective
    };

    /// <summary>
    /// POS values that can attach to an inflectable base (auxiliaries, suffixes, particles).
    /// Used in MorphologicalAnalyser for combining tokens.
    /// </summary>
    public static IReadOnlySet<PartOfSpeech> InflectionPartPOS { get; } = new HashSet<PartOfSpeech>
    {
        PartOfSpeech.Auxiliary,
        PartOfSpeech.Suffix,
        PartOfSpeech.Particle
    };

    /// <summary>
    /// POS values valid for compound expressions (verbs, adjectives, expressions).
    /// Used in Parser for compound word validation.
    /// </summary>
    public static IReadOnlySet<PartOfSpeech> ValidCompoundPOS { get; } = new HashSet<PartOfSpeech>
    {
        PartOfSpeech.Expression,
        PartOfSpeech.Verb,
        PartOfSpeech.IAdjective,
        PartOfSpeech.NaAdjective,
        PartOfSpeech.Auxiliary,
        PartOfSpeech.Conjunction
    };

    /// <summary>
    /// POS values that can participate in noun compounding.
    /// </summary>
    public static IReadOnlySet<PartOfSpeech> NounCompoundPOS { get; } = new HashSet<PartOfSpeech>
    {
        PartOfSpeech.Noun,
        PartOfSpeech.Name,
        PartOfSpeech.CommonNoun,
        PartOfSpeech.NaAdjective,
        PartOfSpeech.Numeral,
        PartOfSpeech.Prefix,
        PartOfSpeech.Suffix
    };

    #endregion

    #region Deconjugator Tag Validation

    /// <summary>
    /// Deconjugator tags that require POS validation against JMDict entries.
    /// If a deconjugation form has any of these tags, the matched word must have compatible POS.
    /// </summary>
    public static IReadOnlySet<string> DeconjTagsRequiringValidation { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Ichidan verbs (group 1)
        "v1", "v1-s",
        // Suru verbs
        "vs-s", "vs-i", "vs-c",
        // Kuru verb
        "vk",
        // Godan verbs (group 5)
        "v5aru", "v5b", "v5g", "v5k", "v5k-s",
        "v5m", "v5n", "v5r", "v5r-i", "v5s", "v5t", "v5u", "v5u-s", "v5uru",
        // Adjectives
        "adj-i", "adj-na", "adj-ix",
        // Auxiliary
        "aux",
        // Verb stem tags (should only match verbs, not nouns)
        "stem-past", "stem-te", "stem-te-defective", "stem-te-verbal", "stem-ren-less", "stem-ren-less-v"
    };

    /// <summary>
    /// Mapping from deconjugator tags to their compatible JMDict equivalents.
    /// Key: deconjugator tag, Value: set of compatible JMDict tags.
    /// </summary>
    // All verb tags in JMDict for stem tag validation
    private static readonly HashSet<string> AllVerbTags =
    [
        "v1", "v1-s", "v5aru", "v5b", "v5g", "v5k", "v5k-s", "v5m", "v5n",
        "v5r", "v5r-i", "v5s", "v5t", "v5u", "v5u-s", "v5uru",
        "vs", "vs-s", "vs-i", "vs-c", "vk", "vz", "vt", "vi",
        "v4k", "v4g", "v4s", "v4t", "v4n", "v4b", "v4m", "v4r", "v4h",
        "v2a-s", "v2b-k", "v2d-s", "v2g-k", "v2g-s", "v2h-k", "v2h-s",
        "v2k-k", "v2k-s", "v2m-k", "v2m-s", "v2n-s", "v2r-k", "v2r-s",
        "v2s-s", "v2t-k", "v2t-s", "v2w-s", "v2y-k", "v2y-s", "v2z-s",
        "vn", "vr", "aux-v"
    ];

    // Verb tags for directly-conjugating verbs only (excludes suru-verb types and
    // transitivity markers). Suru-verbs need する before taking endings like た/て,
    // so generic stem tags (stem-past etc.) shouldn't match vs/vt/vi alone.
    // Proper suru-verb deconjugation adds vs-i/vs-s tags with their own mappings.
    private static readonly HashSet<string> DirectConjugationVerbTags =
    [
        "v1", "v1-s", "v5aru", "v5b", "v5g", "v5k", "v5k-s", "v5m", "v5n",
        "v5r", "v5r-i", "v5s", "v5t", "v5u", "v5u-s", "v5uru",
        "vk", "vz",
        "v4k", "v4g", "v4s", "v4t", "v4n", "v4b", "v4m", "v4r", "v4h",
        "v2a-s", "v2b-k", "v2d-s", "v2g-k", "v2g-s", "v2h-k", "v2h-s",
        "v2k-k", "v2k-s", "v2m-k", "v2m-s", "v2n-s", "v2r-k", "v2r-s",
        "v2s-s", "v2t-k", "v2t-s", "v2w-s", "v2y-k", "v2y-s", "v2z-s",
        "vn", "vr", "aux-v"
    ];

    private static readonly Dictionary<string, HashSet<string>> DeconjToJmDictCompatibility =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Suru-verb mappings: deconjugator uses vs-i/vs-s/vs-c, JMDict uses vs
            ["vs-i"] = ["vs-i", "vs", "vs-s"],
            ["vs-s"] = ["vs-s", "vs", "vs-i"],
            ["vs-c"] = ["vs-c", "vs"],

            // Ichidan verb variants
            ["v1-s"] = ["v1-s", "v1"],

            // I-adjective variants — include "exp" because many expressions conjugate like
            // i-adjectives (e.g. かもしれない, しかたがない are JMDict "exp" but deconjugate via adj-i)
            ["adj-i"] = ["adj-i", "exp"],
            ["adj-ix"] = ["adj-ix", "adj-i"],

            // Godan verb with special endings
            ["v5r-i"] = ["v5r-i", "v5r"],
            ["v5k-s"] = ["v5k-s", "v5k"],
            ["v5u-s"] = ["v5u-s", "v5u"],

            // Verb stem tags — only match directly-conjugating verbs, not suru-verbs
            ["stem-past"] = DirectConjugationVerbTags,
            ["stem-te"] = DirectConjugationVerbTags,
            ["stem-te-defective"] = DirectConjugationVerbTags,
            ["stem-te-verbal"] = DirectConjugationVerbTags,
            ["stem-ren-less"] = DirectConjugationVerbTags,
            ["stem-ren-less-v"] = DirectConjugationVerbTags,
        };

    #endregion

    #region Conversion Methods

    /// <summary>
    /// Converts a Sudachi POS string (Japanese) to PartOfSpeech enum.
    /// </summary>
    public static PartOfSpeech FromSudachi(string sudachiPos)
    {
        return sudachiPos switch
        {
            "名詞" => PartOfSpeech.Noun,
            "動詞" => PartOfSpeech.Verb,
            "形容詞" => PartOfSpeech.IAdjective,
            "形状詞" => PartOfSpeech.NaAdjective,
            "副詞" => PartOfSpeech.Adverb,
            "助詞" => PartOfSpeech.Particle,
            "接続詞" => PartOfSpeech.Conjunction,
            "助動詞" => PartOfSpeech.Auxiliary,
            "感動詞" => PartOfSpeech.Interjection,
            "記号" => PartOfSpeech.Symbol,
            "接頭詞" or "接頭辞" => PartOfSpeech.Prefix,
            "フィラー" => PartOfSpeech.Filler,
            "代名詞" => PartOfSpeech.Pronoun,
            "接尾辞" => PartOfSpeech.Suffix,
            "普通名詞" => PartOfSpeech.CommonNoun,
            "補助記号" => PartOfSpeech.SupplementarySymbol,
            "空白" => PartOfSpeech.BlankSpace,
            "表現" => PartOfSpeech.Expression,
            "形動" => PartOfSpeech.NominalAdjective,
            "連体詞" => PartOfSpeech.PrenounAdjectival,
            "数詞" => PartOfSpeech.Numeral,
            "助数詞" => PartOfSpeech.Counter,
            "副詞的と" => PartOfSpeech.AdverbTo,
            "名詞接尾辞" => PartOfSpeech.NounSuffix,
            _ => PartOfSpeech.Unknown
        };
    }

    /// <summary>
    /// Converts a JMDict POS tag to PartOfSpeech enum.
    /// </summary>
    public static PartOfSpeech FromJmDict(string jmDictTag)
    {
        if (jmDictTag.StartsWith('v') && jmDictTag is not "vulg" and not "vet" and not "vidg")
            return PartOfSpeech.Verb;

        // JMnedict sometimes uses name-* tags (e.g., name-person/name-place) depending on import/source.
        // Treat all such tags as names.
        if (jmDictTag.StartsWith("name-", StringComparison.Ordinal))
            return PartOfSpeech.Name;

        return jmDictTag switch
        {
            "n" or "n-adv" or "n-t" or "n-pr" => PartOfSpeech.Noun,
            "adj-i" or "adj-ix" => PartOfSpeech.IAdjective,
            "adj-na" => PartOfSpeech.NaAdjective,
            "adj-no" or "adj-t" or "adj-f" => PartOfSpeech.NominalAdjective,
            "adj-pn" => PartOfSpeech.PrenounAdjectival,
            "adv" => PartOfSpeech.Adverb,
            "adv-to" => PartOfSpeech.AdverbTo,
            "prt" => PartOfSpeech.Particle,
            "conj" => PartOfSpeech.Conjunction,
            "aux" or "aux-v" or "aux-adj" => PartOfSpeech.Auxiliary,
            "int" => PartOfSpeech.Interjection,
            "pref" or "n-pref" => PartOfSpeech.Prefix,
            "suf" => PartOfSpeech.Suffix,
            "n-suf" => PartOfSpeech.NounSuffix,
            "pn" => PartOfSpeech.Pronoun,
            "exp" => PartOfSpeech.Expression,
            "num" => PartOfSpeech.Numeral,
            "ctr" => PartOfSpeech.Counter,
            // Name types
            "company" or "given" or "place" or "person" or "product" or "ship" or "surname"
                or "unclass" or "name-fem" or "name-masc" or "name-male" or "station" or "group" or "char"
                or "creat" or "dei" or "doc" or "ev" or "fict" or "leg"
                or "myth" or "obj" or "organization" or "oth" or "relig" or "serv" or "work"
                or "unc" => PartOfSpeech.Name,
            _ => PartOfSpeech.Unknown
        };
    }

    /// <summary>
    /// Converts any POS string (Sudachi or JMDict) to PartOfSpeech enum.
    /// This is the unified conversion method that tries both systems.
    /// </summary>
    public static PartOfSpeech FromAny(string pos)
    {
        // Try Sudachi first (Japanese strings)
        var result = FromSudachi(pos);
        if (result != PartOfSpeech.Unknown)
            return result;

        // Try JMDict tags
        return FromJmDict(pos);
    }

    /// <summary>
    /// Batch conversion of POS strings to enums.
    /// </summary>
    public static List<PartOfSpeech> FromAny(IEnumerable<string> posList)
    {
        return posList.Select(FromAny).ToList();
    }

    #endregion

    #region Validation Methods

    /// <summary>
    /// Checks if a JMDict word's POS tags are compatible with a Sudachi-derived POS.
    /// Used when matching parsed tokens against dictionary entries.
    /// </summary>
    public static bool IsJmDictCompatibleWithSudachi(
        List<PartOfSpeech> convertedPosList,
        PartOfSpeech sudachiPos,
        bool allowInterjectionFallback = false)
    {
        // CommonNoun (orphaned suffixes reclassified by the analyser) should use Noun compatibility.
        if (sudachiPos == PartOfSpeech.CommonNoun)
            sudachiPos = PartOfSpeech.Noun;

        if (convertedPosList.Contains(sudachiPos))
            return true;

        if (allowInterjectionFallback && convertedPosList.Contains(PartOfSpeech.Interjection))
            return true;

        // Sudachi 形状詞 (NaAdjective) includes words that JMDict tags as adj-pn (PrenounAdjectival)
        // Examples: この, その, あの, どの, こんな, そんな, あんな, どんな
        if (sudachiPos == PartOfSpeech.NaAdjective && convertedPosList.Contains(PartOfSpeech.PrenounAdjectival))
            return true;

        // Sudachi 形容詞 (IAdjective) should match JMDict adj-pn (PrenounAdjectival) for
        // classical attributive forms that have standalone entries (e.g. 亡き, adj-pn "deceased")
        if (sudachiPos == PartOfSpeech.IAdjective && convertedPosList.Contains(PartOfSpeech.PrenounAdjectival))
            return true;

        // Sudachi 連体詞 (PrenounAdjectival) maps to JMDict adj-f/adj-no/adj-t (NominalAdjective)
        // and adj-na (NaAdjective). E.g. 同じ is tagged adj-f + adj-na in JMDict.
        if (sudachiPos == PartOfSpeech.PrenounAdjectival &&
            (convertedPosList.Contains(PartOfSpeech.NominalAdjective) || convertedPosList.Contains(PartOfSpeech.NaAdjective)))
            return true;

        // Sudachi 感動詞 (Interjection) covers set phrases that JMDict tags as exp (Expression).
        // E.g. 初めまして, おはようございます, さようなら.
        if (sudachiPos == PartOfSpeech.Interjection && convertedPosList.Contains(PartOfSpeech.Expression))
            return true;

        // Sudachi 代名詞 (Pronoun) should match JMDict exp/adv/int for colloquial contractions.
        // E.g. そりゃ (それは), こりゃ (これは) are tagged adv/exp/int in JMDict.
        if (sudachiPos == PartOfSpeech.Pronoun &&
            (convertedPosList.Contains(PartOfSpeech.Expression) ||
             convertedPosList.Contains(PartOfSpeech.Adverb) ||
             convertedPosList.Contains(PartOfSpeech.Interjection)))
            return true;

        // Sudachi 副詞 (Adverb) should match JMDict int (Interjection).
        // E.g. いや is classified as 副詞 by Sudachi but is an interjection in JMDict (否, "no").
        if (sudachiPos == PartOfSpeech.Adverb && convertedPosList.Contains(PartOfSpeech.Interjection))
            return true;

        // Sudachi 副詞 (Adverb) should match JMDict adv-to (AdverbTo).
        // E.g. ごうごう (轟々) is tagged 副詞 by Sudachi but adv-to in JMDict.
        if (sudachiPos == PartOfSpeech.Adverb && convertedPosList.Contains(PartOfSpeech.AdverbTo))
            return true;

        // Sudachi 名詞 (Noun) should match JMDict adj-no/adj-t/adj-f (NominalAdjective).
        // Sudachi classifies many adj-no words as 名詞 (e.g. 若干, 特別, 本当).
        if (sudachiPos == PartOfSpeech.Noun && convertedPosList.Contains(PartOfSpeech.NominalAdjective))
            return true;

        // Sudachi 名詞,副詞可能 (Noun) should match JMDict adv (Adverb).
        // Many words classified as 名詞 by Sudachi with 副詞可能 subcategory only have adv in JMDict.
        if (sudachiPos == PartOfSpeech.Noun && convertedPosList.Contains(PartOfSpeech.Adverb))
            return true;

        // Sudachi 名詞 (Noun) should match JMDict num (Numeral).
        // CombineAmounts merges number+counter tokens (e.g. 二+つ → 二つ) and sets POS to Noun,
        // but the JMDict entries for these words are tagged num.
        if (sudachiPos == PartOfSpeech.Noun && convertedPosList.Contains(PartOfSpeech.Numeral))
            return true;

        // Sudachi 接尾辞 (Suffix) should match JMDict n-suf (NounSuffix) and suf (Suffix).
        // E.g. だらけ is n-suf in JMDict but 接尾辞 in Sudachi; 達 (たち) is suf in JMDict.
        if (sudachiPos == PartOfSpeech.Suffix &&
            (convertedPosList.Contains(PartOfSpeech.NounSuffix) || convertedPosList.Contains(PartOfSpeech.Suffix)))
            return true;

        // Sudachi 動詞/形容詞 (Verb/IAdjective) should match JMDict exp (Expression).
        // Many JMDict expressions are grammatically verbal/adjectival set phrases.
        // E.g. いけない, ならない, しょうがない, たまらない.
        if (sudachiPos is PartOfSpeech.Verb or PartOfSpeech.IAdjective &&
            convertedPosList.Contains(PartOfSpeech.Expression))
            return true;

        // Sudachi 助動詞 (Auxiliary) should match JMDict prt (Particle) for copulas.
        // Japanese copulas (だ, です, や Kansai-ben) are classified as 助動詞 by Sudachi
        // but tagged as prt/cop in JMDict.
        if (sudachiPos == PartOfSpeech.Auxiliary && convertedPosList.Contains(PartOfSpeech.Particle))
            return true;

        return false;
    }

    public static bool IsJmDictCompatibleWithSudachi(
        IEnumerable<string> jmDictPosTags,
        PartOfSpeech sudachiPos,
        bool allowInterjectionFallback = false)
    {
        return IsJmDictCompatibleWithSudachi(jmDictPosTags.Select(FromJmDict).ToList(), sudachiPos, allowInterjectionFallback);
    }

    /// <summary>
    /// Checks if a Sudachi token represents a name-like noun (proper noun/person/place/etc.).
    /// Sudachi emits these as POS=名詞 (Noun) with a more specific category in POS sections.
    /// </summary>
    public static bool IsNameLikeSudachiNoun(
        PartOfSpeech sudachiPos,
        PartOfSpeechSection section1,
        PartOfSpeechSection section2,
        PartOfSpeechSection section3)
    {
        if (sudachiPos != PartOfSpeech.Noun)
            return false;

        return IsNameLikeSudachiSection(section1) ||
               IsNameLikeSudachiSection(section2) ||
               IsNameLikeSudachiSection(section3);
    }

    private static bool IsNameLikeSudachiSection(PartOfSpeechSection section)
    {
        return section is PartOfSpeechSection.ProperNoun
            or PartOfSpeechSection.PersonName
            or PartOfSpeechSection.FamilyName
            or PartOfSpeechSection.Organization
            or PartOfSpeechSection.PlaceName
            or PartOfSpeechSection.Region
            or PartOfSpeechSection.Country
            or PartOfSpeechSection.Name;
    }

    /// <summary>
    /// Checks if a deconjugator tag is compatible with a JMDict entry's POS tags.
    /// Handles mappings like vs-i/vs-s → vs for suru-verbs.
    /// </summary>
    /// <param name="deconjTag">Tag from deconjugation rule (e.g., "vs-i", "v5g")</param>
    /// <param name="jmDictPosTags">POS tags from JMDict entry</param>
    public static bool IsDeconjTagCompatibleWithJmDict(string deconjTag, IEnumerable<string> jmDictPosTags)
    {
        var posTagsList = jmDictPosTags as IList<string> ?? jmDictPosTags.ToList();

        // Direct match
        if (posTagsList.Contains(deconjTag, StringComparer.OrdinalIgnoreCase))
            return true;

        // Check compatibility mappings
        if (DeconjToJmDictCompatibility.TryGetValue(deconjTag, out var compatibleTags))
        {
            foreach (var compatibleTag in compatibleTags)
            {
                if (posTagsList.Contains(compatibleTag, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if any of the deconjugation tags are compatible with the JMDict POS tags.
    /// </summary>
    public static bool AreDeconjTagsCompatibleWithJmDict(
        IEnumerable<string> deconjTags,
        IEnumerable<string> jmDictPosTags)
    {
        var jmDictList = jmDictPosTags as IList<string> ?? jmDictPosTags.ToList();
        return deconjTags.Any(tag => IsDeconjTagCompatibleWithJmDict(tag, jmDictList));
    }

    /// <summary>
    /// Filters deconjugation tags to only those that require POS validation.
    /// </summary>
    public static IEnumerable<string> GetValidatableDeconjTags(IEnumerable<string> tags)
    {
        return tags.Where(t => DeconjTagsRequiringValidation.Contains(t));
    }

    #endregion

    #region Category Checks

    /// <summary>
    /// Checks if a POS can be the base of an inflection (verb, i-adjective, na-adjective).
    /// </summary>
    public static bool IsInflectableBase(PartOfSpeech pos) => InflectableBasePOS.Contains(pos);

    /// <summary>
    /// Checks if a POS can attach to an inflectable base (auxiliary, suffix, particle).
    /// </summary>
    public static bool IsInflectionPart(PartOfSpeech pos) => InflectionPartPOS.Contains(pos);

    /// <summary>
    /// Checks if a POS is valid for compound expressions.
    /// </summary>
    public static bool IsValidCompoundPOS(PartOfSpeech pos) => ValidCompoundPOS.Contains(pos);

    /// <summary>
    /// Checks if a POS can participate in noun compounding.
    /// </summary>
    public static bool IsNounForCompounding(PartOfSpeech pos) => NounCompoundPOS.Contains(pos);

    #endregion

    #region Extensibility

    /// <summary>
    /// Registers additional compatibility mappings between deconjugator and JMDict tags.
    /// Useful for adding new mappings without modifying the core class.
    /// </summary>
    /// <param name="deconjTag">Deconjugator tag</param>
    /// <param name="compatibleJmDictTags">Compatible JMDict tags</param>
    public static void RegisterDeconjCompatibility(string deconjTag, params string[] compatibleJmDictTags)
    {
        if (!DeconjToJmDictCompatibility.TryGetValue(deconjTag, out var existing))
        {
            existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DeconjToJmDictCompatibility[deconjTag] = existing;
        }

        foreach (var tag in compatibleJmDictTags)
            existing.Add(tag);
    }

    #endregion
}
