using System.Diagnostics;
using Jiten.Core.Data;

namespace Jiten.Parser;

// Declarative token-rewrite engine. A rule matches a run of 1–3 consecutive tokens by pattern (plus
// optional prev/next/window context and a lookup guard) and rewrites it into 1–N output tokens built
// from templates. It replaces the accretion of one-off if-blocks (かって, からだ, 思いで, ツバ, あ+いつも…)
// that each hand-rolled the same boilerplate — offset arithmetic, reading updates (often forgotten),
// WordInfo cloning, pin/conjugation recovery — none of it discoverable or diffable as data.
//
// The engine owns that boilerplate once: offsets recomputed from template text lengths, readings taken
// from templates (so a re-cut head can never keep a stale reading), clone-with-PreMatched-reset, and
// conjugation recovery. Rules live in RewriteRulesTable; the table is indexed by first-token surface
// for a single dictionary probe per token.

internal enum RewritePhase
{
    // Runs where ProcessSpecialCases sits (before the combine stages).
    Early,
    // Runs after RepairQuotativeTte/RecombineHiraganaTokens, where the mora-theft repairs live.
    Late,
    // Runs immediately before FilterMisparse (surface-keyed disambiguation pins near the pipeline end).
    Cleanup,
    // Runs with FixReadingAmbiguity (reading/pin-only 1:1 remaps).
    Reading,
}

internal enum LookupGuardKind
{
    CompoundExists,        // HasCompoundLookup(expanded)
    NonNameCompoundExists, // HasNonNameCompoundLookup(expanded)
    CompoundAbsent,        // !HasCompoundLookup(expanded) — theft repairs gate on the whole surface NOT being a word
    FrequencyRankUnder,    // GetNonNameCompoundFrequencyRank(expanded) < Rank
}

// One matched token. Text/TextAnyOf are indexable; StartsWith/EndsWith are residual (scanned per token,
// use sparingly). A null constraint means "any". RequireUnpinned keeps a rule from overriding a pin an
// earlier stage already committed.
internal sealed record TokenPattern(
    string? Text = null,
    string[]? TextAnyOf = null,
    string? TextStartsWith = null,
    string? TextEndsWith = null,
    PartOfSpeech[]? Pos = null,
    string[]? DictFormAnyOf = null,
    string? ReadingPrefix = null,
    string? NotReadingPrefix = null,
    bool RequireUnpinned = true);

// One output token. Text "" keeps the matched surface (single-token pins). Reading is REQUIRED for
// splits/merges (the engine cannot infer it) and optional for 1:1 rewrites (null = keep). Pin sets
// PreMatchedWordId; RecoverConjugations asks the engine to run PinnedConjugationProcess for the pin.
internal sealed record TokenTemplate(
    string Text,
    string? DictForm = null,
    string? NormalizedForm = null,
    PartOfSpeech? Pos = null,
    string? Reading = null,
    int? Pin = null,
    byte? PinReadingIndex = null,
    // A hard pin is a final word decision: lookup-time compound matching must not swallow the
    // token into a longer attested span (the same gate protects hand-placed hard pins).
    bool HardPin = false,
    bool RecoverConjugations = false);

// A neighbour (prev/next) guard. All specified constraints must hold (AND); Negate flips the result.
// ClauseBoundary matches a Symbol/SupplementarySymbol/BlankSpace neighbour or a list edge.
internal sealed record ContextCond(
    string[]? TextAnyOf = null,
    string[]? TextEndsWithAnyOf = null,
    string[]? TextStartsWithAnyOf = null,
    PartOfSpeech[]? PosAnyOf = null,
    bool ClauseBoundary = false,
    bool Negate = false);

// A window guard: some token within the relative index range [From, To] of the match start satisfies
// the constraints (e.g. 帽子 within a few tokens of ツバ marks the hat-brim sense). Negate flips.
internal sealed record WindowCond(
    int From,
    int To,
    string[]? TextAnyOf = null,
    PartOfSpeech[]? PosAnyOf = null,
    bool Negate = false);

// A lookup guard over a template expanded across the matched surfaces: "{0}{1}" = concat of matched
// surfaces, "貸りる" = literal, "{0}い" = clipped-adjective style. Closed vocabulary, no lambdas — rules
// stay serializable and the engine stays the only code path.
internal sealed record LookupGuard(LookupGuardKind Kind, string Pattern, int? Rank = null);

internal sealed record RewriteRule(
    string Id,
    RewritePhase Phase,
    TokenPattern[] Match,
    TokenTemplate[] Replace,
    ContextCond? Prev = null,
    ContextCond? Next = null,
    WindowCond? Window = null,
    LookupGuard? Guard = null);

public partial class MorphologicalAnalyser
{
    // Surface-keyed disambiguation pins migrated out of FilterMisparse. Each was a hand-rolled if-block
    // that pinned a WordId (and often fixed POS/DictForm/reading) for a specific surface. They run in the
    // Cleanup phase, immediately before the remaining FilterMisparse logic — same pipeline position, so
    // behaviour is unchanged. Context-heavy blocks (ツバ, くれ, きった, する-family, 方々, ばっか, the
    // name-strip and boundary-theft re-cuts) stay in FilterMisparse: their conditions don't reduce to the
    // declarative schema.
    private static readonly RewriteRule[] RewriteRulesTable =
    [
        // なんなん (colloquial "what the hell?", 2871194), unless followed by と (喃々と taru-adverb).
        new RewriteRule("nannan", RewritePhase.Cleanup,
            [new TokenPattern(Text: "なんなん", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "なんなん", Pos: PartOfSpeech.Expression, Pin: 2871194)],
            Next: new ContextCond(TextAnyOf: ["と"], Negate: true)),

        // クズ = 屑 "scum" (1246510), not 葛 "arrowroot".
        new RewriteRule("kuzu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "クズ", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1246510)]),

        // あるある = the "I can relate" expression (2150380), not doubled ある.
        new RewriteRule("aruaru", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["あるある", "アルアル"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "あるある", NormalizedForm: "あるある", Pos: PartOfSpeech.Interjection, Pin: 2150380)]),

        // 事 read ゴト after a noun/verb stem is the suffix ごと (2613010), not the noun こと.
        new RewriteRule("goto-suffix", RewritePhase.Cleanup,
            [new TokenPattern(Text: "事", ReadingPrefix: "ゴト", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 2613010)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.Verb])),

