using System.Diagnostics;
using Jiten.Core.Data;

namespace Jiten.Parser;

// A rule matches 1-3 tokens (plus prev/next/window context and a lookup guard) and rewrites them from templates.
// The engine owns offsets, readings (a re-cut never keeps a stale one), cloning and conjugation recovery.

internal enum RewritePhase
{
    // Runs where ProcessSpecialCases sits (before the combine stages).
    Early,
    // Runs after RepairQuotativeTte/RecombineHiraganaTokens, where the mora-theft repairs live.
    Late,
    // Runs immediately before ApplyContextPins (surface-keyed disambiguation pins near the pipeline end).
    Cleanup,
    // Runs immediately before FixReadingAmbiguity (reading/pin-only 1:1 remaps).
    Reading,
}

internal enum LookupGuardKind
{
    CompoundExists,        // HasCompoundLookup(expanded)
    NonNameCompoundExists, // HasNonNameCompoundLookup(expanded)
    CompoundAbsent,        // !HasCompoundLookup(expanded); theft repairs gate on the whole surface NOT being a word
    FrequencyRankUnder,    // GetNonNameCompoundFrequencyRank(expanded) < Rank
}

// Text/TextAnyOf are indexed, StartsWith/EndsWith scanned per token; null = any; RequireUnpinned respects earlier pins.
internal sealed record TokenPattern(
    string? Text = null,
    string[]? TextAnyOf = null,
    string? TextStartsWith = null,
    string? TextEndsWith = null,
    PartOfSpeech[]? Pos = null,
    string[]? DictFormAnyOf = null,
    string[]? NormalizedFormAnyOf = null,
    string? ReadingPrefix = null,
    string? NotReadingPrefix = null,
    bool RequireUnpinned = true);

// Text "" keeps the matched surface; Reading is required for splits/merges (null keeps it on 1:1). Pin sets PreMatchedWordId.
internal sealed record TokenTemplate(
    string Text,
    string? DictForm = null,
    string? NormalizedForm = null,
    PartOfSpeech? Pos = null,
    PartOfSpeechSection? PosSection = null,
    string? Reading = null,
    int? Pin = null,
    byte? PinReadingIndex = null,
    // Final word decision: lookup-time compound matching must not swallow the token into a longer span.
    bool HardPin = false,
    bool RecoverConjugations = false);

// Constraints AND, Negate flips; ClauseBoundary = a Symbol/SupplementarySymbol/BlankSpace neighbour or the list edge.
// Numeral also matches Sudachi's 名詞-数詞 nouns (百, ５０), which PosAnyOf: [Numeral] misses.
internal sealed record ContextCond(
    string[]? TextAnyOf = null,
    string[]? TextEndsWithAnyOf = null,
    string[]? TextStartsWithAnyOf = null,
    PartOfSpeech[]? PosAnyOf = null,
    bool ClauseBoundary = false,
    bool Numeral = false,
    bool Negate = false);

// Some token in [From, To] relative to the match start satisfies the constraints (帽子 near ツバ = brim); Negate flips.
internal sealed record WindowCond(
    int From,
    int To,
    string[]? TextAnyOf = null,
    PartOfSpeech[]? PosAnyOf = null,
    bool Negate = false);

