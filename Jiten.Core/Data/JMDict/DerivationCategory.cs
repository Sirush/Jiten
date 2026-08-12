namespace Jiten.Core.Data.JMDict;

public enum DerivationCategory : short
{
    SaNominal = 1,
    GeAdjective = 2,
    MiNominal = 3,
    Garu = 4,
    Sou = 5,
    KuAdverb = 6,
    NaSaNominal = 7,
    NiAdverb = 8,
    Potential = 9,
    MasuStemNoun = 10,
    MeModerate = 11,
    Sugiru = 12,
    Ppoi = 13,
    Gachi = 14,
    Gimi = 15,
    Gari = 16,
    CausativeDoublet = 17,
    ZuruJiru = 18,
    LexicalPassive = 19,
    HonorificPrefix = 20,
    TeAdverb = 21,
    MaIntensifier = 22,
    ClassicalAdjective = 23,
    TransitivityPair = 24,
    Reduplication = 25,
    ParticleElision = 26
}

public enum DerivationSource : short
{
    RuleGenerated = 0,
    Curated = 1,
    Manual = 2
}

public enum DerivationDirection : short
{
    Bidirectional = 0,
    BaseToDerivedOnly = 1
}

/// <param name="Key">Wire/override-file identifier; stable across rebuilds and used as the settings value.</param>
public record DerivationCategoryInfo(
    DerivationCategory Category,
    string Key,
    string Group,
    string Label,
    string ExampleBase,
    string ExampleDerived,
    string Explanation);

public record DerivationGroupInfo(string Key, string Label, string Explanation);

public static class DerivationCategories
{
    /// <summary>Checkbox groups for the settings UI; a group toggles every category it contains at once.</summary>
    public static readonly IReadOnlyList<DerivationGroupInfo> Groups =
    [
        new("nominalisation", "Adjective nouns (〜さ, 〜み)",
            "Nouns built from adjectives, often expressing a quality or degree: 強い → 強さ, 深い → 深み."),
        new("masu_stem", "Verb stem nouns",
            "A verb's stem used as a noun: 動く → 動き."),
        new("adverbs", "Adverbial forms (〜く, 〜に, 〜て)",
            "Adverbs built from an adjective or a verb's て-form: 詳しい → 詳しく, 静か → 静かに."),
        new("potential", "Potential / passive forms",
            "Potential and passive forms that have their own dictionary entries: 読む → 読める, 見る → 見られる."),
        new("appearance", "Appearance & impression (〜げ, 〜そう, 〜っぽい)",
            "Forms meaning \"looks\", \"seems\", or \"-ish\": 怪しい → 怪しげ, 安い → 安っぽい."),
        new("garu", "Feelings & tendencies (〜がる, 〜がり)",
            "Showing a feeling, or being someone prone to that feeling: 怖い → 怖がる, 寒い → 寒がり."),
        new("degree", "Degree (〜め, 〜すぎる)",
            "Forms meaning \"on the ~ side\" or \"too much\": 多い → 多め, 食べる → 食べ過ぎる."),
        new("tendency", "Tendency (〜がち, 〜気味)",
            "Being prone to something, or showing slight signs of it: 遅れる → 遅れがち, 風邪 → 風邪気味."),
        new("honorific", "お〜 / ご〜 prefixes",
            "お〜 or ご〜 added to a word to make it polite or respectful: 茶 → お茶, 存知 → ご存知."),
        new("intensifier", "Intensifying 真っ〜",
            "An intensifying prefix meaning \"completely\" or \"pure\": 白い → 真っ白."),
        new("doublets", "Verb and adjective variants",
            "Alternative forms of a verb or adjective with the same or a closely related meaning: 感じる / 感ずる, 済ませる / 済ます, 良い / 良き.")
    ];