        // ナシ = the negation なし (1529560), not the pear 梨.
        new RewriteRule("nashi", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ナシ",
                Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.NounSuffix, PartOfSpeech.Suffix],
                RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "なし", NormalizedForm: "なし", Pin: 1529560)]),

        // Bare す stem before explanatory ん is the contracted する (すんだ = するんだ, 1157170).
        // Sudachi already lemmatises it as する, but the one-mora surface cannot resolve there on
        // its own and would otherwise match a junk noun (酢/素/巣). Hard: compound matching would
        // re-fuse す+んだ into the attested すんだ (済んだ).
        new RewriteRule("su-contracted-suru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "す", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["する", "為る"])],
            [new TokenTemplate("", Pin: 1157170, PinReadingIndex: 1, HardPin: true)],
            Next: new ContextCond(TextAnyOf: ["ん", "んだ", "んで"])),

        // Sudachi sometimes keeps the contraction fused as すん (すんの, すんな) and lemmatises it
        // as する itself; unpinned, the surface can only resolve through 済む/住む lookalikes.
        new RewriteRule("sun-contracted-suru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "すん", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["する", "為る"])],
            [new TokenTemplate("", Pin: 1157170, PinReadingIndex: 1, HardPin: true)]),

        // Bare あ stem before explanatory ん is the contracted ある (あんだ = あるんだ, 1296400).
        // Hard: compound matching would re-fuse あ+んだ into the attested あんだ (安打).
        new RewriteRule("a-contracted-aru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "あ", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["ある", "有る", "在る"])],
            [new TokenTemplate("", Pin: 1296400, PinReadingIndex: 2, HardPin: true)],
            Next: new ContextCond(TextAnyOf: ["ん", "んだ", "んで"])),

        // The same contraction fused by Sudachi as あん (あんの, あんだろ), lemmatised as ある.
        new RewriteRule("an-contracted-aru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "あん", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["ある", "有る", "在る"])],
            [new TokenTemplate("", Pin: 1296400, PinReadingIndex: 2, HardPin: true)]),

        // カンパン = the food 乾パン (1209690), not 肝斑/甲板/乾板.
        new RewriteRule("kanpan", RewritePhase.Cleanup,
            [new TokenPattern(Text: "カンパン", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "乾パン", NormalizedForm: "乾パン", Pin: 1209690)]),

        // Kana した after genitive の is 下 (1184140), not 舌.
        new RewriteRule("shita-shita", RewritePhase.Cleanup,
            [new TokenPattern(Text: "した", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun])],
            [new TokenTemplate("", DictForm: "下", NormalizedForm: "下", Pin: 1184140)],
            Prev: new ContextCond(TextAnyOf: ["の"], PosAnyOf: [PartOfSpeech.Particle])),

        // ならば = the conditional conjunction (1009470), not a form of なる.
        new RewriteRule("naraba", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ならば", DictFormAnyOf: ["なる", "成る", "ならば", "だ"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "ならば", NormalizedForm: "ならば", Pos: PartOfSpeech.Conjunction, Pin: 1009470)]),

        // Elongated だろー = だろう (1928670).
        new RewriteRule("darou", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["だろー", "だろぉ", "だろぉー"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "だろう", NormalizedForm: "だろう", Pin: 1928670)]),

        // つー before a nominaliser/question is the という contraction (1922760).
        new RewriteRule("tsuu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "つー", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "という", NormalizedForm: "という", Pos: PartOfSpeech.Particle, Pin: 1922760)],
            Next: new ContextCond(TextAnyOf: ["の", "か", "こと", "わけ"])),

        // 向い* lemmatised as 向く is 向く (1277080), not 向かう.
        new RewriteRule("mukai", RewritePhase.Cleanup,
            [new TokenPattern(TextStartsWith: "向い", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["向く"])],
            [new TokenTemplate("", DictForm: "向く", NormalizedForm: "向く", Pin: 1277080, RecoverConjugations: true)]),

        // 共 (noun とも) + に is the adverb 共に "together" (1234260); Sudachi leaves the two split.
        new RewriteRule("tomoni", RewritePhase.Cleanup,
            [new TokenPattern(Text: "共", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], ReadingPrefix: "トモ", RequireUnpinned: false),
             new TokenPattern(Text: "に", Pos: [PartOfSpeech.Particle], RequireUnpinned: false)],
            [new TokenTemplate("共に", DictForm: "共に", NormalizedForm: "共に", Pos: PartOfSpeech.Adverb, Reading: "トモニ", Pin: 1234260)]),

        // 来 (来る) + the classical adnominal auxiliary たる before a noun is 来たる "coming/next"
        // (1591270, きたる), not 来る taken literally with a たる aux.
        new RewriteRule("kitaru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "来", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["来る"], RequireUnpinned: false),
             new TokenPattern(Text: "たる", Pos: [PartOfSpeech.Auxiliary], RequireUnpinned: false)],
            [new TokenTemplate("来たる", DictForm: "来たる", NormalizedForm: "来たる", Pos: PartOfSpeech.PrenounAdjectival, Reading: "キタル", Pin: 1591270)],
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun])),

        // Demonstrative adverb + した (する past) before a noun is the prenominal adnominal
        // (そうした/こうした/ああした "such", 2008650/2008030/2085100), never the archaic 下物 that a
        // した+もの compound merge would otherwise produce. The Next-noun guard keeps the verbal
        // past (そうしたら, そうしたの?) out.
        new RewriteRule("soushita", RewritePhase.Cleanup,
            [new TokenPattern(Text: "そう", Pos: [PartOfSpeech.Adverb], RequireUnpinned: false),
             new TokenPattern(Text: "した", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["する", "為る"], RequireUnpinned: false)],
            [new TokenTemplate("そうした", DictForm: "そうした", NormalizedForm: "そうした", Pos: PartOfSpeech.PrenounAdjectival, Reading: "ソウシタ", Pin: 2008650)],
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun])),
        new RewriteRule("koushita", RewritePhase.Cleanup,
            [new TokenPattern(Text: "こう", Pos: [PartOfSpeech.Adverb], RequireUnpinned: false),
             new TokenPattern(Text: "した", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["する", "為る"], RequireUnpinned: false)],
            [new TokenTemplate("こうした", DictForm: "こうした", NormalizedForm: "こうした", Pos: PartOfSpeech.PrenounAdjectival, Reading: "コウシタ", Pin: 2008030)],
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun])),
        new RewriteRule("aashita", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ああ", Pos: [PartOfSpeech.Adverb], RequireUnpinned: false),
             new TokenPattern(Text: "した", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["する", "為る"], RequireUnpinned: false)],
            [new TokenTemplate("ああした", DictForm: "ああした", NormalizedForm: "ああした", Pos: PartOfSpeech.PrenounAdjectival, Reading: "アアシタ", Pin: 2085100)],
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun])),

        // ほくそ笑む (2065260, to gloat): Sudachi has no entry and shreds it to ほく+そ+笑(+む);
        // the mora-theft repair reforms 笑む, then this Late rule reassembles the whole verb before
        // the short-kana filter would drop ほく/そ.
        new RewriteRule("hokusoemu", RewritePhase.Late,
            [new TokenPattern(Text: "ほく", Pos: [PartOfSpeech.Adverb], RequireUnpinned: false),
             new TokenPattern(Text: "そ", RequireUnpinned: false),
             new TokenPattern(Text: "笑む", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["笑む"], RequireUnpinned: false)],
            [new TokenTemplate("ほくそ笑む", DictForm: "ほくそ笑む", NormalizedForm: "ほくそ笑む", Pos: PartOfSpeech.Verb, Reading: "ホクソエム", Pin: 2065260, RecoverConjugations: true)]),

        // 合 after 死 is the 合い suffix あい (死合 = しあい, a duel), not the volume unit ごう.
        new RewriteRule("shiai-ai", RewritePhase.Cleanup,
            [new TokenPattern(Text: "合", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "合い", NormalizedForm: "合い", Reading: "アイ", Pin: 1284320)],
            Prev: new ContextCond(TextAnyOf: ["死"])),

        // Bare 有り得 at a clause end is the entry 有り得 (2560320), not the verb 有り得る it deconjugates to.
        new RewriteRule("ariu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "有り得", Pos: [PartOfSpeech.Verb], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "有り得", NormalizedForm: "有り得", Pin: 2560320)],
            Next: new ContextCond(ClauseBoundary: true)),

        // 飛ばし after 首 is 飛ばす "to send flying" (1485230), not the securities-fraud noun 飛ばし (1637130).
        new RewriteRule("kubi-tobashi", RewritePhase.Cleanup,
            [new TokenPattern(Text: "飛ばし", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], DictFormAnyOf: ["飛ばし"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "飛ばす", NormalizedForm: "飛ばす", Pos: PartOfSpeech.Verb, Reading: "トバシ", Pin: 1485230, RecoverConjugations: true)],
            Prev: new ContextCond(TextAnyOf: ["首"])),

        // 羽馬(surname)+車 mis-latticed a winged carriage; re-cut to 羽 + 馬車 (1471780). The exact
        // two-token surface avoids treating unmarked character names as injectable fragments.
        new RewriteRule("hane-basha", RewritePhase.Cleanup,
            [new TokenPattern(Text: "羽馬", RequireUnpinned: false),
             new TokenPattern(Text: "車", RequireUnpinned: false)],
            [new TokenTemplate("羽", DictForm: "羽", NormalizedForm: "羽", Pos: PartOfSpeech.Noun, Reading: "ハネ", Pin: 1171680),
             new TokenTemplate("馬車", DictForm: "馬車", NormalizedForm: "馬車", Pos: PartOfSpeech.Noun, Reading: "バシャ", Pin: 1471780)]),

        // ケダ(surname)+モノ作り mis-latticed 獣作り; re-cut to ケダモノ (獣, 1335590) + 作り. (ケダモノ alone
        // resolves correctly; only the モノ作り fusion strands ケダ on the surname.)
        new RewriteRule("kedamono", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ケダ", RequireUnpinned: false),
             new TokenPattern(Text: "モノ作り", RequireUnpinned: false)],
            [new TokenTemplate("ケダモノ", DictForm: "獣", NormalizedForm: "獣", Pos: PartOfSpeech.Noun, Reading: "ケダモノ", Pin: 1335590),
             new TokenTemplate("作り", DictForm: "作り", NormalizedForm: "作り", Pos: PartOfSpeech.Noun, Reading: "ツクリ", Pin: 1297250)]),

        // 虚(そら)+けど+も after a demonstrative is 虚け (うつけ "fool", 2674470) + the plural suffix ども.
        new RewriteRule("utsuke-domo", RewritePhase.Cleanup,
            [new TokenPattern(Text: "虚", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false),
             new TokenPattern(Text: "けど", Pos: [PartOfSpeech.Particle], RequireUnpinned: false),
             new TokenPattern(Text: "も", Pos: [PartOfSpeech.Particle], RequireUnpinned: false)],
            [new TokenTemplate("虚け", DictForm: "虚け", NormalizedForm: "虚け", Pos: PartOfSpeech.Noun, Reading: "ウツケ", Pin: 2674470),
             new TokenTemplate("ども", DictForm: "ども", NormalizedForm: "共", Pos: PartOfSpeech.Suffix, Reading: "ドモ")],
            Prev: new ContextCond(TextAnyOf: ["この", "その", "あの", "こんな", "そんな", "あんな"])),

        // こった after an adjective is the ことだ contraction ("いいこった" = いいことだ) — resolve it
        // to its own colloquial expression entry (2106260), not the verb 凝る it otherwise matches.
        new RewriteRule("kotta-kotoda", RewritePhase.Cleanup,
            [new TokenPattern(Text: "こった", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "こった", NormalizedForm: "こった", Pos: PartOfSpeech.Expression, Reading: "コッタ", Pin: 2106260, PinReadingIndex: 0)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.IAdjective])),

        // ざまあみやがれ: the あ of the interjection ざまあ is stolen by the following 見 (み), which
        // Sudachi then reads as あみ (編む). Repair only the theft — recut ざま+あみ to ざまあ + み (見る) —
        // and leave やがれ untouched: the vulgar auxiliary やがる is its own token (1012740) after any
        // verb renyoukei (死に|やがれ, し|やがる), so 見|やがれ falls out of the existing machinery.
        new RewriteRule("zamaa-mi", RewritePhase.Late,
            [new TokenPattern(Text: "ざま", RequireUnpinned: false),
             new TokenPattern(Text: "あみ", RequireUnpinned: false)],
            [new TokenTemplate("ざまあ", DictForm: "ざま", NormalizedForm: "ざま", Pos: PartOfSpeech.Noun, Reading: "ザマー", Pin: 1410750, PinReadingIndex: 2),
             new TokenTemplate("み", DictForm: "みる", NormalizedForm: "みる", Pos: PartOfSpeech.Verb, Reading: "ミ", Pin: 1259290, RecoverConjugations: true)]),

        // Katakana イイ is a stylistic spelling of the adjective いい — never the イラン・イラク
        // abbreviation, which otherwise wins on exact surface match.
        new RewriteRule("ii-katakana", RewritePhase.Cleanup,
            [new TokenPattern(Text: "イイ")],
            [new TokenTemplate("", DictForm: "いい", NormalizedForm: "いい", Pos: PartOfSpeech.IAdjective,
                Reading: "イイ", Pin: 2820690, PinReadingIndex: 0)]),

        // 被っ* with an abstract-damage object nearby is こうむる "to suffer/incur" (損失を被った);
        // the clothing かぶる keeps everything else.
        new RewriteRule("koumutta", RewritePhase.Cleanup,
            [new TokenPattern(TextStartsWith: "被っ", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["被る"])],
            [new TokenTemplate("", DictForm: "被る", NormalizedForm: "被る", Pin: 1484340, RecoverConjugations: true)],
            Window: new WindowCond(-4, -1, TextAnyOf: ["損失", "被害", "損害", "迷惑", "ダメージ", "罰", "不利益"])),

        // あんた = the colloquial pronoun "you" (1979920), not the past of 編む.
        new RewriteRule("anta", RewritePhase.Cleanup,
            [new TokenPattern(Text: "あんた", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "あんた", NormalizedForm: "あんた", Pos: PartOfSpeech.Pronoun, Pin: 1979920)]),

        // うっす = the colloquial greeting (2262630), not 臼/薄.
        new RewriteRule("ussu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "うっす", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "うっす", NormalizedForm: "うっす", Pos: PartOfSpeech.Interjection, Pin: 2262630)]),

        // だっけ = the recollection ending (2131200), not だけ.
        new RewriteRule("dakke", RewritePhase.Cleanup,
            [new TokenPattern(Text: "だっけ", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "だっけ", Pos: PartOfSpeech.Expression, Pin: 2131200)]),

        // いかんせん (如何せん) must not resegment into いかん + せん.
        new RewriteRule("ikansen", RewritePhase.Cleanup,
            [new TokenPattern(Text: "いかんせん", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1919420)]),

        // Prefix-tagged せん is the Kansai negative of する (2844926), not the numeral 千.
        new RewriteRule("sen-neg", RewritePhase.Cleanup,
            [new TokenPattern(Text: "せん", Pos: [PartOfSpeech.Prefix], RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Expression, Pin: 2844926)]),

        // セン not after a numeral is 線 "line" (1391780), not 千.
        new RewriteRule("sen-line", RewritePhase.Cleanup,
            [new TokenPattern(Text: "セン", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1391780)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Numeral], Negate: true)),

        // ノリ = 乗り (1354720), not 海苔.
        new RewriteRule("nori", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ノリ", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1354720)]),

        // 頚木 = kanji variant of 頸木 (くびき/yoke, 1831840), absent from lookups.
        new RewriteRule("kubiki", RewritePhase.Cleanup,
            [new TokenPattern(Text: "頚木", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1831840)]),

        // Drawn-out かあ is the question particle か (2028970), not the noun カア.
        new RewriteRule("kaa", RewritePhase.Cleanup,
            [new TokenPattern(Text: "かあ", Pos: [PartOfSpeech.Particle], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "か", NormalizedForm: "か", Pin: 2028970)]),

        // アリアリ = ありあり "vividly" (2007200), not the currency ariary.
        new RewriteRule("ariari", RewritePhase.Cleanup,
            [new TokenPattern(Text: "アリアリ", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 2007200)]),

        // そうそう = "that's right" (1006640), unless before たる/たり (錚々たる).
        new RewriteRule("sousou", RewritePhase.Cleanup,
            [new TokenPattern(Text: "そうそう", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1006640)],
            Next: new ContextCond(TextAnyOf: ["たる", "たり"], Negate: true)),

        // いとおしい = 愛おしい (2007340), not the archaic verb 射通す.
        new RewriteRule("itooshii", RewritePhase.Cleanup,
            [new TokenPattern(Text: "いとおしい", DictFormAnyOf: ["いとおす", "射通す"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "いとおしい", NormalizedForm: "愛おしい", Pos: PartOfSpeech.IAdjective, Pin: 2007340)]),

        // してみれば/してみりゃ after から is the discourse connective ("from …'s standpoint",
        // 2407670), not the literal する conditional — 勉強をしてみれば keeps the verb.
        new RewriteRule("kara-shitemireba", RewritePhase.Cleanup,
            [new TokenPattern(Text: "してみれば")],
            [new TokenTemplate("", DictForm: "してみれば", Pos: PartOfSpeech.Expression, Pin: 2407670, PinReadingIndex: 1)],
            Prev: new ContextCond(TextAnyOf: ["から"])),
        new RewriteRule("kara-shitemirya", RewritePhase.Cleanup,
            [new TokenPattern(Text: "してみりゃ")],
            [new TokenTemplate("", DictForm: "してみれば", Pos: PartOfSpeech.Expression, Pin: 2407670, PinReadingIndex: 1)],
            Prev: new ContextCond(TextAnyOf: ["から"])),

        // なかれ = the classical negative imperative 勿れ (1535750), not 無い/なし.
        new RewriteRule("nakare", RewritePhase.Cleanup,
            [new TokenPattern(Text: "なかれ", DictFormAnyOf: ["ない", "なし", "無い"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "なかれ", NormalizedForm: "なかれ", Pos: PartOfSpeech.Suffix, Pin: 1535750)]),

        // Clause-initial つって/つった is the という contraction (っつう 2798260) — a quotative needs
        // quoted material before it, while 釣る needs an object; mid-clause 魚をつって keeps the verb.
        new RewriteRule("tsutte-quotative", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["つって", "つった"], DictFormAnyOf: ["釣る", "吊る", "つる"])],
            [new TokenTemplate("", DictForm: "っつう", Pos: PartOfSpeech.Particle, Pin: 2798260)],
            Prev: new ContextCond(ClauseBoundary: true)),

        // っつって/っつった/つーて/っつー carry the っ/ー marks of the という contraction — never
        // つて "connections" or 行く forms.
        new RewriteRule("ttsutte", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["っつって", "っつった", "つーて", "っつー", "っつう"])],
            [new TokenTemplate("", DictForm: "っつう", Pos: PartOfSpeech.Particle, Pin: 2798260)]),

        // --- Re-cuts (splits/merges). Templates carry the correct readings, so the F4 stale-reading
        // class cannot recur. Text is conserved (asserted at load). ---

        // ない's final mora stolen by 行く in front of the という contraction or a quotative
        // (できな|いっ|つー, たまんな|いっ|て): return the い and keep the contraction whole.
        new RewriteRule("nai-ttsuu", RewritePhase.Early,
            [new TokenPattern(Text: "な", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["ない"]),
             new TokenPattern(Text: "いっ", DictFormAnyOf: ["いく", "行く"]),
             new TokenPattern(Text: "つー", DictFormAnyOf: ["つう"])],
            [
                new TokenTemplate("ない", DictForm: "ない", NormalizedForm: "ない", Pos: PartOfSpeech.Auxiliary, Reading: "ナイ"),
                new TokenTemplate("っつー", DictForm: "っつう", NormalizedForm: "っつう", Pos: PartOfSpeech.Particle, Reading: "ッツー", Pin: 2798260),
            ]),
        new RewriteRule("nai-tte-mora", RewritePhase.Early,
            [new TokenPattern(Text: "な", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["ない"]),
             new TokenPattern(Text: "いっ", DictFormAnyOf: ["いく", "行く"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("ない", DictForm: "ない", NormalizedForm: "ない", Pos: PartOfSpeech.Auxiliary, Reading: "ナイ"),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ]),

        // Clause-initial てこ+と is the てこと (ということ) contraction, not the lever 梃子 — Sudachi
        // tags the contraction shape Adverb, the tool Noun.
        new RewriteRule("te-koto", RewritePhase.Early,
            [new TokenPattern(Text: "てこ", Pos: [PartOfSpeech.Adverb]),
             new TokenPattern(Text: "と", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("て", DictForm: "て", NormalizedForm: "て", Pos: PartOfSpeech.Particle, Reading: "テ"),
                new TokenTemplate("こと", DictForm: "こと", NormalizedForm: "こと", Pos: PartOfSpeech.Noun, Reading: "コト"),
            ],
            Prev: new ContextCond(ClauseBoundary: true)),

        // しなきゃ+って: Sudachi reads the きゃ as a scream — な(だ)+きゃっ+て(norm って) is the
        // ければ-contraction なきゃ + quotative って.
        new RewriteRule("nakya-tte", RewritePhase.Early,
            [new TokenPattern(Text: "な", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["だ"]),
             new TokenPattern(Text: "きゃっ", Pos: [PartOfSpeech.Interjection]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("なきゃ", DictForm: "なきゃ", NormalizedForm: "なければ", Pos: PartOfSpeech.Auxiliary, Reading: "ナキャ"),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ]),

        // ずっと+いる shreds as ずっ[ずる]|とい[Aux] — the と belongs to the adverb, the い to いる
        // (ずっといてほしい, ずっといた).
        new RewriteRule("zutto-i", RewritePhase.Early,
            [new TokenPattern(Text: "ずっ", DictFormAnyOf: ["ずる"]),
             new TokenPattern(Text: "とい", Pos: [PartOfSpeech.Auxiliary])],
            [
                new TokenTemplate("ずっと", DictForm: "ずっと", NormalizedForm: "ずっと", Pos: PartOfSpeech.Adverb, Reading: "ズット"),
                new TokenTemplate("い", DictForm: "いる", NormalizedForm: "いる", Pos: PartOfSpeech.Verb, Reading: "イ"),
            ]),

        // じゃ|あっ[ある]|て is the quoted interjection じゃあ + って (「じゃあってなによ」) — ある's
        // common-verb protection keeps the mora-theft repair away, so the split is declared here.
        new RewriteRule("jaa-tte", RewritePhase.Early,
            [new TokenPattern(Text: "じゃ", Pos: [PartOfSpeech.Conjunction]),
             new TokenPattern(Text: "あっ", DictFormAnyOf: ["ある", "会う"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("じゃあ", DictForm: "じゃあ", NormalizedForm: "じゃあ", Pos: PartOfSpeech.Conjunction,
                    Reading: "ジャア", Pin: 1005900),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ]),

        // 連用形+てって at clause end is て + quotative って (顔出してって……); before a continuation
        // (出てってくれ) the てく auxiliary survives.
        new RewriteRule("tette-quotative", RewritePhase.Early,
            [new TokenPattern(Text: "てっ", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["てく"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("て", DictForm: "て", NormalizedForm: "て", Pos: PartOfSpeech.Particle, Reading: "テ"),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ],
            Next: new ContextCond(ClauseBoundary: true)),

        // Sudachi tags the っつ-contraction pieces with dict つう/ちゅう; the leading っ rides on the
        // previous token (かっ|つー, どもっ|つっ|て, えっ|つっ|た). Give the っ back to the contraction
        // before the combine stages fuse the pair into 買う/どもる lookalikes.
        new RewriteRule("ka-ttsuu", RewritePhase.Early,
            [new TokenPattern(Text: "かっ"),
             new TokenPattern(Text: "つー", DictFormAnyOf: ["つう"])],
            [
                new TokenTemplate("か", DictForm: "か", NormalizedForm: "か", Pos: PartOfSpeech.Particle, Reading: "カ"),
                new TokenTemplate("っつー", DictForm: "っつう", NormalizedForm: "っつう", Pos: PartOfSpeech.Particle, Reading: "ッツー", Pin: 2798260),
            ]),
        new RewriteRule("ka-cchuu", RewritePhase.Early,
            [new TokenPattern(Text: "かっ"),
             new TokenPattern(Text: "ちゅう", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["ちゅう"])],
            [
                new TokenTemplate("か", DictForm: "か", NormalizedForm: "か", Pos: PartOfSpeech.Particle, Reading: "カ"),
                new TokenTemplate("っちゅう", DictForm: "っちゅう", NormalizedForm: "っちゅう", Pos: PartOfSpeech.Conjunction, Reading: "ッチュウ", Pin: 2757620),
            ]),
        // ども stays a Suffix so a preceding noun can reclaim it (子+ども → 子ども); standalone
        // it resolves as the plural suffix, which is the reading these frames carry.
        new RewriteRule("domo-ttsutte", RewritePhase.Early,
            [new TokenPattern(Text: "どもっ", DictFormAnyOf: ["どもる", "吃る"]),
             new TokenPattern(Text: "つっ", DictFormAnyOf: ["つう"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("ども", DictForm: "ども", NormalizedForm: "ども", Pos: PartOfSpeech.Suffix, Reading: "ドモ"),
                new TokenTemplate("っつって", DictForm: "っつう", NormalizedForm: "っつう", Pos: PartOfSpeech.Particle, Reading: "ッツッテ", Pin: 2798260),
            ]),

        // ねえ's stretched え read as a standalone interjection before the contraction
        // (いらねえ|っつった → いらね|えっ|つっ|た): the えっ run is え + っつった.
        new RewriteRule("e-ttsutta", RewritePhase.Early,
            [new TokenPattern(Text: "えっ", Pos: [PartOfSpeech.Interjection]),
             new TokenPattern(Text: "つっ", DictFormAnyOf: ["つう"]),
             new TokenPattern(Text: "た", Pos: [PartOfSpeech.Auxiliary])],
            [
                new TokenTemplate("え", DictForm: "え", NormalizedForm: "え", Pos: PartOfSpeech.Interjection, Reading: "エ"),
                new TokenTemplate("っつった", DictForm: "っつう", NormalizedForm: "っつう", Pos: PartOfSpeech.Particle, Reading: "ッツッタ", Pin: 2798260),
            ]),

        // ずつ+って: the ず arrives as the negative auxiliary, so the function-word gate on the
        // generic rule below can't see the theft — give ずつ (2829645) its つ back.
        new RewriteRule("zutsu-tte", RewritePhase.Early,
            [new TokenPattern(Text: "ず"),
             new TokenPattern(Text: "つっ", DictFormAnyOf: ["つう"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("ずつ", DictForm: "ずつ", NormalizedForm: "ずつ", Pos: PartOfSpeech.Particle, Reading: "ズツ", Pin: 2829645),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ]),

        // Bare つっ(つう)+て/た is the same contraction conjugated (つっても, いらねえっつった).
        // A quotative follows a completed clause, so the previous token must be a boundary or a
        // function word — a content-word fragment before つっ means the つ was stolen from it
        // (待|つっ|て, 撃|つっ|て, ず|つっ|て), repaired elsewhere.
        new RewriteRule("tsutte-raw", RewritePhase.Early,
            [new TokenPattern(Text: "つっ", DictFormAnyOf: ["つう"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [new TokenTemplate("つって", DictForm: "っつう", NormalizedForm: "っつう", Pos: PartOfSpeech.Particle, Reading: "ツッテ", Pin: 2798260)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb, PartOfSpeech.Noun, PartOfSpeech.CommonNoun,
                PartOfSpeech.Name, PartOfSpeech.Pronoun, PartOfSpeech.NaAdjective, PartOfSpeech.Prefix,
                PartOfSpeech.Suffix, PartOfSpeech.Numeral, PartOfSpeech.Counter], Negate: true)),
        new RewriteRule("tsutta-raw", RewritePhase.Early,
            [new TokenPattern(Text: "つっ", DictFormAnyOf: ["つう"]),
             new TokenPattern(Text: "た", Pos: [PartOfSpeech.Auxiliary])],
            [new TokenTemplate("つった", DictForm: "っつう", NormalizedForm: "っつう", Pos: PartOfSpeech.Particle, Reading: "ツッタ", Pin: 2798260)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb, PartOfSpeech.Noun, PartOfSpeech.CommonNoun,
                PartOfSpeech.Name, PartOfSpeech.Pronoun, PartOfSpeech.NaAdjective, PartOfSpeech.Prefix,
                PartOfSpeech.Suffix, PartOfSpeech.Numeral, PartOfSpeech.Counter], Negate: true)),

        // 〜だって before a quote verb is copula だ + quotative って (大袈裟だって言いたい), not the
        // concessive conjunction だって. Cleanup phase: both CombineTte (だっ+て) and the combine group
        // (言い+たい) have finished, so the merged だって is a single token and the following quote verb
        // 言う/思う is settled as one 言いたい/思う — gate the recut on a nominal host and that verb head.
        new RewriteRule("datte-quotative", RewritePhase.Cleanup,
            [new TokenPattern(Text: "だって", RequireUnpinned: false)],
            [new TokenTemplate("だ", DictForm: "だ", NormalizedForm: "だ", Pos: PartOfSpeech.Auxiliary, Reading: "ダ"),
             new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ", Pin: 2086960)],
            // Restricted to a na-adjective predicate: 大袈裟だ/危険だ + って is copula + quotative, and
            // "even exaggerated" is not a reading, so the split is unambiguous. A noun/pronoun + だって is
            // the "even/too" particle far too often to split safely (子供だって思ってる = "even children
            // think", 俺だって = "I too"), so those keep だって whole even before a quote verb.
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.NaAdjective]),
            // The quote-verb class the copula's quotative って attaches to (言う/思う/聞く/考える/感じる).
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Verb], TextStartsWithAnyOf: ["言", "思", "聞", "考", "感"])),

        // Slang ねえ (= ない) before a quotative って: Sudachi shreds it to ね + えっ(interjection) + て,
        // stealing the え. After a verb, reclaim ねえ as the negative and hand て back as って
        // (堪らねえって, 食えねえって); the verb then folds 〜ねえ into the plain negative.
        new RewriteRule("nee-tte", RewritePhase.Early,
            [new TokenPattern(Text: "ね"),
             new TokenPattern(Text: "えっ"),   // Sudachi tags the stolen え as Interjection or Verb by context
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [new TokenTemplate("ねえ", DictForm: "ない", NormalizedForm: "ない", Pos: PartOfSpeech.Auxiliary, Reading: "ネエ"),
             new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ", Pin: 2086960)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb])),

        // 連用形+たっちゅう fused by the lattice into 塔頭: the dialectal という after past た
        // (起こしたっちゅう); the temple noun never follows a bare 連用形.
        new RewriteRule("ta-cchuu", RewritePhase.Early,
            [new TokenPattern(Text: "たっちゅう")],
            [
                new TokenTemplate("た", DictForm: "た", NormalizedForm: "た", Pos: PartOfSpeech.Auxiliary, Reading: "タ"),
                new TokenTemplate("っちゅう", DictForm: "っちゅう", NormalizedForm: "っちゅう", Pos: PartOfSpeech.Conjunction, Reading: "ッチュウ", Pin: 2757620),
            ],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb])),

        // じゃ (conjunction "well then") + a bare interjection あ is the drawn-out conjunction じゃあ
        // (1005900) — a genuine interjection あ after じゃ is set off by punctuation.
        new RewriteRule("jaa", RewritePhase.Cleanup,
            [new TokenPattern(Text: "じゃ", Pos: [PartOfSpeech.Conjunction]),
             new TokenPattern(Text: "あ", Pos: [PartOfSpeech.Interjection])],
            [new TokenTemplate("じゃあ", DictForm: "じゃあ", NormalizedForm: "じゃあ", Pos: PartOfSpeech.Conjunction,
                Reading: "ジャア", Pin: 1005900, PinReadingIndex: 0)]),

        // Copula や + emphatic ばい only exists in Kyushu dialect after a full predicate; directly
        // after a noun the sequence is the i-adjective やばい (1012840) cut by the lattice.
        new RewriteRule("ya-bai", RewritePhase.Cleanup,
            [new TokenPattern(Text: "や", Pos: [PartOfSpeech.Auxiliary]),
             new TokenPattern(Text: "ばい", Pos: [PartOfSpeech.Particle])],
            [new TokenTemplate("やばい", DictForm: "やばい", NormalizedForm: "やばい", Pos: PartOfSpeech.IAdjective,
                Reading: "ヤバイ", Pin: 1012840, PinReadingIndex: 0)]),

        // 何かって is 何か + quotative って, not the adverb かつて.
        new RewriteRule("nani-katte", RewritePhase.Cleanup,
            [new TokenPattern(Text: "かって", RequireUnpinned: false)],
            [
                new TokenTemplate("か", DictForm: "か", NormalizedForm: "か", Pos: PartOfSpeech.Particle, Reading: "カ"),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ],
            Prev: new ContextCond(TextAnyOf: ["何", "誰", "だれ", "なん"])),

        // Kana からだ after a predicate is から + だ "because it is", not the body 体. A case/topic
        // particle right after rules the predicate reading out (there からだ is the body noun).
        new RewriteRule("kara-da", RewritePhase.Cleanup,
            [new TokenPattern(Text: "からだ", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [
                new TokenTemplate("から", DictForm: "から", NormalizedForm: "から", Pos: PartOfSpeech.Particle, Reading: "カラ"),
                new TokenTemplate("だ", DictForm: "だ", NormalizedForm: "だ", Pos: PartOfSpeech.Auxiliary, Reading: "ダ"),
            ],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb, PartOfSpeech.IAdjective, PartOfSpeech.Auxiliary]),
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Particle],
                TextAnyOf: ["を", "が", "に", "は", "も", "の", "で", "へ", "や"], Negate: true)),

        // 思いで after an adjective is 思い + で, not the memory noun 思い出.
        new RewriteRule("omoi-de", RewritePhase.Cleanup,
            [new TokenPattern(Text: "思いで", RequireUnpinned: false)],
            [
                new TokenTemplate("思い", DictForm: "思い", NormalizedForm: "思い", Pos: PartOfSpeech.Noun, Reading: "オモイ"),
                new TokenTemplate("で", DictForm: "で", NormalizedForm: "で", Pos: PartOfSpeech.Particle, Reading: "デ"),
            ],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.IAdjective, PartOfSpeech.Adnominal])),

        // 右手首/左手首 is 右/左 + 手首 "right/left wrist"; the lattice cuts 右手 + 首.
        new RewriteRule("migi-tekubi", RewritePhase.Cleanup,
            [new TokenPattern(Text: "右手首", RequireUnpinned: false)],
            [
                new TokenTemplate("右", DictForm: "右", NormalizedForm: "右", Pos: PartOfSpeech.Noun, Reading: "ミギ"),
                new TokenTemplate("手首", DictForm: "手首", NormalizedForm: "手首", Pos: PartOfSpeech.Noun, Reading: "テクビ", Pin: 1327770),
            ]),
        new RewriteRule("hidari-tekubi", RewritePhase.Cleanup,
            [new TokenPattern(Text: "左手首", RequireUnpinned: false)],
            [
                new TokenTemplate("左", DictForm: "左", NormalizedForm: "左", Pos: PartOfSpeech.Noun, Reading: "ヒダリ"),
                new TokenTemplate("手首", DictForm: "手首", NormalizedForm: "手首", Pos: PartOfSpeech.Noun, Reading: "テクビ", Pin: 1327770),
            ]),

        // 主人格 is 主 + 人格 "primary personality"; the lattice cuts 主人 + 格.
        new RewriteRule("shu-jinkaku", RewritePhase.Cleanup,
            [new TokenPattern(Text: "主人", RequireUnpinned: false), new TokenPattern(Text: "格")],
            [
                new TokenTemplate("主", DictForm: "主", NormalizedForm: "主", Pos: PartOfSpeech.Noun, Reading: "シュ"),
                new TokenTemplate("人格", DictForm: "人格", NormalizedForm: "人格", Pos: PartOfSpeech.Noun, Reading: "ジンカク", Pin: 1366730),
            ]),

        // 存 + 在す is a shredded 存在する (存在すべく); 在す alone is the archaic honorific います.
        new RewriteRule("sonzai-su", RewritePhase.Cleanup,
            [new TokenPattern(Text: "存", RequireUnpinned: false), new TokenPattern(Text: "在す")],
            [
                new TokenTemplate("存在", DictForm: "存在", NormalizedForm: "存在", Pos: PartOfSpeech.Noun, Reading: "ソンザイ"),
                new TokenTemplate("す", DictForm: "する", NormalizedForm: "する", Pos: PartOfSpeech.Verb, Reading: "ス", Pin: 1157170),
            ]),

        // A sentence-final ね(え) can only follow a finite form; after the 仮定形 なら the sequence
        // is the slang negative of 成る (鼻持ちならねえ, 我慢ならねえ). Restore the IAdjective shape
        // Sudachi itself produces for other ねえ negatives, so tail deconjugation and expression
        // matching see 〜ならない. Early phase so the combine stages treat it like any negative.
        new RewriteRule("nara-nee", RewritePhase.Early,
            [new TokenPattern(Text: "なら", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["だ"]),
             new TokenPattern(Text: "ねえ", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("なら", DictForm: "成る", NormalizedForm: "成る", Pos: PartOfSpeech.Verb, Reading: "ナラ"),
                new TokenTemplate("ねえ", DictForm: "ねえ", NormalizedForm: "無い", Pos: PartOfSpeech.IAdjective, Reading: "ネエ"),
            ]),

        // 面さ lemmatised as 面す is the する-verb mizenkei, which is real only before a passive/
        // causative auxiliary; after a noun with no れる/せる continuation the cut is the face
        // suffix 面 (づら) + particle さ. Early phase so the suffix can rejoin its noun downstream.
        new RewriteRule("tsura-sa", RewritePhase.Early,
            [new TokenPattern(Text: "面さ", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["面す", "面する"])],
            [
                new TokenTemplate("面", DictForm: "面", NormalizedForm: "面", Pos: PartOfSpeech.Suffix, Reading: "ヅラ"),
                new TokenTemplate("さ", DictForm: "さ", NormalizedForm: "さ", Pos: PartOfSpeech.Particle, Reading: "サ"),
            ],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun]),
            Next: new ContextCond(TextAnyOf: ["れ", "れる", "れた", "せ", "せる"], Negate: true)),

        // あ (interjection) directly against いつも is the stolen-mora あいつ + も, not an exclamation
        // (a genuine interjection あ is set off by punctuation). Runs in the Late phase, where the
        // mora-theft repairs live — replaces the RepairInterjectionPronounTheft stage.
        new RewriteRule("aitsu-mo", RewritePhase.Late,
            [new TokenPattern(Text: "あ", Pos: [PartOfSpeech.Interjection], RequireUnpinned: false),
             new TokenPattern(Text: "いつも")],
            [
                new TokenTemplate("あいつ", DictForm: "あいつ", NormalizedForm: "あいつ", Pos: PartOfSpeech.Pronoun, Reading: "アイツ"),
                new TokenTemplate("も", DictForm: "も", NormalizedForm: "も", Pos: PartOfSpeech.Particle, Reading: "モ"),
            ]),
    ];

    private sealed class RewriteIndex
    {
        // Rules whose first pattern has an exact surface, keyed by that surface (one probe per token).
        public readonly Dictionary<string, List<RewriteRule>> ByFirstText = new(StringComparer.Ordinal);
        // Rules whose first pattern has no exact surface (StartsWith/EndsWith/POS-only) — scanned.
        public readonly List<RewriteRule> Residual = [];
        public bool IsEmpty => ByFirstText.Count == 0 && Residual.Count == 0;
    }

    private static readonly Dictionary<RewritePhase, RewriteIndex> _rewriteIndex = BuildRewriteIndex(RewriteRulesTable);

    private static Dictionary<RewritePhase, RewriteIndex> BuildRewriteIndex(RewriteRule[] rules)
    {
        ValidateRewriteRules(rules);
        var index = new Dictionary<RewritePhase, RewriteIndex>();
        foreach (var rule in rules)
        {
            if (!index.TryGetValue(rule.Phase, out var bucket))
                index[rule.Phase] = bucket = new RewriteIndex();

            var first = rule.Match[0];
            if (first.Text != null)
                AddToBucket(bucket.ByFirstText, first.Text, rule);
            else if (first.TextAnyOf is { Length: > 0 })
                foreach (var t in first.TextAnyOf)
                    AddToBucket(bucket.ByFirstText, t, rule);
            else
                bucket.Residual.Add(rule);
        }
        return index;
    }

    private static void AddToBucket(Dictionary<string, List<RewriteRule>> map, string key, RewriteRule rule)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(rule);
    }

    // Fails fast (in tests, at type load) on authoring mistakes: duplicate ids, bad arity, and — for
    // fully-literal split/merge rules — surface-text that does not conserve.
    private static void ValidateRewriteRules(RewriteRule[] rules)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!seenIds.Add(rule.Id))
                throw new InvalidOperationException($"Duplicate rewrite rule id '{rule.Id}'.");
            if (rule.Match.Length is < 1 or > 3)
                throw new InvalidOperationException($"Rewrite rule '{rule.Id}' must match 1–3 tokens.");
            if (rule.Replace.Length < 1)
                throw new InvalidOperationException($"Rewrite rule '{rule.Id}' must produce at least one token.");

            // Conservation is only checkable when every matched surface and every replacement surface is
            // a literal; TextAnyOf/StartsWith patterns and Text="" (keep) are verified at runtime instead.
            bool literalMatch = Array.TrueForAll(rule.Match, m => m.Text != null);
            bool literalReplace = Array.TrueForAll(rule.Replace, r => r.Text.Length > 0);
            if (literalMatch && literalReplace)
            {
                string matched = string.Concat(Array.ConvertAll(rule.Match, m => m.Text));
                string produced = string.Concat(Array.ConvertAll(rule.Replace, r => r.Text));
                if (matched != produced)
                    throw new InvalidOperationException(
                        $"Rewrite rule '{rule.Id}' does not conserve surface text: '{matched}' -> '{produced}'.");
            }

            // Splits/merges (more than a single keep-surface output) need an explicit reading per output.
            bool isRewrite = rule.Replace.Length != 1 || rule.Replace[0].Text.Length > 0;
            if (isRewrite)
                foreach (var t in rule.Replace)
                    if (t.Text.Length > 0 && t.Reading == null)
                        throw new InvalidOperationException(
                            $"Rewrite rule '{rule.Id}' output '{t.Text}' must specify a Reading (splits/merges cannot infer it).");
        }
    }

    private List<WordInfo> ApplyTokenRewriteRulesEarly(List<WordInfo> wordInfos) =>
        ApplyTokenRewriteRules(wordInfos, RewritePhase.Early);

    private List<WordInfo> ApplyTokenRewriteRulesLate(List<WordInfo> wordInfos) =>
        ApplyTokenRewriteRules(wordInfos, RewritePhase.Late);

    private List<WordInfo> ApplyTokenRewriteRulesCleanup(List<WordInfo> wordInfos) =>
        ApplyTokenRewriteRules(wordInfos, RewritePhase.Cleanup);

    private List<WordInfo> ApplyTokenRewriteRules(List<WordInfo> wordInfos, RewritePhase phase)
    {
        if (!_rewriteIndex.TryGetValue(phase, out var index) || index.IsEmpty)
            return wordInfos;
        return RunRewriteRules(wordInfos, index);
    }

    // Copy-on-write: returns the same list reference when nothing fires (so the pipeline skips a feature
    // rescan). Rules are tested exact-index-first then residual, each bucket in table order; the first
    // rule that fires at a position consumes its matched span and the scan continues after it.
    private List<WordInfo> RunRewriteRules(List<WordInfo> wordInfos, RewriteIndex index)
    {
        List<WordInfo>? result = null;
        int i = 0;
        while (i < wordInfos.Count)
        {
            var rule = FindFiringRule(wordInfos, i, index);
            if (rule != null)
            {
                result ??= new List<WordInfo>(wordInfos.Take(i));
                result.AddRange(BuildOutputs(rule, wordInfos, i));
                i += rule.Match.Length;
            }
            else
            {
                result?.Add(wordInfos[i]);
                i++;
            }
        }
        return result ?? wordInfos;
    }

    private RewriteRule? FindFiringRule(List<WordInfo> tokens, int i, RewriteIndex index)
    {
        if (index.ByFirstText.TryGetValue(tokens[i].Text, out var exact))
            foreach (var rule in exact)
                if (MatchesRuleAt(rule, tokens, i))
                    return rule;

        foreach (var rule in index.Residual)
            if (MatchesRuleAt(rule, tokens, i))
                return rule;

        return null;
    }

    private bool MatchesRuleAt(RewriteRule rule, List<WordInfo> tokens, int i)
    {
        int len = rule.Match.Length;
        if (i + len > tokens.Count) return false;

        for (int k = 0; k < len; k++)
            if (!MatchesPattern(tokens[i + k], rule.Match[k]))
                return false;

        if (rule.Prev != null && !MatchesContext(i > 0 ? tokens[i - 1] : null, rule.Prev))
            return false;

        int nextIdx = i + len;
        if (rule.Next != null && !MatchesContext(nextIdx < tokens.Count ? tokens[nextIdx] : null, rule.Next))
            return false;

        if (rule.Window != null && !MatchesWindow(tokens, i, rule.Window))
            return false;

        if (rule.Guard != null && !EvaluateGuard(rule.Guard, tokens, i, len))
            return false;

        return true;
    }

    private static bool MatchesPattern(WordInfo token, TokenPattern p)
    {
        if (p.RequireUnpinned && token.PreMatchedWordId != null) return false;
        if (p.Text != null && token.Text != p.Text) return false;
        if (p.TextAnyOf != null && Array.IndexOf(p.TextAnyOf, token.Text) < 0) return false;
        if (p.TextStartsWith != null && !token.Text.StartsWith(p.TextStartsWith, StringComparison.Ordinal)) return false;
        if (p.TextEndsWith != null && !token.Text.EndsWith(p.TextEndsWith, StringComparison.Ordinal)) return false;
        if (p.Pos != null && Array.IndexOf(p.Pos, token.PartOfSpeech) < 0) return false;
        if (p.DictFormAnyOf != null && Array.IndexOf(p.DictFormAnyOf, token.DictionaryForm) < 0) return false;
        if (p.ReadingPrefix != null && !token.Reading.StartsWith(p.ReadingPrefix, StringComparison.Ordinal)) return false;
        if (p.NotReadingPrefix != null && token.Reading.StartsWith(p.NotReadingPrefix, StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool MatchesContext(WordInfo? neighbour, ContextCond cond)
    {
        bool ok = true;
        if (cond.TextAnyOf != null)
            ok &= neighbour != null && Array.IndexOf(cond.TextAnyOf, neighbour.Text) >= 0;
        if (cond.TextEndsWithAnyOf != null)
            ok &= neighbour != null && Array.Exists(cond.TextEndsWithAnyOf,
                s => neighbour.Text.EndsWith(s, StringComparison.Ordinal));
        if (cond.TextStartsWithAnyOf != null)
            ok &= neighbour != null && Array.Exists(cond.TextStartsWithAnyOf,
                s => neighbour.Text.StartsWith(s, StringComparison.Ordinal));
        if (cond.PosAnyOf != null)
            ok &= neighbour != null && Array.IndexOf(cond.PosAnyOf, neighbour.PartOfSpeech) >= 0;
        if (cond.ClauseBoundary)
            ok &= IsClauseBoundary(neighbour);
        return cond.Negate ? !ok : ok;
    }

    private static bool IsClauseBoundary(WordInfo? neighbour) =>
        neighbour == null
        || neighbour.PartOfSpeech is PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace;

    private static bool MatchesWindow(List<WordInfo> tokens, int matchStart, WindowCond cond)
    {
        int from = Math.Max(0, matchStart + cond.From);
        int to = Math.Min(tokens.Count - 1, matchStart + cond.To);
        bool found = false;
        for (int j = from; j <= to; j++)
        {
            var t = tokens[j];
            if ((cond.TextAnyOf == null || Array.IndexOf(cond.TextAnyOf, t.Text) >= 0)
                && (cond.PosAnyOf == null || Array.IndexOf(cond.PosAnyOf, t.PartOfSpeech) >= 0))
            {
                found = true;
                break;
            }
        }
        return cond.Negate ? !found : found;
    }

    private bool EvaluateGuard(LookupGuard guard, List<WordInfo> tokens, int i, int len)
    {
        string expanded = ExpandGuardPattern(guard.Pattern, tokens, i, len);
        return guard.Kind switch
        {
            LookupGuardKind.CompoundExists        => HasCompoundLookup?.Invoke(expanded) == true,
            LookupGuardKind.NonNameCompoundExists => HasNonNameCompoundLookup?.Invoke(expanded) == true,
            LookupGuardKind.CompoundAbsent        => HasCompoundLookup?.Invoke(expanded) != true,
            LookupGuardKind.FrequencyRankUnder    => GetNonNameCompoundFrequencyRank?.Invoke(expanded) is { } r
                                                     && r < (guard.Rank ?? int.MaxValue),
            _ => false,
        };
    }

    // "{k}" expands to the k-th matched surface; any other character is literal ("貸りる", "{0}い").
    private static string ExpandGuardPattern(string pattern, List<WordInfo> tokens, int i, int len)
    {
        if (!pattern.Contains('{')) return pattern;
        var sb = new System.Text.StringBuilder(pattern.Length + 4);
        for (int p = 0; p < pattern.Length; p++)
        {
            if (pattern[p] == '{' && p + 2 < pattern.Length && pattern[p + 2] == '}'
                && pattern[p + 1] is >= '0' and <= '9')
            {
                int idx = pattern[p + 1] - '0';
                if (idx < len) sb.Append(tokens[i + idx].Text);
                p += 2;
            }
            else
            {
                sb.Append(pattern[p]);
            }
        }
        return sb.ToString();
    }

    private List<WordInfo> BuildOutputs(RewriteRule rule, List<WordInfo> tokens, int i)
    {
        int len = rule.Match.Length;
        var matched = tokens.GetRange(i, len);
        int spanStart = matched[0].StartOffset;
        int spanEnd = matched[^1].EndOffset;

        var outputs = new List<WordInfo>(rule.Replace.Length);
        foreach (var t in rule.Replace)
        {
            // For an N:M rewrite, output k inherits flags from matched[k]; a split (1:N) inherits from
            // the single source. min(k, len-1) covers both without special-casing.
            var source = matched[Math.Min(outputs.Count, len - 1)];
            string surface = t.Text.Length == 0 ? source.Text : t.Text;
            bool surfaceChanged = t.Text.Length > 0;

            var w = new WordInfo(source)
            {
                Text = surface,
                // Each field is overridden only when the template specifies it; a 1:1 pin that sets only
                // DictForm therefore leaves NormalizedForm as the source had it (matching the hand blocks).
                DictionaryForm = t.DictForm ?? (surfaceChanged ? surface : source.DictionaryForm),
                NormalizedForm = t.NormalizedForm ?? (surfaceChanged ? surface : source.NormalizedForm),
                PartOfSpeech = t.Pos ?? source.PartOfSpeech,
                Reading = t.Reading ?? source.Reading,
                PreMatchedWordId = t.Pin,
                PreMatchedReadingIndex = t.Pin != null ? t.PinReadingIndex : null,
                PreMatchedCandidateWordIds = null,
                PreMatchedConjugations = null,
                PinnedByRewriteRule = t.Pin != null,
                HardPinned = t.Pin != null && t.HardPin,
            };
            if (t.RecoverConjugations && t.Pin != null)
                w.PreMatchedConjugations = PinnedConjugationProcess(surface, w.DictionaryForm);

            outputs.Add(w);
        }

        AssignOffsets(outputs, spanStart, spanEnd);
        Debug.Assert(string.Concat(matched.Select(m => m.Text)) == string.Concat(outputs.Select(o => o.Text)),
            $"Rewrite rule '{rule.Id}' did not conserve surface text at runtime.");
        return outputs;
    }

    // Tiles the matched span [spanStart, spanEnd] across the outputs by surface length. When the source
    // offsets are unknown (-1), only the outer edges carry the span bounds and interior boundaries stay
    // unknown — mirroring the hand-rolled blocks' `offset >= 0 ? ... : -1` guards.
    private static void AssignOffsets(List<WordInfo> outputs, int spanStart, int spanEnd)
    {
        if (spanStart >= 0)
        {
            int cursor = spanStart;
            foreach (var w in outputs)
            {
                w.StartOffset = cursor;
                cursor += w.Text.Length;
                w.EndOffset = cursor;
            }
        }
        else
        {
            foreach (var w in outputs)
            {
                w.StartOffset = -1;
                w.EndOffset = -1;
            }
            outputs[0].StartOffset = spanStart;
        }
        outputs[^1].EndOffset = spanEnd;
    }

    // Test hook: run an arbitrary rule table over a token list (validated first), bypassing the static
    // table so the engine can be exercised with focused synthetic rules.
    internal List<WordInfo> ApplyRewriteRulesForTesting(List<WordInfo> input, RewriteRule[] rules, RewritePhase phase)
    {
        var index = BuildRewriteIndex(rules).GetValueOrDefault(phase);
        return index == null || index.IsEmpty ? input : RunRewriteRules(input, index);
    }
}
