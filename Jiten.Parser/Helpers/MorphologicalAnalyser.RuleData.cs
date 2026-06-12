using Jiten.Core.Data;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    private static readonly HashSet<(string, string, string, PartOfSpeech?)> SpecialCases3 =
    [
        ("な", "の", "で", PartOfSpeech.Expression),
        ("それ", "で", "も", PartOfSpeech.Conjunction),
        ("なく", "なっ", "た", PartOfSpeech.Verb),
        ("さ", "せ", "て", PartOfSpeech.Verb),
        ("ほう", "が", "いい", PartOfSpeech.Expression),
        ("に", "とっ", "て", PartOfSpeech.Expression),
        ("に", "つい", "て", PartOfSpeech.Expression),
        // ("いそう", "に", "ない") removed: いそうにない has no JMDict entry, tokens should stay split
        ("か", "の", "ように", PartOfSpeech.Expression),
        ("それ", "よ", "か", PartOfSpeech.Expression),
        ("に", "劣ら", "ず", PartOfSpeech.Expression),
        ("しょう", "が", "ねぇ", PartOfSpeech.IAdjective),
        ("しょう", "が", "ねー", PartOfSpeech.IAdjective),
        ("いけす", "か", "ねぇ", PartOfSpeech.IAdjective),
        ("いけす", "か", "ねー", PartOfSpeech.IAdjective),
        ("たら", "し", "たら", PartOfSpeech.Verb),
        ("どう", "あって", "も", PartOfSpeech.Expression),
    ];

    private static readonly HashSet<string> AuxiliaryVerbs =
    [
        "続ける",
        "始める",
        "終わる",
        "終える",
        "出す",
        "かける",
        "いたす",
        "いただく",
        "頂く",
        "する",
        "回る",
    ];

    private static readonly HashSet<string> TeFormSubsidiaryVerbs =
    [
        "あげる", "上げる",
        "くれる", "呉れる",
        "もらう", "貰う",
        "やる",
        "さしあげる", "差し上げる",
        "くださる", "下さる",
        "おく", "置く",
        "みる", "見る",
    ];

    // Te-form auxiliary verbs with full deconjugator support (both dict form and conjugated forms)
    // Used in 3-token combining: verb + て/で + aux
    // Only include verbs that have deconjugator rules so combined tokens get proper conjugation info
    private static readonly HashSet<string> TeFormAuxChainVerbs =
    [
        "いる", "居る",
        "ある", "有る",
        "おく", "置く",
        "しまう", "仕舞う",
        "いく", "行く",
        "くる", "来る",
        "みる", "見る",
    ];

    private static readonly Dictionary<string, string> AuxiliaryVerbStems = new()
                                                                            {
                                                                                { "続ける", "続け" }, { "始める", "始め" }, { "終わる", "終わ" },
                                                                                { "終える", "終え" }, { "出す", "出" }, { "かける", "かけ" },
                                                                                { "回る", "回" }, { "合う", "合" }, { "放つ", "放" },
                                                                            };

    private static readonly HashSet<string> CompoundVerbSplitSuffixes = [..AuxiliaryVerbs, "合う", "放つ"];

    private static readonly HashSet<(string, string, PartOfSpeech?)> SpecialCases2 =
    [
        ("じゃ", "ない", PartOfSpeech.Expression),
        ("だ", "ろう", PartOfSpeech.Auxiliary), // Sudachi shreds そりゃそうだろう into …だ|ろう(蝋)
        ("す", "べき", PartOfSpeech.Expression), // classical す + べき → すべき 1006200 (とすべき戦術)
        ("なさ", "すぎる", PartOfSpeech.Verb), // なさ[ない]+すぎる → ない via さすぎる excess deconj
        ("なさ", "すぎ", PartOfSpeech.Verb),
        ("近", "すぎ", PartOfSpeech.Verb), // adjective stem + すぎ → 近い via excess deconj
        ("近", "すぎる", PartOfSpeech.Verb),
        ("だ", "けど", PartOfSpeech.Conjunction),
        ("だ", "が", PartOfSpeech.Conjunction),
        ("で", "さえ", PartOfSpeech.Expression),
        ("で", "すら", PartOfSpeech.Expression),
        ("と", "いう", PartOfSpeech.Expression),
        ("と", "して", PartOfSpeech.Expression),
        ("と", "か", PartOfSpeech.Conjunction),
        ("だ", "から", PartOfSpeech.Conjunction),
        ("から", "して", PartOfSpeech.Expression),
        ("これ", "まで", PartOfSpeech.Expression),
        ("それ", "も", PartOfSpeech.Conjunction),
        ("それ", "だけ", PartOfSpeech.Noun),
        ("くせ", "に", PartOfSpeech.Conjunction),
        ("なの", "に", PartOfSpeech.Conjunction),
        ("なの", "で", PartOfSpeech.Conjunction),
        ("の", "で", PartOfSpeech.Particle),
        ("誰", "も", PartOfSpeech.Expression),
        ("誰", "か", PartOfSpeech.Expression),
        ("すぐ", "に", PartOfSpeech.Adverb),
        ("なん", "か", PartOfSpeech.Particle),
        ("なん", "て", PartOfSpeech.Particle),
        ("だっ", "た", PartOfSpeech.Expression),
        ("だっ", "たら", PartOfSpeech.Conjunction),
        ("よう", "に", PartOfSpeech.Expression),
        ("ん", "です", PartOfSpeech.Expression),
        ("ん", "だ", PartOfSpeech.Expression),
        ("です", "か", PartOfSpeech.Expression),
        ("し", "て", PartOfSpeech.Verb),
        ("し", "ちゃ", PartOfSpeech.Verb),
        ("何", "の", PartOfSpeech.Pronoun),
        ("カッコ", "いい", PartOfSpeech.IAdjective),
        ("ドキッ", "と", PartOfSpeech.Adverb),
        ("ドキっ", "と", PartOfSpeech.Adverb),
        ("チラッ", "と", PartOfSpeech.Adverb),
        ("チラっ", "と", PartOfSpeech.Adverb),
        ("ニッ", "と", PartOfSpeech.Adverb),
        ("にっ", "と", PartOfSpeech.Adverb),
        ("か", "な", PartOfSpeech.Particle),
        ("わ", "い", PartOfSpeech.Particle),
        ("だ", "い", PartOfSpeech.Particle), // familiar question marker だい (思っただけだい！)
        ("よう", "です", PartOfSpeech.Expression),
        ("何も", "かも", PartOfSpeech.Expression),
        ("何と", "も", PartOfSpeech.Adverb),
        ("何と", "なく", PartOfSpeech.Adverb),
        ("中途", "半端", PartOfSpeech.Noun),
        ("常", "に", PartOfSpeech.Adverb),
        ("なくて", "も", PartOfSpeech.Expression),
        ("なんに", "も", PartOfSpeech.Adverb),
        ("なし", "で", PartOfSpeech.Expression),
        ("なん", "で", PartOfSpeech.Adverb),
        ("に", "とって", PartOfSpeech.Expression),
        ("に", "ついて", PartOfSpeech.Expression),
        ("だ", "って", PartOfSpeech.Conjunction),
        ("どこ", "か", PartOfSpeech.Pronoun),
        ("急", "に", PartOfSpeech.Adverb),
        ("と", "ても", PartOfSpeech.Adverb),
        ("で", "も", PartOfSpeech.Conjunction),
        ("として", "も", PartOfSpeech.Expression),
        ("多", "き", PartOfSpeech.IAdjective),
        ("ぶっ", "た", PartOfSpeech.Suffix),
        ("に", "よる", PartOfSpeech.Expression),
        ("に", "より", PartOfSpeech.Expression),
        ("とっく", "に", PartOfSpeech.Adverb),
        ("おい", "で", PartOfSpeech.Expression),
        ("どうして", "も", PartOfSpeech.Adverb),
        ("か", "も", PartOfSpeech.Particle),
        ("かも", "しれない", PartOfSpeech.Expression),
        ("何に", "も", PartOfSpeech.Adverb),
        ("ま", "だ", PartOfSpeech.Adverb),
        ("いっしょ", "に", PartOfSpeech.Adverb),
        ("と", "言った", PartOfSpeech.Conjunction),
        ("その", "時", PartOfSpeech.Expression),
        ("そう", "いえば", PartOfSpeech.Expression),
        ("出来", "なさそう", PartOfSpeech.Verb),
        ("それ", "相応", PartOfSpeech.NaAdjective),
        ("当", "たった", PartOfSpeech.Verb),
        ("こそ", "あれ", PartOfSpeech.Conjunction),
        ("こう", "やって", PartOfSpeech.Conjunction),
        ("そう", "やって", PartOfSpeech.Conjunction),
        ("って", "ば", PartOfSpeech.Particle),
        ("かく", "も", PartOfSpeech.NominalAdjective),
        ("よう", "さん", PartOfSpeech.NominalAdjective),
        ("と", "する", PartOfSpeech.Expression),
        ("しきり", "と", PartOfSpeech.Adverb),
        ("しきり", "に", PartOfSpeech.Adverb),
        ("ない", "ない", PartOfSpeech.IAdjective),
        ("ビクと", "も", PartOfSpeech.Adverb),
        ("びくと", "も", PartOfSpeech.Adverb),
        ("要する", "に", PartOfSpeech.Expression),
        ("この", "前", PartOfSpeech.Adverb),
        ("ならび", "に", PartOfSpeech.Conjunction),
        ("残虐", "非道", PartOfSpeech.NaAdjective),
        ("一緒", "に", PartOfSpeech.Adverb),
        ("別", "に", PartOfSpeech.Adverb),
        ("先", "に", PartOfSpeech.Adverb),
        ("仮", "に", PartOfSpeech.Adverb),
        ("滅多", "に", PartOfSpeech.Adverb),
        ("何", "で", PartOfSpeech.Adverb),
        ("何", "か", PartOfSpeech.Pronoun),
        ("何", "も", PartOfSpeech.Pronoun),
        ("実", "は", PartOfSpeech.Expression),
        ("後", "で", PartOfSpeech.Expression),
        ("元", "は", PartOfSpeech.Expression),
        ("当", "の", PartOfSpeech.NominalAdjective),
        ("真", "の", PartOfSpeech.NominalAdjective),
        ("大", "の", PartOfSpeech.NominalAdjective),
        ("例", "の", PartOfSpeech.Adverb),
        ("か", "い", PartOfSpeech.Particle),
        ("露", "にして", PartOfSpeech.Expression),
        ("なれ", "ど", PartOfSpeech.Conjunction),
        ("ここ", "ぞ", PartOfSpeech.Expression),
        ("並び", "に", PartOfSpeech.Conjunction),
        ("真っ先", "に", PartOfSpeech.Adverb),
        ("です", "から", PartOfSpeech.Conjunction),
        ("つ", "か", PartOfSpeech.Conjunction),
        ("それ", "じゃ", PartOfSpeech.Conjunction),
        ("を", "以って", PartOfSpeech.Expression),
        ("に", "つれ", PartOfSpeech.Conjunction),
        ("いつまで", "も", PartOfSpeech.Adverb),
        ("だから", "こそ", PartOfSpeech.Expression),
        ("では", "あるまい", PartOfSpeech.Expression),
        ("に", "於いて", PartOfSpeech.Expression),
        // user_dic にせよ only wins the lattice after なる; recombine the に+せよ(為る命令形) cut
        // that Sudachi produces after other verbs (振られるにせよ, 成就するにせよ)
        ("に", "せよ", PartOfSpeech.Expression),
        // Sudachi splits colloquial どっか/どっから as どっ(代名詞)+particle
        ("どっ", "か", PartOfSpeech.Adverb),
        ("どっ", "から", PartOfSpeech.Adverb),
        // CombineTte builds ちゃってぇ with the expressive small-vowel tail; merged back onto なん,
        // the lookup's small-kana strip resolves なんちゃって (matched via CombineFinal, post-tte)
        ("なん", "ちゃってぇ", PartOfSpeech.Expression),
    ];

    private readonly HashSet<char> _sentenceEnders = ['。', '！', '？', '」'];

    private static readonly string _stopToken = "|";
    private static readonly string _batchDelimiter = "|||";

    private static readonly HashSet<string> NCompoundSuffixes =
        ["だ", "です", "じゃ", "なら", "ても", "でも", "だろ", "だろう", "だって", "だけど", "だけ", "だが", "だし", "だから"];

    private static readonly HashSet<string> DaCompoundSuffixes =
        ["が", "けど", "けれど", "けれども", "から", "し", "って", "の"];

    private static readonly HashSet<string> VerbIndicatingAuxiliaries =
        ["られる", "れる", "せる", "させる"];

    private static readonly string[] GodanVerbEndings = ["る", "す", "つ", "く", "ぐ", "む", "ぶ", "ぬ", "う"];

    internal static readonly HashSet<char> GodanVolitionalOKana =
        ['お', 'こ', 'ご', 'そ', 'ぞ', 'と', 'ど', 'の', 'ほ', 'ぼ', 'ぽ', 'も', 'よ', 'ろ'];
}