    /// <summary>Categories the builder generates and users can enable; the rest need curated data, so rows
    /// carrying them stay dormant.</summary>
    public static readonly IReadOnlyList<DerivationCategoryInfo> Shipped =
    [
        new(DerivationCategory.SaNominal, "sa_i_adj", "nominalisation", "い-adjective → さ noun",
            "強い", "強さ", "Turns an adjective into a noun naming its degree."),
        new(DerivationCategory.NaSaNominal, "na_sa", "nominalisation", "な-adjective → さ noun",
            "便利", "便利さ", "Turns a na-adjective into a noun naming its degree."),
        new(DerivationCategory.MiNominal, "mi_nominal", "nominalisation", "い-adjective → み noun",
            "深い", "深み", "Turns an adjective into a noun naming the quality itself, not its degree."),
        new(DerivationCategory.MasuStemNoun, "masu_stem_noun", "masu_stem", "verb stem → noun",
            "動く", "動き", "The polite-form stem of a verb used as a noun."),
        new(DerivationCategory.KuAdverb, "ku_adverb", "adverbs", "い-adjective → く adverb",
            "詳しい", "詳しく", "The adverbial form of an い-adjective."),
        new(DerivationCategory.NiAdverb, "ni_adverb", "adverbs", "な-adjective → に adverb",
            "静か", "静かに", "The adverbial form of a na-adjective."),
        new(DerivationCategory.TeAdverb, "te_form_adverb", "adverbs", "verb て-form → adverb",
            "極める", "極めて", "A verb's て-form lexicalised as an adverb."),
        new(DerivationCategory.Potential, "potential", "potential", "verb → potential",
            "読む", "読める", "The \"can do\" form of a verb."),
        new(DerivationCategory.LexicalPassive, "lexical_passive", "potential", "verb → passive",
            "見る", "見られる", "A passive form that earned its own dictionary entry."),
        new(DerivationCategory.GeAdjective, "ge_i_adj", "appearance", "い-adjective → げ",
            "怪しい", "怪しげ", "\"Looking/seeming ~\" from an adjective."),
        new(DerivationCategory.Sou, "sou_i_adj", "appearance", "い-adjective → そう",
            "眠い", "眠そう", "\"Looks ~\" from an adjective."),
        new(DerivationCategory.Ppoi, "ppoi", "appearance", "〜っぽい",
            "安い", "安っぽい", "\"~ish, has a touch of ~\"."),
        new(DerivationCategory.Garu, "garu_both", "garu", "〜がる / 〜がり",
            "怖い", "怖がる", "Showing signs of a feeling, and the person who habitually does."),
        new(DerivationCategory.Gari, "gari", "garu", "〜がり noun",
            "寒い", "寒がり", "Someone who habitually feels a certain way."),
        new(DerivationCategory.MeModerate, "me", "degree", "〜め",
            "多い", "多め", "\"On the ~ side\", a moderate degree."),
        new(DerivationCategory.Sugiru, "sugiru", "degree", "〜すぎる / 〜すぎ",
            "食べる", "食べ過ぎる", "Doing something to excess."),
        new(DerivationCategory.Gachi, "gachi", "tendency", "〜がち",
            "遅れる", "遅れがち", "Prone to, tends to."),
        new(DerivationCategory.Gimi, "gimi", "tendency", "〜気味",
            "風邪", "風邪気味", "A touch of, slightly."),
        new(DerivationCategory.HonorificPrefix, "honorific_o_go", "honorific", "お〜 / ご〜",
            "茶", "お茶", "The polite prefix on a noun."),
        new(DerivationCategory.MaIntensifier, "matsu_intensifier", "intensifier", "真っ〜",
            "白い", "真っ白", "\"Pure, dead, completely ~\"."),
        new(DerivationCategory.CausativeDoublet, "causative_doublet", "doublets", "せる / す variants",
            "済ませる", "済ます", "The same verb with two interchangeable endings."),
        new(DerivationCategory.ZuruJiru, "zuru_jiru", "doublets", "じる / ずる variants",
            "感じる", "感ずる", "The modern and classical endings of the same verb."),
        new(DerivationCategory.ClassicalAdjective, "classical_adj", "doublets", "classical き forms",
            "良い", "良き", "The literary form of an adjective.")
    ];

    private static readonly Dictionary<string, DerivationCategory> ByKey =
        AllKeys().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<DerivationCategory, string> KeyByCategory =
        AllKeys().ToDictionary(p => p.Value, p => p.Key);

    public static readonly IReadOnlyList<DerivationCategory> ShippedCategories =
        Shipped.Select(c => c.Category).ToList();

    public static bool TryParseKey(string key, out DerivationCategory category) => ByKey.TryGetValue(key, out category);

    public static string GetKey(DerivationCategory category) => KeyByCategory[category];

    private static IEnumerable<KeyValuePair<string, DerivationCategory>> AllKeys()
    {
        foreach (var info in Shipped)
            yield return new(info.Key, info.Category);

        yield return new("transitivity_pair", DerivationCategory.TransitivityPair);
        yield return new("reduplication", DerivationCategory.Reduplication);
        yield return new("particle_elision", DerivationCategory.ParticleElision);
    }
}