// Pattern expands over matched surfaces ("{0}{1}", "{0}い", literal "貸りる"); no lambdas, so rules stay pure data.
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
    // Kana-surface pins stay in ApplyContextPins: a rule pin sets PinnedByRewriteRule, exempting the token from misparse gates.
    private static readonly RewriteRule[] RewriteRulesTable =
    [
        // Colloquial なんなん ("what the hell?"), unless followed by と (喃々と taru-adverb).
        new RewriteRule("nannan", RewritePhase.Cleanup,
            [new TokenPattern(Text: "なんなん", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "なんなん", Pos: PartOfSpeech.Expression, Pin: 2871194)],
            Next: new ContextCond(TextAnyOf: ["と"], Negate: true)),

        // クズ = 屑 "scum", not 葛 "arrowroot".
        new RewriteRule("kuzu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "クズ", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1246510)]),

        // あるある = the "I can relate" expression, not doubled ある.
        new RewriteRule("aruaru", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["あるある", "アルアル"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "あるある", NormalizedForm: "あるある", Pos: PartOfSpeech.Interjection, Pin: 2150380)]),

        // 事 read ゴト after a noun/verb stem is the suffix ごと, not the noun こと.
        new RewriteRule("goto-suffix", RewritePhase.Cleanup,
            [new TokenPattern(Text: "事", ReadingPrefix: "ゴト", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 2613010)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.Verb])),

        // ナシ = the negation なし, not the pear 梨.
        new RewriteRule("nashi", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ナシ",
                Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.NounSuffix, PartOfSpeech.Suffix],
                RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "なし", NormalizedForm: "なし", Pin: 1529560)]),

        // Standalone じゃが is the archaic conjunction (じゃが、…; 理由じゃが), not the potato; じゃがバター stays potato.
        new RewriteRule("jaga-conjunction", RewritePhase.Cleanup,
            [new TokenPattern(Text: "じゃが", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Conjunction, Pin: 2856812)],
            Next: new ContextCond(TextAnyOf: ["バター", "いも", "イモ", "芋"], Negate: true)),

        // Pronoun おら/オラ is 俺; unpinned, katakana オラ's exact-surface match hands it to hola.
        new RewriteRule("ora-pronoun", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["おら", "オラ"], Pos: [PartOfSpeech.Pronoun], RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 2080360, PinReadingIndex: 3)]),

        // す before explanatory ん is contracted する (すんだ), not 酢/素/巣; hard, or す+んだ re-fuses into 済んだ.
        new RewriteRule("su-contracted-suru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "す", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["する", "為る"])],
            [new TokenTemplate("", Pin: 1157170, PinReadingIndex: 1, HardPin: true)],
            Next: new ContextCond(TextAnyOf: ["ん", "んだ", "んで"])),

        // Sudachi's fused すん (すんの, すんな) is する; unpinned it can only resolve to 済む/住む lookalikes.
        new RewriteRule("sun-contracted-suru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "すん", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["する", "為る"])],
            [new TokenTemplate("", Pin: 1157170, PinReadingIndex: 1, HardPin: true)]),

        // あ before explanatory ん is contracted ある (あんだ); hard, or あ+んだ re-fuses into 安打.
        new RewriteRule("a-contracted-aru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "あ", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["ある", "有る", "在る"])],
            [new TokenTemplate("", Pin: 1296400, PinReadingIndex: 2, HardPin: true)],
            Next: new ContextCond(TextAnyOf: ["ん", "んだ", "んで"])),

        // The same contraction fused by Sudachi as あん (あんの, あんだろ), lemmatised as ある.
        new RewriteRule("an-contracted-aru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "あん", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["ある", "有る", "在る"])],
            [new TokenTemplate("", Pin: 1296400, PinReadingIndex: 2, HardPin: true)]),

        // カンパン = the food 乾パン, not 肝斑/甲板/乾板.
        new RewriteRule("kanpan", RewritePhase.Cleanup,
            [new TokenPattern(Text: "カンパン", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "乾パン", NormalizedForm: "乾パン", Pin: 1209690)]),

        // Kana した after genitive の is 下, not 舌.
        new RewriteRule("shita-shita", RewritePhase.Cleanup,
            [new TokenPattern(Text: "した", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun])],
            [new TokenTemplate("", DictForm: "下", NormalizedForm: "下", Pin: 1184140)],
            Prev: new ContextCond(TextAnyOf: ["の"], PosAnyOf: [PartOfSpeech.Particle])),

        // ならば = the conditional conjunction, not a form of なる.
        new RewriteRule("naraba", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ならば", DictFormAnyOf: ["なる", "成る", "ならば", "だ"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "ならば", NormalizedForm: "ならば", Pos: PartOfSpeech.Conjunction, Pin: 1009470)]),

        // Elongated だろー = だろう.
        new RewriteRule("darou", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["だろー", "だろぉ", "だろぉー"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "だろう", NormalizedForm: "だろう", Pin: 1928670)]),

        // つー before a nominaliser/question is the という contraction.
        new RewriteRule("tsuu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "つー", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "という", NormalizedForm: "という", Pos: PartOfSpeech.Particle, Pin: 1922760)],
            Next: new ContextCond(TextAnyOf: ["の", "か", "こと", "わけ"])),

        // 向い* lemmatised as 向く is 向く, not 向かう.
        new RewriteRule("mukai", RewritePhase.Cleanup,
            [new TokenPattern(TextStartsWith: "向い", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["向く"])],
            [new TokenTemplate("", DictForm: "向く", NormalizedForm: "向く", Pin: 1277080, RecoverConjugations: true)]),

        // 共 (noun とも) + に is the adverb 共に "together"; Sudachi leaves the two split.
        new RewriteRule("tomoni", RewritePhase.Cleanup,
            [new TokenPattern(Text: "共", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], ReadingPrefix: "トモ", RequireUnpinned: false),
             new TokenPattern(Text: "に", Pos: [PartOfSpeech.Particle], RequireUnpinned: false)],
            [new TokenTemplate("共に", DictForm: "共に", NormalizedForm: "共に", Pos: PartOfSpeech.Adverb, Reading: "トモニ", Pin: 1234260)]),

        // 来 + adnominal たる before a noun is 来たる (きたる, "coming/next"), not 来る with a たる aux.
        new RewriteRule("kitaru", RewritePhase.Cleanup,
            [new TokenPattern(Text: "来", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["来る"], RequireUnpinned: false),
             new TokenPattern(Text: "たる", Pos: [PartOfSpeech.Auxiliary], RequireUnpinned: false)],
            [new TokenTemplate("来たる", DictForm: "来たる", NormalizedForm: "来たる", Pos: PartOfSpeech.PrenounAdjectival, Reading: "キタル", Pin: 1591270)],
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun])),

        // そうした/こうした/ああした before a noun is the adnominal "such", not した+もの → 下物; verbal そうしたら stays out.
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

        // Sudachi shreds ほくそ笑む; runs Late, after mora-theft reforms 笑む and before the short-kana filter drops ほく/そ.
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

        // Bare 有り得 at a clause end is the entry 有り得, not the verb 有り得る it deconjugates to.
        new RewriteRule("ariu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "有り得", Pos: [PartOfSpeech.Verb], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "有り得", NormalizedForm: "有り得", Pin: 2560320)],
            Next: new ContextCond(ClauseBoundary: true)),

        // 飛ばし after 首 is 飛ばす "to send flying", not the securities-fraud noun 飛ばし (1637130).
        new RewriteRule("kubi-tobashi", RewritePhase.Cleanup,
            [new TokenPattern(Text: "飛ばし", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], DictFormAnyOf: ["飛ばし"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "飛ばす", NormalizedForm: "飛ばす", Pos: PartOfSpeech.Verb, Reading: "トバシ", Pin: 1485230, RecoverConjugations: true)],
            Prev: new ContextCond(TextAnyOf: ["首"])),

        // 羽馬(surname)+車 is a winged carriage (羽 + 馬車); exact two-token match so other names aren't re-cut.
        new RewriteRule("hane-basha", RewritePhase.Cleanup,
            [new TokenPattern(Text: "羽馬", RequireUnpinned: false),
             new TokenPattern(Text: "車", RequireUnpinned: false)],
            [new TokenTemplate("羽", DictForm: "羽", NormalizedForm: "羽", Pos: PartOfSpeech.Noun, Reading: "ハネ", Pin: 1171680),
             new TokenTemplate("馬車", DictForm: "馬車", NormalizedForm: "馬車", Pos: PartOfSpeech.Noun, Reading: "バシャ", Pin: 1471780)]),

        // Sudachi cuts 行かせまい as 行|か|せまい(狭い); no Prev needed since a real 行か、狭い keeps kanji or punctuation.
        new RewriteRule("ikasemai", RewritePhase.Cleanup,
            [new TokenPattern(Text: "行", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun]),
             new TokenPattern(Text: "か", Pos: [PartOfSpeech.Particle]),
             new TokenPattern(Text: "せまい")],
            [new TokenTemplate("行かせまい", DictForm: "行く", NormalizedForm: "行く", Pos: PartOfSpeech.Verb,
                Reading: "イカセマイ", Pin: 1578850, PinReadingIndex: 0, RecoverConjugations: true)]),

        // っこない after a verb stem (行け|っこ|ない); the verb gate spares nominal っこ (慣れっこ, かけっこ).
        new RewriteRule("kkonai", RewritePhase.Cleanup,
            [new TokenPattern(Text: "っこ"),
             new TokenPattern(Text: "ない")],
            [new TokenTemplate("っこない", DictForm: "っこない", NormalizedForm: "っこない", Pos: PartOfSpeech.Expression,
                Reading: "ッコナイ", Pin: 2145640, PinReadingIndex: 0)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb])),

        // Explanatory なんです, matched Early before tail fusion; non-question tails rule out 何, Pronoun hosts stay out.
        new RewriteRule("nandesu", RewritePhase.Early,
            [new TokenPattern(Text: "な"),
             new TokenPattern(Text: "ん"),
             new TokenPattern(Text: "です")],
            [new TokenTemplate("なんです", DictForm: "なんです", NormalizedForm: "なんです", Pos: PartOfSpeech.Expression,
                Reading: "ナンデス", Pin: 2683060, PinReadingIndex: 0)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.Suffix,
                PartOfSpeech.Name, PartOfSpeech.NaAdjective]),
            Next: new ContextCond(TextAnyOf: ["か", "？"], Negate: true)),

        // A question tail is ambiguous (趣味なんですか); a na-adjective host rules out 何 (好き何ですか), a bare noun doesn't.
        new RewriteRule("nandesu-ka-naadj", RewritePhase.Early,
            [new TokenPattern(Text: "な"),
             new TokenPattern(Text: "ん"),
             new TokenPattern(Text: "です")],
            [new TokenTemplate("なんです", DictForm: "なんです", NormalizedForm: "なんです", Pos: PartOfSpeech.Expression,
                Reading: "ナンデス", Pin: 2683060, PinReadingIndex: 0)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.NaAdjective]),
            Next: new ContextCond(TextAnyOf: ["か", "？"])),

        // A genitive の before the host also rules out 何: 俺のせいなんですか ("it's MY fault?!").
        new RewriteRule("nandesu-ka-no", RewritePhase.Early,
            [new TokenPattern(Text: "な"),
             new TokenPattern(Text: "ん"),
             new TokenPattern(Text: "です")],
            [new TokenTemplate("なんです", DictForm: "なんです", NormalizedForm: "なんです", Pos: PartOfSpeech.Expression,
                Reading: "ナンデス", Pin: 2683060, PinReadingIndex: 0)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.Suffix,
                PartOfSpeech.Name]),
            Next: new ContextCond(TextAnyOf: ["か", "？"]),
            Window: new WindowCond(-2, -2, TextAnyOf: ["の"])),

        // Settles 見るも無残 before the na-adjective merge absorbs the connector な.
        new RewriteRule("mirumo-muzan", RewritePhase.Late,
            [new TokenPattern(Text: "見るも"),
             new TokenPattern(Text: "無残")],
            [new TokenTemplate("見るも無残", DictForm: "見るも無残", NormalizedForm: "見るも無残", Pos: PartOfSpeech.Expression,
                Reading: "ミルモムザン", Pin: 2871068, PinReadingIndex: 0)]),
        // Shape where the na-adjective merge already absorbed the connector (無残な).
        new RewriteRule("mirumo-muzan-na", RewritePhase.Late,
            [new TokenPattern(Text: "見るも"),
             new TokenPattern(Text: "無残な")],
            [new TokenTemplate("見るも無残", DictForm: "見るも無残", NormalizedForm: "見るも無残", Pos: PartOfSpeech.Expression,
                Reading: "ミルモムザン", Pin: 2871068, PinReadingIndex: 0),
             new TokenTemplate("な", DictForm: "な", NormalizedForm: "な", Pos: PartOfSpeech.Auxiliary, Reading: "ナ")]),

        // JMnedict's place entry 養護院 outranks noun+院; per-surface since a blanket recut would shred genuine names.
        new RewriteRule("yougoin", RewritePhase.Late,
            [new TokenPattern(Text: "養護院", RequireUnpinned: false)],
            [new TokenTemplate("養護", DictForm: "養護", NormalizedForm: "養護", Pos: PartOfSpeech.Noun,
                Reading: "ヨウゴ", Pin: 1605847, PinReadingIndex: 0, HardPin: true),
             new TokenTemplate("院", DictForm: "院", NormalizedForm: "院", Pos: PartOfSpeech.Noun,
                Reading: "イン", Pin: 2414530, PinReadingIndex: 0, HardPin: true)]),

        // 打ち下ろし is deverbal 打ち下ろす; JMDict's only noun entry is the golf term (downhill hole).
        new RewriteRule("uchioroshi", RewritePhase.Cleanup,
            [new TokenPattern(Text: "打ち下ろし", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "打ち下ろす", NormalizedForm: "打ち下ろす", Pos: PartOfSpeech.Verb,
                Reading: "ウチオロシ", Pin: 1408600, PinReadingIndex: 0, RecoverConjugations: true)]),

        // Sentence-final だい after explanatory ん: the fused んだ strands the い, which then drops.
        new RewriteRule("ndai", RewritePhase.Late,
            [new TokenPattern(Text: "んだ"),
             new TokenPattern(Text: "い", Pos: [PartOfSpeech.Particle])],
            [new TokenTemplate("ん", DictForm: "ん", NormalizedForm: "ん", Pos: PartOfSpeech.Particle, Reading: "ン", Pin: 2139720),
             new TokenTemplate("だい", DictForm: "だい", NormalizedForm: "だい", Pos: PartOfSpeech.Particle,
                Reading: "ダイ", Pin: 2097680, PinReadingIndex: 0)]),

        // Sudachi prefers 上様, so 母|上様 never yields 母上; closed host list since a generic re-cut hits 滑走|路上.
        new RewriteRule("kinship-uesama", RewritePhase.Early,
            [new TokenPattern(TextAnyOf: ["父", "母", "兄", "姉", "祖父", "祖母", "義父", "義母"],
                Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun]),
             new TokenPattern(Text: "上様")],
            [new TokenTemplate("", Pos: PartOfSpeech.Noun),
             new TokenTemplate("上", DictForm: "上", NormalizedForm: "上", Pos: PartOfSpeech.Suffix, Reading: "ウエ"),
             new TokenTemplate("様", DictForm: "様", NormalizedForm: "様", Pos: PartOfSpeech.Suffix,
                Reading: "サマ", Pin: 1545790, PinReadingIndex: 0)]),

        // Rustic くだせえ (slurred 下さい) shreds to くだ+せえ; the deconjugator's slur fold recovers ください.
        new RewriteRule("kudasee", RewritePhase.Late,
            [new TokenPattern(Text: "くだ"),
             new TokenPattern(Text: "せえ")],
            [new TokenTemplate("くだせえ", DictForm: "ください", NormalizedForm: "ください", Pos: PartOfSpeech.Expression,
                Reading: "クダセエ", Pin: 1184270, PinReadingIndex: 1, RecoverConjugations: true)]),

        // Dialect ちょる (=ておる) after a verb; the verb gate spares the interjection ちょっ (ちょっ、待て).
        new RewriteRule("chotta", RewritePhase.Late,
            [new TokenPattern(Text: "ちょっ"),
             new TokenPattern(Text: "た", Pos: [PartOfSpeech.Auxiliary])],
            [new TokenTemplate("ちょった", DictForm: "ちょる", NormalizedForm: "ちょる", Pos: PartOfSpeech.Auxiliary,
                Reading: "チョッタ", Pin: 2869627, PinReadingIndex: 0, RecoverConjugations: true)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb, PartOfSpeech.Auxiliary])),

        // Kansai のうなる ("to disappear"), which Sudachi reads as の + 唸る.
        new RewriteRule("nounatta", RewritePhase.Late,
            [new TokenPattern(Text: "の", Pos: [PartOfSpeech.Particle]),
             new TokenPattern(TextStartsWith: "うなっ", Pos: [PartOfSpeech.Verb]),
             new TokenPattern(Text: "た", Pos: [PartOfSpeech.Auxiliary])],
            [new TokenTemplate("のうなった", DictForm: "のうなる", NormalizedForm: "のうなる", Pos: PartOfSpeech.Verb,
                Reading: "ノウナッタ", Pin: 2793080, PinReadingIndex: 1, RecoverConjugations: true)],
            // の after a nominal/predicate host is genitive (彼のうなった声); elsewhere it opens のうなる (もうのうなった).
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb, PartOfSpeech.Auxiliary, PartOfSpeech.IAdjective,
                PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.Name, PartOfSpeech.Pronoun,
                PartOfSpeech.NaAdjective, PartOfSpeech.Prefix, PartOfSpeech.Suffix, PartOfSpeech.Numeral,
                PartOfSpeech.Counter], Negate: true)),

        // Dialect じゃった (=だった) after nominal content; after a verb it stays the じゃう contraction (飲んじゃった).
        new RewriteRule("jatta", RewritePhase.Late,
            [new TokenPattern(Text: "じゃっ", Pos: [PartOfSpeech.Auxiliary]),
             new TokenPattern(Text: "た", Pos: [PartOfSpeech.Auxiliary])],
            [new TokenTemplate("じゃった", DictForm: "じゃった", NormalizedForm: "じゃった", Pos: PartOfSpeech.Expression,
                Reading: "ジャッタ", Pin: 2850797, PinReadingIndex: 0)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.Name,
                PartOfSpeech.Pronoun, PartOfSpeech.NaAdjective, PartOfSpeech.Suffix])),

        // Sudachi cuts 立とうと as 立|とうと (a rare adverb); recut to volitional 立とう + と.
        new RewriteRule("tatouto", RewritePhase.Cleanup,
            [new TokenPattern(Text: "立", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun, PartOfSpeech.Name]),
             new TokenPattern(Text: "とうと")],
            [new TokenTemplate("立とう", DictForm: "立つ", NormalizedForm: "立つ", Pos: PartOfSpeech.Verb,
                Reading: "タトウ", Pin: 1597040, PinReadingIndex: 0, RecoverConjugations: true),
             new TokenTemplate("と", DictForm: "と", NormalizedForm: "と", Pos: PartOfSpeech.Particle, Reading: "ト")]),

        // ケダ(surname)+モノ作り is ケダモノ (獣) + 作り; bare ケダモノ already resolves.
        new RewriteRule("kedamono", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ケダ", RequireUnpinned: false),
             new TokenPattern(Text: "モノ作り", RequireUnpinned: false)],
            [new TokenTemplate("ケダモノ", DictForm: "獣", NormalizedForm: "獣", Pos: PartOfSpeech.Noun, Reading: "ケダモノ", Pin: 1335590),
             new TokenTemplate("作り", DictForm: "作り", NormalizedForm: "作り", Pos: PartOfSpeech.Noun, Reading: "ツクリ", Pin: 1297250)]),

        // 虚(そら)+けど+も after a demonstrative is 虚け (うつけ "fool") + the plural suffix ども.
        new RewriteRule("utsuke-domo", RewritePhase.Cleanup,
            [new TokenPattern(Text: "虚", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false),
             new TokenPattern(Text: "けど", Pos: [PartOfSpeech.Particle], RequireUnpinned: false),
             new TokenPattern(Text: "も", Pos: [PartOfSpeech.Particle], RequireUnpinned: false)],
            [new TokenTemplate("虚け", DictForm: "虚け", NormalizedForm: "虚け", Pos: PartOfSpeech.Noun, Reading: "ウツケ", Pin: 2674470),
             new TokenTemplate("ども", DictForm: "ども", NormalizedForm: "共", Pos: PartOfSpeech.Suffix, Reading: "ドモ")],
            Prev: new ContextCond(TextAnyOf: ["この", "その", "あの", "こんな", "そんな", "あんな"])),

        // こった after an adjective is the ことだ contraction (いいこった), not 凝る.
        new RewriteRule("kotta-kotoda", RewritePhase.Cleanup,
            [new TokenPattern(Text: "こった", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "こった", NormalizedForm: "こった", Pos: PartOfSpeech.Expression, Reading: "コッタ", Pin: 2106260, PinReadingIndex: 0)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.IAdjective])),

        // Compound matching probes unattested ざまあみやがる, so shredded ざま|あみ|やがれ never reaches its entry.
        new RewriteRule("zamaa-miyagare", RewritePhase.Late,
            [new TokenPattern(Text: "ざま", RequireUnpinned: false),
             new TokenPattern(Text: "あみ", RequireUnpinned: false),
             new TokenPattern(Text: "やがれ", RequireUnpinned: false)],
            [new TokenTemplate("ざまあみやがれ", DictForm: "ざまあみやがれ", NormalizedForm: "ざまあみやがれ",
                Pos: PartOfSpeech.Expression, Reading: "ザマーミヤガレ", Pin: 2868161, PinReadingIndex: 1)]),

        // Katakana イイ is the adjective いい, never the イラン・イラク abbreviation that wins on exact surface.
        new RewriteRule("ii-katakana", RewritePhase.Cleanup,
            [new TokenPattern(Text: "イイ")],
            [new TokenTemplate("", DictForm: "いい", NormalizedForm: "いい", Pos: PartOfSpeech.IAdjective,
                Reading: "イイ", Pin: 2820690, PinReadingIndex: 0)]),

        // 被っ* near an abstract-damage noun is こうむる (損失を被った); the clothing かぶる keeps everything else.
        new RewriteRule("koumutta", RewritePhase.Cleanup,
            [new TokenPattern(TextStartsWith: "被っ", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["被る"])],
            [new TokenTemplate("", DictForm: "被る", NormalizedForm: "被る", Pin: 1484340, RecoverConjugations: true)],
            Window: new WindowCond(-4, -1, TextAnyOf: ["損失", "被害", "損害", "迷惑", "ダメージ", "罰", "不利益"])),

        // あんた = the colloquial pronoun "you", not the past of 編む.
        new RewriteRule("anta", RewritePhase.Cleanup,
            [new TokenPattern(Text: "あんた", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "あんた", NormalizedForm: "あんた", Pos: PartOfSpeech.Pronoun, Pin: 1979920)]),

        // うっす = the colloquial greeting, not 臼/薄.
        new RewriteRule("ussu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "うっす", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "うっす", NormalizedForm: "うっす", Pos: PartOfSpeech.Interjection, Pin: 2262630)]),

        // だっけ = the recollection ending, not だけ.
        new RewriteRule("dakke", RewritePhase.Cleanup,
            [new TokenPattern(Text: "だっけ", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "だっけ", Pos: PartOfSpeech.Expression, Pin: 2131200)]),

        // いかんせん (如何せん) must not resegment into いかん + せん.
        new RewriteRule("ikansen", RewritePhase.Cleanup,
            [new TokenPattern(Text: "いかんせん", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1919420)]),

        // Prefix-tagged せん is the Kansai negative of する, not the numeral 千.
        new RewriteRule("sen-neg", RewritePhase.Cleanup,
            [new TokenPattern(Text: "せん", Pos: [PartOfSpeech.Prefix], RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Expression, Pin: 2844926)]),

        // セン not after a numeral is 線 "line", not 千.
        new RewriteRule("sen-line", RewritePhase.Cleanup,
            [new TokenPattern(Text: "セン", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1391780)],
            Prev: new ContextCond(Numeral: true, Negate: true)),

        // ヤード after a numeral is the unit (碼), not the working-area ヤード.
        new RewriteRule("yard-unit", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ヤード", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1136260, PinReadingIndex: 1)],
            Prev: new ContextCond(Numeral: true)),

        // ノリ = 乗り, not 海苔.
        new RewriteRule("nori", RewritePhase.Cleanup,
            [new TokenPattern(Text: "ノリ", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1354720)]),

        // 頚木 = kanji variant of 頸木 (くびき/yoke), absent from lookups.
        new RewriteRule("kubiki", RewritePhase.Cleanup,
            [new TokenPattern(Text: "頚木", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1831840)]),

        // Drawn-out かあ is the question particle か, not the noun カア.
        new RewriteRule("kaa", RewritePhase.Cleanup,
            [new TokenPattern(Text: "かあ", Pos: [PartOfSpeech.Particle], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "か", NormalizedForm: "か", Pin: 2028970)]),

        // アリアリ = ありあり "vividly", not the currency ariary.
        new RewriteRule("ariari", RewritePhase.Cleanup,
            [new TokenPattern(Text: "アリアリ", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 2007200)]),

        // そうそう = "that's right", unless before たる/たり (錚々たる).
        new RewriteRule("sousou", RewritePhase.Cleanup,
            [new TokenPattern(Text: "そうそう", RequireUnpinned: false)],
            [new TokenTemplate("", Pin: 1006640)],
            Next: new ContextCond(TextAnyOf: ["たる", "たり"], Negate: true)),

        // いとおしい = 愛おしい, not the archaic verb 射通す.
        new RewriteRule("itooshii", RewritePhase.Cleanup,
            [new TokenPattern(Text: "いとおしい", DictFormAnyOf: ["いとおす", "射通す"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "いとおしい", NormalizedForm: "愛おしい", Pos: PartOfSpeech.IAdjective, Pin: 2007340)]),

        // してみれば/してみりゃ after から is the connective "from X's standpoint"; 勉強をしてみれば keeps the verb.
        new RewriteRule("kara-shitemireba", RewritePhase.Cleanup,
            [new TokenPattern(Text: "してみれば")],
            [new TokenTemplate("", DictForm: "してみれば", Pos: PartOfSpeech.Expression, Pin: 2407670, PinReadingIndex: 1)],
            Prev: new ContextCond(TextAnyOf: ["から"])),
        new RewriteRule("kara-shitemirya", RewritePhase.Cleanup,
            [new TokenPattern(Text: "してみりゃ")],
            [new TokenTemplate("", DictForm: "してみれば", Pos: PartOfSpeech.Expression, Pin: 2407670, PinReadingIndex: 1)],
            Prev: new ContextCond(TextAnyOf: ["から"])),

        // なかれ = the classical negative imperative 勿れ, not 無い/なし.
        new RewriteRule("nakare", RewritePhase.Cleanup,
            [new TokenPattern(Text: "なかれ", DictFormAnyOf: ["ない", "なし", "無い"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "なかれ", NormalizedForm: "なかれ", Pos: PartOfSpeech.Suffix, Pin: 1535750)]),

        // Clause-initial つって/つった is the という contraction (釣る needs an object); 魚をつって keeps the verb.
        new RewriteRule("tsutte-quotative", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["つって", "つった"], DictFormAnyOf: ["釣る", "吊る", "つる"])],
            [new TokenTemplate("", DictForm: "っつう", Pos: PartOfSpeech.Particle, Pin: 2798260)],
            Prev: new ContextCond(ClauseBoundary: true)),

        // The っ/ー marks mean the という contraction, never つて "connections" or 行く forms.
        new RewriteRule("ttsutte", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["っつって", "っつった", "つーて", "っつー", "っつう"])],
            [new TokenTemplate("", DictForm: "っつう", Pos: PartOfSpeech.Particle, Pin: 2798260)]),

        // --- Re-cuts (splits/merges): text is conserved, asserted at load. ---

        // 行く steals ない's い before the という contraction or quotative (できな|いっ|つー, たまんな|いっ|て).
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

        // Clause-initial てこ+と is the てこと (ということ) contraction; Sudachi tags it Adverb, the lever 梃子 Noun.
        new RewriteRule("te-koto", RewritePhase.Early,
            [new TokenPattern(Text: "てこ", Pos: [PartOfSpeech.Adverb]),
             new TokenPattern(Text: "と", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("て", DictForm: "て", NormalizedForm: "て", Pos: PartOfSpeech.Particle, Reading: "テ"),
                new TokenTemplate("こと", DictForm: "こと", NormalizedForm: "こと", Pos: PartOfSpeech.Noun, Reading: "コト"),
            ],
            Prev: new ContextCond(ClauseBoundary: true)),

        // しなきゃって: Sudachi reads きゃっ as a scream; it is the ければ-contraction なきゃ + quotative って.
        new RewriteRule("nakya-tte", RewritePhase.Early,
            [new TokenPattern(Text: "な", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["だ"]),
             new TokenPattern(Text: "きゃっ", Pos: [PartOfSpeech.Interjection]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("なきゃ", DictForm: "なきゃ", NormalizedForm: "なければ", Pos: PartOfSpeech.Auxiliary, Reading: "ナキャ"),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ]),

        // ずっと+いる shreds as ずっ[ずる]|とい[Aux] (ずっといた).
        new RewriteRule("zutto-i", RewritePhase.Early,
            [new TokenPattern(Text: "ずっ", DictFormAnyOf: ["ずる"]),
             new TokenPattern(Text: "とい", Pos: [PartOfSpeech.Auxiliary])],
            [
                new TokenTemplate("ずっと", DictForm: "ずっと", NormalizedForm: "ずっと", Pos: PartOfSpeech.Adverb, Reading: "ズット"),
                new TokenTemplate("い", DictForm: "いる", NormalizedForm: "いる", Pos: PartOfSpeech.Verb, Reading: "イ"),
            ]),

        // じゃ|あっ|て is じゃあ + って (じゃあってなによ); ある's common-verb protection blocks the mora-theft repair.
        new RewriteRule("jaa-tte", RewritePhase.Early,
            [new TokenPattern(Text: "じゃ", Pos: [PartOfSpeech.Conjunction]),
             new TokenPattern(Text: "あっ", DictFormAnyOf: ["ある", "会う"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("じゃあ", DictForm: "じゃあ", NormalizedForm: "じゃあ", Pos: PartOfSpeech.Conjunction,
                    Reading: "ジャア", Pin: 1005900),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ]),

        // Clause-final てって is て + quotative って (顔出してって……); 出てってくれ keeps the てく auxiliary.
        new RewriteRule("tette-quotative", RewritePhase.Early,
            [new TokenPattern(Text: "てっ", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["てく"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("て", DictForm: "て", NormalizedForm: "て", Pos: PartOfSpeech.Particle, Reading: "テ"),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ],
            Next: new ContextCond(ClauseBoundary: true)),

        // The contraction's leading っ rides on the previous token (かっ|つー); return it before combines build 買う lookalikes.
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
        // ども stays a Suffix so a preceding noun can reclaim it (子+ども → 子ども).
        new RewriteRule("domo-ttsutte", RewritePhase.Early,
            [new TokenPattern(Text: "どもっ", DictFormAnyOf: ["どもる", "吃る"]),
             new TokenPattern(Text: "つっ", DictFormAnyOf: ["つう"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("ども", DictForm: "ども", NormalizedForm: "ども", Pos: PartOfSpeech.Suffix, Reading: "ドモ"),
                new TokenTemplate("っつって", DictForm: "っつう", NormalizedForm: "っつう", Pos: PartOfSpeech.Particle, Reading: "ッツッテ", Pin: 2798260),
            ]),

        // ねえ's え becomes an interjection before the contraction (いらね|えっ|つっ|た → え + っつった).
        new RewriteRule("e-ttsutta", RewritePhase.Early,
            [new TokenPattern(Text: "えっ", Pos: [PartOfSpeech.Interjection]),
             new TokenPattern(Text: "つっ", DictFormAnyOf: ["つう"]),
             new TokenPattern(Text: "た", Pos: [PartOfSpeech.Auxiliary])],
            [
                new TokenTemplate("え", DictForm: "え", NormalizedForm: "え", Pos: PartOfSpeech.Interjection, Reading: "エ"),
                new TokenTemplate("っつった", DictForm: "っつう", NormalizedForm: "っつう", Pos: PartOfSpeech.Particle, Reading: "ッツッタ", Pin: 2798260),
            ]),

        // ずつって: the ず arrives as the negative auxiliary, which tsutte-raw's function-word gate would accept.
        new RewriteRule("zutsu-tte", RewritePhase.Early,
            [new TokenPattern(Text: "ず"),
             new TokenPattern(Text: "つっ", DictFormAnyOf: ["つう"]),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("ずつ", DictForm: "ずつ", NormalizedForm: "ずつ", Pos: PartOfSpeech.Particle, Reading: "ズツ", Pin: 2829645),
                new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ]),

        // Bare つっ+て/た is the contraction only after a boundary or function word; after content, つ was stolen (待|つっ|て).
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

        // Na-adj + だって + quote verb is copula + quotative (大袈裟だって言いたい); Cleanup, after CombineTte and verb merges.
        new RewriteRule("datte-quotative", RewritePhase.Cleanup,
            [new TokenPattern(Text: "だって", RequireUnpinned: false)],
            [new TokenTemplate("だ", DictForm: "だ", NormalizedForm: "だ", Pos: PartOfSpeech.Auxiliary, Reading: "ダ"),
             new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ", Pin: 2086960)],
            // Noun + だって is too often "even/too" to split (子供だって思ってる, 俺だって).
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.NaAdjective]),
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Verb], TextStartsWithAnyOf: ["言", "思", "聞", "考", "感"])),

        // Slang ねえ before quotative って shreds to ね + えっ + て after a verb (堪らねえって).
        new RewriteRule("nee-tte", RewritePhase.Early,
            [new TokenPattern(Text: "ね"),
             new TokenPattern(Text: "えっ"),   // Sudachi tags the stolen え as Interjection or Verb by context
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle])],
            [new TokenTemplate("ねえ", DictForm: "ない", NormalizedForm: "ない", Pos: PartOfSpeech.Auxiliary, Reading: "ネエ"),
             new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ", Pin: 2086960)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb])),

        // たっちゅう after a verb is past た + dialectal という (起こしたっちゅう), not the temple noun 塔頭.
        new RewriteRule("ta-cchuu", RewritePhase.Early,
            [new TokenPattern(Text: "たっちゅう")],
            [
                new TokenTemplate("た", DictForm: "た", NormalizedForm: "た", Pos: PartOfSpeech.Auxiliary, Reading: "タ"),
                new TokenTemplate("っちゅう", DictForm: "っちゅう", NormalizedForm: "っちゅう", Pos: PartOfSpeech.Conjunction, Reading: "ッチュウ", Pin: 2757620),
            ],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb])),

        // じゃ + bare interjection あ is じゃあ; a genuine interjection あ after じゃ is set off by punctuation.
        new RewriteRule("jaa", RewritePhase.Cleanup,
            [new TokenPattern(Text: "じゃ", Pos: [PartOfSpeech.Conjunction]),
             new TokenPattern(Text: "あ", Pos: [PartOfSpeech.Interjection])],
            [new TokenTemplate("じゃあ", DictForm: "じゃあ", NormalizedForm: "じゃあ", Pos: PartOfSpeech.Conjunction,
                Reading: "ジャア", Pin: 1005900, PinReadingIndex: 0)]),

        // Kyushu や + ばい follows a full predicate; after a noun it is a cut やばい.
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

        // Kana からだ after a predicate is から + だ, not 体; a following case/topic particle means the body noun.
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

        // Sentence-final ねえ can't follow 仮定形 なら, so ならねえ is 成る's slang negative (我慢ならねえ), shaped as ねえ IAdjective.
        new RewriteRule("nara-nee", RewritePhase.Early,
            [new TokenPattern(Text: "なら", Pos: [PartOfSpeech.Auxiliary], DictFormAnyOf: ["だ"]),
             new TokenPattern(Text: "ねえ", Pos: [PartOfSpeech.Particle])],
            [
                new TokenTemplate("なら", DictForm: "成る", NormalizedForm: "成る", Pos: PartOfSpeech.Verb, Reading: "ナラ"),
                new TokenTemplate("ねえ", DictForm: "ねえ", NormalizedForm: "無い", Pos: PartOfSpeech.IAdjective, Reading: "ネエ"),
            ]),

        // 面さ (面す) is real only before れる/せる; otherwise it is suffix 面 (づら) + さ, Early so it rejoins its noun.
        new RewriteRule("tsura-sa", RewritePhase.Early,
            [new TokenPattern(Text: "面さ", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["面す", "面する"])],
            [
                new TokenTemplate("面", DictForm: "面", NormalizedForm: "面", Pos: PartOfSpeech.Suffix, Reading: "ヅラ"),
                new TokenTemplate("さ", DictForm: "さ", NormalizedForm: "さ", Pos: PartOfSpeech.Particle, Reading: "サ"),
            ],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun]),
            Next: new ContextCond(TextAnyOf: ["れ", "れる", "れた", "せ", "せる"], Negate: true)),

        // Sudachi's adverb もさ is always particle も + さ; kana 猛者 is [uk] in JMdict and would otherwise claim it.
        new RewriteRule("demo-sa", RewritePhase.Early,
            [new TokenPattern(Text: "で"),
             new TokenPattern(Text: "もさ", Pos: [PartOfSpeech.Adverb])],
            [
                new TokenTemplate("でも", DictForm: "でも", NormalizedForm: "でも", Pos: PartOfSpeech.Conjunction, Reading: "デモ"),
                new TokenTemplate("さ", DictForm: "さ", NormalizedForm: "さ", Pos: PartOfSpeech.Particle, Reading: "サ"),
            ]),
        new RewriteRule("mo-sa", RewritePhase.Early,
            [new TokenPattern(Text: "もさ", Pos: [PartOfSpeech.Adverb])],
            [
                new TokenTemplate("も", DictForm: "も", NormalizedForm: "も", Pos: PartOfSpeech.Particle, Reading: "モ"),
                new TokenTemplate("さ", DictForm: "さ", NormalizedForm: "さ", Pos: PartOfSpeech.Particle, Reading: "サ"),
            ]),
        new RewriteRule("demo-sa-katakana", RewritePhase.Early,
            [new TokenPattern(Text: "で"),
             new TokenPattern(Text: "もサ", Pos: [PartOfSpeech.Adverb])],
            [
                new TokenTemplate("でも", DictForm: "でも", NormalizedForm: "でも", Pos: PartOfSpeech.Conjunction, Reading: "デモ"),
                new TokenTemplate("サ", DictForm: "さ", NormalizedForm: "さ", Pos: PartOfSpeech.Particle, Reading: "サ"),
            ]),
        new RewriteRule("mo-sa-katakana", RewritePhase.Early,
            [new TokenPattern(Text: "もサ", Pos: [PartOfSpeech.Adverb])],
            [
                new TokenTemplate("も", DictForm: "も", NormalizedForm: "も", Pos: PartOfSpeech.Particle, Reading: "モ"),
                new TokenTemplate("サ", DictForm: "さ", NormalizedForm: "さ", Pos: PartOfSpeech.Particle, Reading: "サ"),
            ]),

        // あ directly against いつも is あいつ + も; a genuine interjection あ is set off by punctuation.
        new RewriteRule("aitsu-mo", RewritePhase.Late,
            [new TokenPattern(Text: "あ", Pos: [PartOfSpeech.Interjection], RequireUnpinned: false),
             new TokenPattern(Text: "いつも")],
            [
                new TokenTemplate("あいつ", DictForm: "あいつ", NormalizedForm: "あいつ", Pos: PartOfSpeech.Pronoun, Reading: "アイツ"),
                new TokenTemplate("も", DictForm: "も", NormalizedForm: "も", Pos: PartOfSpeech.Particle, Reading: "モ"),
            ]),

        // --- Single-surface fixes at the ProcessSpecialCases position; none gate on pins. ---

        // そうかいそうかい: Sudachi mashes the middle into OOV かいそうか, which then matches 階層化.
        new RewriteRule("kaisouka", RewritePhase.Early,
            [new TokenPattern(Text: "かいそうか", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false)],
            [new TokenTemplate("かい", DictForm: "かい", NormalizedForm: "かい", Pos: PartOfSpeech.Particle, Reading: "カイ"),
             new TokenTemplate("そう", DictForm: "そう", NormalizedForm: "そう", Pos: PartOfSpeech.Adverb, Reading: "ソウ"),
             new TokenTemplate("か", DictForm: "か", NormalizedForm: "か", Pos: PartOfSpeech.Particle, Reading: "カ")]),

        // [noun]がないって: Sudachi cuts がな(仮名)+いっ(言う)+て, which resolves to the rare 我鳴る.
        new RewriteRule("ganai-tte", RewritePhase.Early,
            [new TokenPattern(Text: "がな", NormalizedFormAnyOf: ["仮名"], RequireUnpinned: false),
             new TokenPattern(Text: "いっ", NormalizedFormAnyOf: ["言う"], RequireUnpinned: false),
             new TokenPattern(Text: "て", RequireUnpinned: false)],
            [new TokenTemplate("が", DictForm: "が", NormalizedForm: "が", Pos: PartOfSpeech.Particle, Reading: "ガ", Pin: 2028930),
             new TokenTemplate("ない", DictForm: "ない", NormalizedForm: "無い", Pos: PartOfSpeech.IAdjective, Reading: "ナイ", Pin: 1529520),
             new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ", Pin: 2086960)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun,
                PartOfSpeech.Pronoun, PartOfSpeech.Counter])),

        // After a predicate, きっての steals とき's き (→ とき+って+の); after a noun it is real (学校きっての秀才).
        new RewriteRule("toki-tteno", RewritePhase.Early,
            [new TokenPattern(Text: "と", RequireUnpinned: false),
             new TokenPattern(Text: "きっての", RequireUnpinned: false)],
            [new TokenTemplate("とき", DictForm: "とき", NormalizedForm: "時", Pos: PartOfSpeech.Noun, Reading: "トキ"),
             new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ", Pin: 2086960),
             new TokenTemplate("の", DictForm: "の", NormalizedForm: "の", Pos: PartOfSpeech.Particle, Reading: "ノ")],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb, PartOfSpeech.IAdjective,
                PartOfSpeech.Auxiliary, PartOfSpeech.Expression])),

        // Sudachi normalises most ai→ee adjectives whole, but ありがてぇ shreds to あり|が|てぇ.
        new RewriteRule("arigatee", RewritePhase.Early,
            [new TokenPattern(Text: "あり", RequireUnpinned: false),
             new TokenPattern(Text: "が", RequireUnpinned: false),
             new TokenPattern(Text: "てぇ", RequireUnpinned: false)],
            [new TokenTemplate("ありがてぇ", DictForm: "ありがたい", NormalizedForm: "ありがたい",
                Pos: PartOfSpeech.IAdjective, Reading: "アリガテェ", Pin: 1541560, RecoverConjugations: true)]),

        // Non-standard 貸り (借り) cuts as 貸 Noun + り Aux; as a verb stem the inflection combines (貸りたい).
        new RewriteRule("kariru", RewritePhase.Early,
            [new TokenPattern(Text: "貸", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false),
             new TokenPattern(Text: "り", RequireUnpinned: false)],
            [new TokenTemplate("貸り", DictForm: "貸りる", NormalizedForm: "貸りる", Pos: PartOfSpeech.Verb, Reading: "カリ")],
            Guard: new LookupGuard(LookupGuardKind.NonNameCompoundExists, "貸りる")),

        // Sudachi cuts こんなの as 連体詞 こん|なの (→ 紺); the POS gate spares the noun (色は紺なの).
        new RewriteRule("konna-no", RewritePhase.Early,
            [new TokenPattern(Text: "こん", Pos: [PartOfSpeech.PrenounAdjectival], RequireUnpinned: false),
             new TokenPattern(Text: "なの", RequireUnpinned: false)],
            [new TokenTemplate("こんな", DictForm: "こんな", NormalizedForm: "こんな", Reading: "コンナ"),
             new TokenTemplate("の", DictForm: "の", NormalizedForm: "の", Pos: PartOfSpeech.Particle, Reading: "ノ")],
            Guard: new LookupGuard(LookupGuardKind.CompoundExists, "こんな")),
        new RewriteRule("sonna-no", RewritePhase.Early,
            [new TokenPattern(Text: "そん", Pos: [PartOfSpeech.PrenounAdjectival], RequireUnpinned: false),
             new TokenPattern(Text: "なの", RequireUnpinned: false)],
            [new TokenTemplate("そんな", DictForm: "そんな", NormalizedForm: "そんな", Reading: "ソンナ"),
             new TokenTemplate("の", DictForm: "の", NormalizedForm: "の", Pos: PartOfSpeech.Particle, Reading: "ノ")],
            Guard: new LookupGuard(LookupGuardKind.CompoundExists, "そんな")),
        new RewriteRule("anna-no", RewritePhase.Early,
            [new TokenPattern(Text: "あん", Pos: [PartOfSpeech.PrenounAdjectival], RequireUnpinned: false),
             new TokenPattern(Text: "なの", RequireUnpinned: false)],
            [new TokenTemplate("あんな", DictForm: "あんな", NormalizedForm: "あんな", Reading: "アンナ"),
             new TokenTemplate("の", DictForm: "の", NormalizedForm: "の", Pos: PartOfSpeech.Particle, Reading: "ノ")],
            Guard: new LookupGuard(LookupGuardKind.CompoundExists, "あんな")),
        new RewriteRule("donna-no", RewritePhase.Early,
            [new TokenPattern(Text: "どん", Pos: [PartOfSpeech.PrenounAdjectival], RequireUnpinned: false),
             new TokenPattern(Text: "なの", RequireUnpinned: false)],
            [new TokenTemplate("どんな", DictForm: "どんな", NormalizedForm: "どんな", Reading: "ドンナ"),
             new TokenTemplate("の", DictForm: "の", NormalizedForm: "の", Pos: PartOfSpeech.Particle, Reading: "ノ")],
            Guard: new LookupGuard(LookupGuardKind.CompoundExists, "どんな")),

        // Sudachi cuts ども+め after a noun as prefix ど + もめ (揉め) (化け物どもめ).
        new RewriteRule("domo-me", RewritePhase.Early,
            [new TokenPattern(Text: "ど", Pos: [PartOfSpeech.Prefix], RequireUnpinned: false),
             new TokenPattern(Text: "もめ", RequireUnpinned: false)],
            [new TokenTemplate("ども", DictForm: "ども", NormalizedForm: "ども", Pos: PartOfSpeech.Suffix, Reading: "ドモ"),
             new TokenTemplate("め", DictForm: "め", NormalizedForm: "め", Pos: PartOfSpeech.Suffix, Reading: "メ")],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun,
                PartOfSpeech.Name, PartOfSpeech.Pronoun])),

        // Katakana particles ノヨ, not the name 乃代.
        new RewriteRule("no-yo", RewritePhase.Early,
            [new TokenPattern(Text: "ノヨ", Pos: [PartOfSpeech.Noun, PartOfSpeech.NaAdjective], RequireUnpinned: false)],
            [new TokenTemplate("ノ", DictForm: "の", NormalizedForm: "の", Pos: PartOfSpeech.Particle, Reading: "ノ"),
             new TokenTemplate("ヨ", DictForm: "よ", NormalizedForm: "よ", Pos: PartOfSpeech.Particle, Reading: "ヨ")]),

        // Clause-initial いいか、/いいか! is "Listen!"; the question keeps its split elsewhere (行っていいか分からない, いいか?).
        new RewriteRule("iika-listen", RewritePhase.Early,
            [new TokenPattern(Text: "いい", Pos: [PartOfSpeech.IAdjective], RequireUnpinned: false),
             new TokenPattern(Text: "か", Pos: [PartOfSpeech.Particle], RequireUnpinned: false)],
            [new TokenTemplate("いいか", DictForm: "いいか", NormalizedForm: "いいか", Pos: PartOfSpeech.Expression,
                Reading: "イイカ", Pin: 2555520)],
            Prev: new ContextCond(ClauseBoundary: true),
            Next: new ContextCond(TextAnyOf: ["、", "!", "！"])),

        // Clause-initial ですが/ですけど are polite conjunctions ("however"); with no predicate before them they cannot be copula + particle.
        new RewriteRule("desuga-conj", RewritePhase.Early,
            [new TokenPattern(Text: "です", Pos: [PartOfSpeech.Auxiliary]),
             new TokenPattern(Text: "が", Pos: [PartOfSpeech.Particle])],
            [new TokenTemplate("ですが", DictForm: "ですが", NormalizedForm: "ですが", Pos: PartOfSpeech.Conjunction,
                Reading: "デスガ", Pin: 2850805)],
            Prev: new ContextCond(ClauseBoundary: true)),
        new RewriteRule("desukedo-conj", RewritePhase.Early,
            [new TokenPattern(Text: "です", Pos: [PartOfSpeech.Auxiliary]),
             new TokenPattern(Text: "けど", Pos: [PartOfSpeech.Particle])],
            [new TokenTemplate("ですけど", DictForm: "ですけど", NormalizedForm: "ですけど", Pos: PartOfSpeech.Conjunction,
                Reading: "デスケド", Pin: 2871534)],
            Prev: new ContextCond(ClauseBoundary: true)),

        // Noun ひと with lemma 一 is almost always 人; prefix uses (ひと月) are tagged 接頭辞 or stay fused.
        new RewriteRule("hito-dict", RewritePhase.Early,
            [new TokenPattern(Text: "ひと", Pos: [PartOfSpeech.Noun], DictFormAnyOf: ["一"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "人", NormalizedForm: "人")]),
        new RewriteRule("hito-norm", RewritePhase.Early,
            [new TokenPattern(Text: "ひと", Pos: [PartOfSpeech.Noun], NormalizedFormAnyOf: ["一"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "人", NormalizedForm: "人")]),

        // Suffix かねる + negative is the lexicalised かねない (破りかねない); df=かねる rules out 金.
        new RewriteRule("kanenai", RewritePhase.Early,
            [new TokenPattern(Text: "かね", Pos: [PartOfSpeech.Suffix], DictFormAnyOf: ["かねる"], RequireUnpinned: false),
             new TokenPattern(Text: "ない", RequireUnpinned: false)],
            [new TokenTemplate("かねない", DictForm: "かねない", NormalizedForm: "かねない",
                Pos: PartOfSpeech.Expression, Reading: "カネナイ")]),
        new RewriteRule("kanenakatta", RewritePhase.Early,
            [new TokenPattern(Text: "かね", Pos: [PartOfSpeech.Suffix], DictFormAnyOf: ["かねる"], RequireUnpinned: false),
             new TokenPattern(Text: "なかっ", RequireUnpinned: false),
             new TokenPattern(Text: "た", RequireUnpinned: false)],
            [new TokenTemplate("かねなかった", DictForm: "かねない", NormalizedForm: "かねない",
                Pos: PartOfSpeech.Expression, Reading: "カネナカッタ")]),

        // Imperative たまえ after a verb stem (気をつけたまえ); た前 is ungrammatical ("before" is る前/の前).
        new RewriteRule("tamae", RewritePhase.Early,
            [new TokenPattern(Text: "た", Pos: [PartOfSpeech.Auxiliary], RequireUnpinned: false),
             new TokenPattern(Text: "まえ", Pos: [PartOfSpeech.Noun], RequireUnpinned: false)],
            [new TokenTemplate("たまえ", DictForm: "たまえ", NormalizedForm: "たまえ", Pos: PartOfSpeech.Suffix, Reading: "タマエ")],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb])),

        // Suffix-tagged っけ is the recollection sentence-ending particle.
        new RewriteRule("kke-sfp", RewritePhase.Early,
            [new TokenPattern(Text: "っけ", Pos: [PartOfSpeech.Suffix], RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Particle, PosSection: PartOfSpeechSection.SentenceEndingParticle)]),

        // 私戦 + いたく(痛く) is 私 + 戦いたく, not しせん "private war".
        new RewriteRule("shisen-itaku", RewritePhase.Early,
            [new TokenPattern(Text: "私戦", Pos: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun], RequireUnpinned: false),
             new TokenPattern(Text: "いたく", NormalizedFormAnyOf: ["痛く"], RequireUnpinned: false)],
            [new TokenTemplate("私", DictForm: "私", NormalizedForm: "私", Pos: PartOfSpeech.Pronoun, Reading: "ワタシ"),
             new TokenTemplate("戦いたく", DictForm: "戦う", NormalizedForm: "戦う", Pos: PartOfSpeech.Verb, Reading: "タタカイタク")]),

        // Sudachi glues a trailing particle into these 表現 tokens.
        new RewriteRule("hitomae-de", RewritePhase.Early,
            [new TokenPattern(Text: "人前で", Pos: [PartOfSpeech.Expression], RequireUnpinned: false)],
            [new TokenTemplate("人前", DictForm: "人前", NormalizedForm: "人前", Pos: PartOfSpeech.Noun, Reading: ""),
             new TokenTemplate("で", DictForm: "で", NormalizedForm: "で", Pos: PartOfSpeech.Particle, Reading: "デ")]),
        new RewriteRule("sama-ni", RewritePhase.Early,
            [new TokenPattern(Text: "様に", Pos: [PartOfSpeech.Expression], RequireUnpinned: false)],
            [new TokenTemplate("様", DictForm: "様", NormalizedForm: "様", Pos: PartOfSpeech.Noun, Reading: ""),
             new TokenTemplate("に", DictForm: "に", NormalizedForm: "に", Pos: PartOfSpeech.Particle, Reading: "ニ")]),
        new RewriteRule("dare-datte", RewritePhase.Early,
            [new TokenPattern(Text: "誰だって", Pos: [PartOfSpeech.Expression], RequireUnpinned: false)],
            [new TokenTemplate("誰", DictForm: "誰", NormalizedForm: "誰", Pos: PartOfSpeech.Pronoun, Reading: ""),
             new TokenTemplate("だって", DictForm: "だって", NormalizedForm: "だって", Pos: PartOfSpeech.Particle, Reading: "ダッテ")]),

        // Split so a preceding 放って can form 放っておく.
        new RewriteRule("okeba-ii", RewritePhase.Early,
            [new TokenPattern(Text: "おけばいい", Pos: [PartOfSpeech.Expression], RequireUnpinned: false)],
            [new TokenTemplate("おけ", DictForm: "おく", NormalizedForm: "おく", Pos: PartOfSpeech.Verb, Reading: "オケ"),
             new TokenTemplate("ば", DictForm: "ば", NormalizedForm: "ば", Pos: PartOfSpeech.Particle, Reading: "バ"),
             new TokenTemplate("いい", DictForm: "いい", NormalizedForm: "いい", Pos: PartOfSpeech.IAdjective, Reading: "イイ")]),

        // Suffix-tagged 張り before ます/やがる is 張る's stem (結界張ります, 意地ばっかり張りやがって).
        new RewriteRule("hari-verb-stem", RewritePhase.Early,
            [new TokenPattern(Text: "張り", Pos: [PartOfSpeech.Suffix])],
            [new TokenTemplate("", DictForm: "張る", NormalizedForm: "張る", Pos: PartOfSpeech.Verb)],
            Next: new ContextCond(TextStartsWithAnyOf: ["ます", "まし", "ませ", "やが"])),

        // Sudachi marks the 心 of 心ばかり "small token (of thanks)" as a prefix; the noun 心 + ばかり stays split.
        new RewriteRule("kokoro-bakari", RewritePhase.Early,
            [new TokenPattern(Text: "心", Pos: [PartOfSpeech.Prefix]),
             new TokenPattern(Text: "ばかり")],
            [new TokenTemplate("心ばかり", DictForm: "心ばかり", NormalizedForm: "心ばかり", Pos: PartOfSpeech.Noun,
                Reading: "ココロバカリ", Pin: 1793680)],
            Next: new ContextCond(TextAnyOf: ["が", "は", "を", "も"], Negate: true)),

        // 結構人 "good-natured person" is almost always the adverb 結構 + 人 (結構人がいる).
        new RewriteRule("kekkou-hito", RewritePhase.Early,
            [new TokenPattern(Text: "結構人", RequireUnpinned: false)],
            [new TokenTemplate("結構", DictForm: "結構", NormalizedForm: "結構", Pos: PartOfSpeech.Adverb, Reading: "ケッコウ", Pin: 1254760, HardPin: true),
             new TokenTemplate("人", DictForm: "人", NormalizedForm: "人", Pos: PartOfSpeech.Noun, Reading: "ヒト")]),

        // で + noun しょう is a split でしょう.
        new RewriteRule("de-shou", RewritePhase.Early,
            [new TokenPattern(Text: "で", Pos: [PartOfSpeech.Conjunction, PartOfSpeech.Auxiliary], RequireUnpinned: false),
             new TokenPattern(Text: "しょう", Pos: [PartOfSpeech.Noun], RequireUnpinned: false)],
            [new TokenTemplate("でしょう", DictForm: "でしょう", NormalizedForm: "です", Pos: PartOfSpeech.Expression,
                PosSection: PartOfSpeechSection.None, Reading: "デショウ")]),

        // Sudachi parses 何でしょう as 何で(noun) + しょう(noun).
        new RewriteRule("nande-shou", RewritePhase.Early,
            [new TokenPattern(Text: "何で", Pos: [PartOfSpeech.Noun], RequireUnpinned: false),
             new TokenPattern(Text: "しょう", Pos: [PartOfSpeech.Noun], RequireUnpinned: false)],
            [new TokenTemplate("何", DictForm: "何", NormalizedForm: "何", Pos: PartOfSpeech.Pronoun, Reading: "ナニ"),
             new TokenTemplate("でしょう", DictForm: "でしょう", NormalizedForm: "です", Pos: PartOfSpeech.Expression,
                PosSection: PartOfSpeechSection.None, Reading: "デショウ")]),

        // Sudachi sometimes tags でしょう Auxiliary.
        new RewriteRule("deshou-expression", RewritePhase.Early,
            [new TokenPattern(Text: "でしょう", RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Expression, PosSection: PartOfSpeechSection.None)]),

        // Standalone ぬ misread as the archaic verb 寝(ぬ) is the classical negative auxiliary (norm ず).
        new RewriteRule("nu-negative", RewritePhase.Early,
            [new TokenPattern(Text: "ぬ", Pos: [PartOfSpeech.Verb], NormalizedFormAnyOf: ["寝る"], RequireUnpinned: false)],
            [new TokenTemplate("", NormalizedForm: "ず", Pos: PartOfSpeech.Auxiliary)]),

        // なれ tagged 汝 (archaic "thou") is 成る's 命令形/可能形 stem in modern text.
        new RewriteRule("nare-naru", RewritePhase.Early,
            [new TokenPattern(Text: "なれ", Pos: [PartOfSpeech.Pronoun], NormalizedFormAnyOf: ["汝"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "なる", NormalizedForm: "なる", Pos: PartOfSpeech.Verb, Reading: "ナレ")]),

        new RewriteRule("ima-adverb", RewritePhase.Early,
            [new TokenPattern(Text: "今", Pos: [PartOfSpeech.Prefix], RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Adverb)]),

        // Modern 空 almost always reads から; うつろ is usually written 虚ろ.
        new RewriteRule("kara-empty", RewritePhase.Early,
            [new TokenPattern(Text: "空", Pos: [PartOfSpeech.NaAdjective], ReadingPrefix: "ウツロ", RequireUnpinned: false)],
            [new TokenTemplate("", NormalizedForm: "空", Pos: PartOfSpeech.Noun, Reading: "カラ")]),

        // にしろ ("even if"), except after a noun-ish host (大概にしろ is 大概にする's imperative).
        new RewriteRule("ni-shiro", RewritePhase.Early,
            [new TokenPattern(Text: "に", RequireUnpinned: false),
             new TokenPattern(Text: "しろ", RequireUnpinned: false)],
            [new TokenTemplate("にしろ", DictForm: "にしろ", NormalizedForm: "にしろ", Pos: PartOfSpeech.Expression, Reading: "ニシロ")],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.NaAdjective, PartOfSpeech.Pronoun], Negate: true)),

        // とくと after a verb is とく (ておく) + と, not the adverb 篤と.
        new RewriteRule("toku-to", RewritePhase.Early,
            [new TokenPattern(Text: "とくと", Pos: [PartOfSpeech.Adverb], NormalizedFormAnyOf: ["篤と"], RequireUnpinned: false)],
            [new TokenTemplate("とく", DictForm: "とく", NormalizedForm: "とく", Pos: PartOfSpeech.Auxiliary, Reading: "トク"),
             new TokenTemplate("と", DictForm: "と", NormalizedForm: "と", Pos: PartOfSpeech.Particle, Reading: "ト")],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb])),

        // とくよう is とく (ておく) + よう, not 徳用; noun hosts count since Sudachi often tags verb stems as nouns.
        new RewriteRule("toku-you", RewritePhase.Early,
            [new TokenPattern(Text: "とくよう", NormalizedFormAnyOf: ["徳用"], RequireUnpinned: false)],
            [new TokenTemplate("とく", DictForm: "とく", NormalizedForm: "とく", Pos: PartOfSpeech.Auxiliary, Reading: "トク"),
             new TokenTemplate("よう", DictForm: "よう", NormalizedForm: "よう", Pos: PartOfSpeech.Noun, Reading: "ヨウ")],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Verb, PartOfSpeech.Noun])),

        new RewriteRule("shiyou-toshite", RewritePhase.Early,
            [new TokenPattern(Text: "しよう", Pos: [PartOfSpeech.Noun], RequireUnpinned: false),
             new TokenPattern(Text: "として", RequireUnpinned: false)],
            [new TokenTemplate("しようとして", DictForm: "しようとする", Pos: PartOfSpeech.Verb, Reading: "シヨウトシテ")]),
        // Sudachi sometimes misreads し as a conjunction here.
        new RewriteRule("shi-youtoshite", RewritePhase.Early,
            [new TokenPattern(Text: "し", Pos: [PartOfSpeech.Conjunction], RequireUnpinned: false),
             new TokenPattern(Text: "ようとして", RequireUnpinned: false)],
            [new TokenTemplate("しようとして", DictForm: "しようとして", Pos: PartOfSpeech.Verb, Reading: "シヨウトシテ")]),

        // 恋って is 恋 + quotative って, not archaic 恋う.
        new RewriteRule("koi-tte", RewritePhase.Early,
            [new TokenPattern(Text: "恋っ", Pos: [PartOfSpeech.Verb], DictFormAnyOf: ["恋う"], RequireUnpinned: false),
             new TokenPattern(Text: "て", Pos: [PartOfSpeech.Particle], RequireUnpinned: false)],
            [new TokenTemplate("恋", DictForm: "恋", NormalizedForm: "恋", Pos: PartOfSpeech.Noun, Reading: "コイ"),
             new TokenTemplate("って", DictForm: "って", NormalizedForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ")]),

        // 逆 + copula に is almost always the adverb 逆に ("conversely").
        new RewriteRule("gyaku-ni", RewritePhase.Early,
            [new TokenPattern(Text: "逆", Pos: [PartOfSpeech.Noun], RequireUnpinned: false),
             new TokenPattern(Text: "に", DictFormAnyOf: ["だ"], RequireUnpinned: false)],
            [new TokenTemplate("逆に", DictForm: "逆に", Pos: PartOfSpeech.Adverb, Reading: "ギャクニ")]),

        new RewriteRule("you-noun", RewritePhase.Early,
            [new TokenPattern(Text: "よう", RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Noun)]),
        new RewriteRule("juugo-numeral", RewritePhase.Early,
            [new TokenPattern(Text: "十五", RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Numeral)]),
        new RewriteRule("ore-pronoun", RewritePhase.Early,
            [new TokenPattern(Text: "オレ", RequireUnpinned: false)],
            [new TokenTemplate("", Pos: PartOfSpeech.Pronoun)]),

        // 飛んだ after a case particle is 飛ぶ past (意識が飛んだ); the 連体詞 とんだ only opens a noun phrase.
        new RewriteRule("tonda-tobu", RewritePhase.Cleanup,
            [new TokenPattern(Text: "飛んだ")],
            [new TokenTemplate("", DictForm: "飛ぶ", NormalizedForm: "飛ぶ", Pos: PartOfSpeech.Verb, Pin: 1429700, RecoverConjugations: true)],
            Prev: new ContextCond(TextAnyOf: ["が", "は", "も", "に", "を", "で", "へ", "から", "まで"])),

        // 置き after a case particle is 置く's 連用形 (横に置き、); the interval suffix おき follows a quantity (三日置き).
        new RewriteRule("oki-oku", RewritePhase.Cleanup,
            [new TokenPattern(Text: "置き")],
            [new TokenTemplate("", DictForm: "置く", NormalizedForm: "置く", Pos: PartOfSpeech.Verb, Pin: 1421850, RecoverConjugations: true)],
            Prev: new ContextCond(TextAnyOf: ["に", "を", "へ"])),

        // Sudachi shreds 強がって into 強/がっ/て; conjugated forms only, since bare 強がり is the noun.
        new RewriteRule("tsuyogaru", RewritePhase.Cleanup,
            [new TokenPattern(TextAnyOf: ["強がって", "強がった"])],
            [new TokenTemplate("", DictForm: "強がる", NormalizedForm: "強がる", Pos: PartOfSpeech.Verb, Pin: 1928800, RecoverConjugations: true)]),

        // 強がり before a particle is the noun "bluff"; the がる chain would resolve it to 強い (ながら keeps the verb).
        new RewriteRule("tsuyogari-noun", RewritePhase.Cleanup,
            [new TokenPattern(Text: "強がり")],
            [new TokenTemplate("", DictForm: "強がり", NormalizedForm: "強がり", Pos: PartOfSpeech.Noun, Pin: 1236110)],
            Next: new ContextCond(TextAnyOf: ["を", "が", "は", "の", "で"])),

        // 放ってお* is the expression 放っておく, not bare 放る.
        new RewriteRule("hotteoku", RewritePhase.Cleanup,
            [new TokenPattern(TextStartsWith: "放ってお")],
            [new TokenTemplate("", DictForm: "放っておく", NormalizedForm: "放っておく", Pin: 1907980, RecoverConjugations: true)]),

        // まさに never takes a topic particle, so 将に right before は is the noun 将 + に (将には必要な資質).
        new RewriteRule("sho-ni", RewritePhase.Cleanup,
            [new TokenPattern(Text: "将に", Pos: [PartOfSpeech.Adverb])],
            [new TokenTemplate("将", DictForm: "将", NormalizedForm: "将", Pos: PartOfSpeech.Noun, Reading: "ショウ"),
             new TokenTemplate("に", DictForm: "に", NormalizedForm: "に", Pos: PartOfSpeech.Particle,
                 PosSection: PartOfSpeechSection.CaseMarkingParticle, Reading: "ニ")],
            Next: new ContextCond(TextAnyOf: ["は"], PosAnyOf: [PartOfSpeech.Particle])),

        // 表 before へ/に with no noun host is おもて (表へ出る), not the chart ひょう (メニュー表).
        new RewriteRule("omote", RewritePhase.Reading,
            [new TokenPattern(Text: "表", ReadingPrefix: "ヒョウ")],
            [new TokenTemplate("", Reading: "オモテ")],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun], Negate: true),
            Next: new ContextCond(TextAnyOf: ["へ", "に"])),
        // 一日 is ついたち only as a date after a month (X月一日).
        new RewriteRule("ichinichi", RewritePhase.Reading,
            [new TokenPattern(TextAnyOf: ["一日", "１日", "1日"], ReadingPrefix: "ツイタチ")],
            [new TokenTemplate("", Reading: "イチニチ")],
            Prev: new ContextCond(TextEndsWithAnyOf: ["月"], Negate: true)),
        // Compound-only readings: 禍 カ (コロナ禍), 私 シ (私立), 次 ジ (次回), 隙 ヒマ (obsolete).
        new RewriteRule("wazawai", RewritePhase.Reading,
            [new TokenPattern(Text: "禍", ReadingPrefix: "カ")],
            [new TokenTemplate("", Reading: "ワザワイ")]),
        new RewriteRule("watashi", RewritePhase.Reading,
            [new TokenPattern(Text: "私", ReadingPrefix: "シ")],
            [new TokenTemplate("", Pos: PartOfSpeech.Pronoun, Reading: "ワタシ")]),
        new RewriteRule("tsugi", RewritePhase.Reading,
            [new TokenPattern(Text: "次", Pos: [PartOfSpeech.Prefix], ReadingPrefix: "ジ")],
            [new TokenTemplate("", Pos: PartOfSpeech.CommonNoun, Reading: "ツギ")]),
        new RewriteRule("suki", RewritePhase.Reading,
            [new TokenPattern(Text: "隙", ReadingPrefix: "ヒマ")],
            [new TokenTemplate("", Reading: "スキ")]),
        // Rare name/archaic readings: 全機 まさき, 何時 なんどき, 大仰 おおのき.
        new RewriteRule("zenki", RewritePhase.Reading,
            [new TokenPattern(Text: "全機", ReadingPrefix: "マサキ")],
            [new TokenTemplate("", Reading: "ゼンキ")]),
        new RewriteRule("nanji", RewritePhase.Reading,
            [new TokenPattern(Text: "何時", ReadingPrefix: "ナンドキ")],
            [new TokenTemplate("", Reading: "ナンジ")]),
        new RewriteRule("oogyou", RewritePhase.Reading,
            [new TokenPattern(Text: "大仰", ReadingPrefix: "オオノキ")],
            [new TokenTemplate("", Pos: PartOfSpeech.NaAdjective, Reading: "オオギョウ")]),
        // Suffix 長 チョウ is the noun "chief" (1429740); JMDict's suffix 長 is なが "long".
        new RewriteRule("chou-chief", RewritePhase.Reading,
            [new TokenPattern(Text: "長", Pos: [PartOfSpeech.Suffix], ReadingPrefix: "チョウ")],
            [new TokenTemplate("", Pos: PartOfSpeech.Noun)]),
        // 通り right after a noun is the suffix どおり "as per" (予定通り), not the street noun.
        new RewriteRule("doori", RewritePhase.Reading,
            [new TokenPattern(Text: "通り", ReadingPrefix: "トオリ")],
            [new TokenTemplate("", Pin: 1432930)],
            Prev: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.CommonNoun])),
        // 縁 after の with a rimmed object is ふち "rim" (浴槽の縁), not えん "fate".
        new RewriteRule("fuchi", RewritePhase.Reading,
            [new TokenPattern(Text: "縁", ReadingPrefix: "エン")],
            [new TokenTemplate("", Reading: "フチ")],
            Prev: new ContextCond(TextAnyOf: ["の"]),
            Window: new WindowCond(-2, -2, TextAnyOf: ["浴槽", "風呂", "プール", "池", "井戸", "窓", "テーブル",
                "机", "帽子", "コップ", "グラス", "崖", "屋根", "ベッド"])),
        // 生 after 次の/前の… is せい "life" (次の生); なま lists bare 生 with priority, so the reading alone loses.
        new RewriteRule("sei-life", RewritePhase.Reading,
            [new TokenPattern(Text: "生", ReadingPrefix: "ナマ")],
            [new TokenTemplate("", Reading: "セイ", Pin: 2088240)],
            Prev: new ContextCond(TextAnyOf: ["の"]),
            Window: new WindowCond(-2, -2, TextAnyOf: ["次", "前", "今", "来", "この", "別"])),
        // 糞 is くそ unless it reads as literal droppings: after の (犬の糞) or before と (糞と尿).
        new RewriteRule("kuso", RewritePhase.Reading,
            [new TokenPattern(Text: "糞", ReadingPrefix: "フン")],
            [new TokenTemplate("", Reading: "クソ")],
            Prev: new ContextCond(TextAnyOf: ["の"], Negate: true),
            Next: new ContextCond(TextAnyOf: ["と"], Negate: true)),
        // Sudachi's あの tags are unreliable: 感動詞 is treated as prenominal, 連体詞 as the filler when no nominal follows.
        new RewriteRule("ano-prenoun", RewritePhase.Reading,
            [new TokenPattern(Text: "あの", Pos: [PartOfSpeech.Interjection])],
            [new TokenTemplate("", Pos: PartOfSpeech.PrenounAdjectival)]),
        new RewriteRule("ano-filler", RewritePhase.Reading,
            [new TokenPattern(Text: "あの", Pos: [PartOfSpeech.PrenounAdjectival])],
            [new TokenTemplate("", Pos: PartOfSpeech.Interjection)],
            Next: new ContextCond(PosAnyOf: [PartOfSpeech.Noun, PartOfSpeech.Pronoun, PartOfSpeech.NaAdjective,
                PartOfSpeech.Counter, PartOfSpeech.Numeral], Negate: true)),
    ];

    private sealed class RewriteIndex
    {
        public readonly Dictionary<string, List<RewriteRule>> ByFirstText = new(StringComparer.Ordinal);
        // First pattern has no exact surface (StartsWith/EndsWith/POS-only), so these are tried at every token.
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

    // Fails fast at type load on duplicate ids, bad arity, and literal split/merge rules that don't conserve text.
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

            // Only all-literal rules are checkable here; TextAnyOf/StartsWith and Text="" are verified at runtime.
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

            // Splits/merges need an explicit reading per output.
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

    private List<WordInfo> ApplyTokenRewriteRulesReading(List<WordInfo> wordInfos) =>
        ApplyTokenRewriteRules(wordInfos, RewritePhase.Reading);

    private List<WordInfo> ApplyTokenRewriteRules(List<WordInfo> wordInfos, RewritePhase phase)
    {
        if (!_rewriteIndex.TryGetValue(phase, out var index) || index.IsEmpty)
            return wordInfos;
        return RunRewriteRules(wordInfos, index);
    }

    // Returns the input list when nothing fires, so the pipeline skips a rescan; exact bucket, then residual, in table order.
    private List<WordInfo> RunRewriteRules(List<WordInfo> wordInfos, RewriteIndex index)
    {
        List<WordInfo>? result = null;
        int i = 0;
        while (i < wordInfos.Count)
        {
            var rule = FindFiringRule(wordInfos, i, index);
            if (rule != null)
            {
                result ??= CopyAccumulatorUpTo(wordInfos, i);
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
        if (p.NormalizedFormAnyOf != null && Array.IndexOf(p.NormalizedFormAnyOf, token.NormalizedForm) < 0) return false;
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
        if (cond.Numeral)
            ok &= neighbour != null && IsNumeralToken(neighbour);
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
            // Output k inherits flags from matched[k], clamped so a 1:N split inherits from its single source.
            var source = matched[Math.Min(outputs.Count, len - 1)];
            string surface = t.Text.Length == 0 ? source.Text : t.Text;
            bool surfaceChanged = t.Text.Length > 0;

            var w = new WordInfo(source)
            {
                Text = surface,
                // Unset template fields keep the source's value, so a 1:1 pin setting only DictForm keeps NormalizedForm.
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
            if (t.PosSection != null)
                w.PartOfSpeechSection1 = t.PosSection.Value;
            if (t.RecoverConjugations && t.Pin != null)
                w.PreMatchedConjugations = PinnedConjugationProcess(surface, w.DictionaryForm);

            outputs.Add(w);
        }

        AssignOffsets(outputs, spanStart, spanEnd);
        Debug.Assert(string.Concat(matched.Select(m => m.Text)) == string.Concat(outputs.Select(o => o.Text)),
            $"Rewrite rule '{rule.Id}' did not conserve surface text at runtime.");
        return outputs;
    }

    // Tiles the span by surface length; with unknown (-1) source offsets, interior boundaries stay -1.
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

    // Test hook: runs a synthetic rule table (validated) instead of the static one.
    internal List<WordInfo> ApplyRewriteRulesForTesting(List<WordInfo> input, RewriteRule[] rules, RewritePhase phase)
    {
        var index = BuildRewriteIndex(rules).GetValueOrDefault(phase);
        return index == null || index.IsEmpty ? input : RunRewriteRules(input, index);
    }
}
