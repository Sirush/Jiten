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
// conjugation recovery. Rules live in RewriteRulesTable (currently empty — migration happens in later
// steps); the table is indexed by first-token surface for a single dictionary probe per token.

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
    bool RecoverConjugations = false);

// A neighbour (prev/next) guard. All specified constraints must hold (AND); Negate flips the result.
// ClauseBoundary matches a Symbol/SupplementarySymbol/BlankSpace neighbour or a list edge.
internal sealed record ContextCond(
    string[]? TextAnyOf = null,
    string[]? TextEndsWithAnyOf = null,
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

        // なかれ = the classical negative imperative 勿れ (1535750), not 無い/なし.
        new RewriteRule("nakare", RewritePhase.Cleanup,
            [new TokenPattern(Text: "なかれ", DictFormAnyOf: ["ない", "なし", "無い"], RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "なかれ", NormalizedForm: "なかれ", Pos: PartOfSpeech.Suffix, Pin: 1535750)]),

        // --- Re-cuts (splits/merges). Templates carry the correct readings, so the F4 stale-reading
        // class cannot recur. Text is conserved (asserted at load). ---

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
    // empty table so the engine can be exercised before any rules are migrated.
    internal List<WordInfo> ApplyRewriteRulesForTesting(List<WordInfo> input, RewriteRule[] rules, RewritePhase phase)
    {
        var index = BuildRewriteIndex(rules).GetValueOrDefault(phase);
        return index == null || index.IsEmpty ? input : RunRewriteRules(input, index);
    }
}
