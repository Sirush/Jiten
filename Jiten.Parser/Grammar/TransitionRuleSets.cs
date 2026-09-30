namespace Jiten.Parser.Grammar;

internal static class TransitionRuleSets
{
    internal static readonly HashSet<string> VerbOnlyAuxDictForms =
    [
        "られる", "れる", "せる", "させる",
        "たい", "たがる",
        "ます"
    ];

    internal static readonly HashSet<string> VerbOrAdjAuxDictForms = ["た", "ぬ"];

    internal static readonly HashSet<string> CommonParticles =
    [
        "が", "を", "に", "で", "へ", "は", "の", "も", "や",
        "から", "まで", "より", "だけ", "しか", "ばかり", "など", "さえ"
    ];

    internal static readonly HashSet<string> CaseMarkingParticles = ["が", "を", "に", "で", "へ"];

    // Never sentence-initial; で is left out because conjunctive で can open a sentence.
    internal static readonly HashSet<string> StrictCaseMarkingParticles = ["が", "を", "へ"];

    internal static readonly HashSet<string> CopulaForms = ["だ", "です", "である"];

    internal static readonly HashSet<string> ExplanatoryNForms = ["ん", "んだ", "んです", "んじゃ", "んで"];

    internal static readonly HashSet<string> ConditionalParticles = ["と", "なら"];

    internal static readonly HashSet<string> TeFormAuxiliaries =
    [
        "いる", "ある", "しまう", "おく", "みる", "くる", "いく",
        "もらう", "あげる", "くれる"
    ];

    internal static readonly HashSet<string> Interjections =
    [
        "ああ", "ええ", "まあ", "ほら", "よう", "そうそう"
    ];

    internal static readonly HashSet<string> SuruForms =
    [
        "する", "した", "して", "し", "される", "させる"
    ];

    // Volitional とする "try to" forces the volitional reading (走り出そうと = 走り出す, not 走り出る + そう).
    internal static readonly HashSet<string> VolitionalToVerbForms =
    [
        "とする", "として", "とした", "としている", "としていた", "とすれば", "としたら", "と思う", "と思った"
    ];

    internal static readonly HashSet<string> HonorificSuffixes =
    [
        "さん", "くん", "ちゃん", "様", "殿", "氏"
    ];

    internal static readonly HashSet<string> HonorificRegisterTags = ["pol", "hon", "fam"];

    internal static readonly HashSet<string> NounSuffixes =
    [
        "的", "性", "化", "中", "用", "式", "風"
    ];

    // A nearby resolved contextWordId boosts the target verb over its homophones.
    internal static readonly Dictionary<int, (int TargetWordId, int Bonus)> NounVerbCollocations = new()
    {
        { 1172400, (1444150, 30) }, // 嘘 → 吐く (to tell a lie), not 点く/付く
        { 1597190, (1444150, 30) }, // ため息 → 吐く (to sigh), not 付く
        { 1371260, (1229610, 30) }, // 水 → 汲む (to draw water), not 組む
    };

    // A next-token surface in NextAnchors boosts the target wordId over a higher-priority homograph.
    internal static readonly Dictionary<int, (string[] NextAnchors, int Bonus)> ForwardAnchorBoosts = new()
    {
        // 一分前 = いっぷん "one minute", not the news-ranked いちぶ "one tenth" (1166270).
        { 1166290, (["前", "後", "ぐらい", "くらい", "ほど", "経つ", "経った", "経って"], 40) },
    };

    // Pass-2 prefilter keeps a confident token rescorable when an anchor could flip its homograph.
    internal static readonly HashSet<string> ForwardAnchorSurfaces =
        ForwardAnchorBoosts.Values.SelectMany(a => a.NextAnchors).ToHashSet();

    // A nominal previous token boosts the target wordId over a higher-priority homograph of the surface.
    internal static readonly Dictionary<int, (string Surface, int Bonus)> PrevNominalBoosts = new()
    {
        // NといいNといい "both N and N" follows a nominal; the wish 〜といい (2872982) follows a predicate.
        { 2844736, ("といい", 40) },
    };

    internal static readonly HashSet<string> PrevNominalBoostSurfaces =
        PrevNominalBoosts.Values.Select(v => v.Surface).ToHashSet();

    internal static readonly ScoringRule[] SoftRules =
    [
        new("noun-particle-synergy",
            [ScoringCondition.CandidateIsNounLike],
            [ScoringCondition.NextIsCommonParticle],
            40),

        new("noun-copula-synergy",
            [ScoringCondition.CandidateIsNounLike, ScoringCondition.CandidateIsNotHonorific, ScoringCondition.CandidateIsNotName],
            [ScoringCondition.NextIsCopula],
            65),

        new("na-adj-connector-synergy",
            [ScoringCondition.CandidateIsNaAdj],
            [ScoringCondition.NextIsNaConnector],
            30),

        // Must outweigh noun-particle-synergy (+40) from the same に (露になった = あらわ, not dew).
        new("na-adj-ni-adverbial-synergy",
            [ScoringCondition.CandidateIsNaAdj],
            [ScoringCondition.NextIsNiParticle],
            65),

        new("adverb-verb-synergy",
            [ScoringCondition.CandidateIsAdverb],
            [ScoringCondition.NextIsVerbOrIAdj],
            20),

        new("verb-aux-synergy",
            [ScoringCondition.CandidateIsAuxiliary],
            [ScoringCondition.PrevIsVerbOrIAdj],
            20),

        new("aux-copula-synergy",
            [ScoringCondition.CandidateIsAuxiliary],
            [ScoringCondition.NextIsCopula],
            65),

        new("single-kana-penalty-left",
            [ScoringCondition.CandidateIsSingleKanaNonParticle],
            [ScoringCondition.PrevIsSingleKanaNonParticle],
            -40),

        new("single-kana-penalty-right",
            [ScoringCondition.CandidateIsSingleKanaNonParticle],
            [ScoringCondition.NextIsSingleKanaNonParticle],
            -40),

        new("particle-particle-penalty-left",
            [ScoringCondition.CandidateIsParticle, ScoringCondition.CandidateIsNotNounLike],
            [ScoringCondition.PrevIsParticle],
            -20),

        new("particle-particle-penalty-right",
            [ScoringCondition.CandidateIsParticle, ScoringCondition.CandidateIsNotNounLike],
            [ScoringCondition.NextIsParticle],
            -20),

        new("no-da-synergy",
            [ScoringCondition.CandidateIsNoParticle],
            [ScoringCondition.PrevIsVerbAuxOrIAdj, ScoringCondition.NextIsCopula],
            25),

        new("predicate-explanatory-n-synergy",
            [ScoringCondition.CandidateIsPredicateHost],
            [ScoringCondition.NextIsExplanatoryN],
            25),

        // Must outbid a top-priority noun homograph plus its ruby prior (第二話: counter わ beats はなし).
        new("numeral-counter-cohesion",
            [ScoringCondition.CandidateIsCounter],
            [ScoringCondition.PrevIsNumeral],
            60),

        new("orphan-counter-penalty",
            [ScoringCondition.CandidateIsCounter, ScoringCondition.CandidateIsNotNounLike],
            [ScoringCondition.PrevIsNotNumericLike],
            -30),

        new("kanji-compound-break-penalty-left",
            [ScoringCondition.CandidateIsSingleKanji],
            [ScoringCondition.PrevIsSingleKanji],
            -30),

        new("kanji-compound-break-penalty-right",
            [ScoringCondition.CandidateIsSingleKanji],
            [ScoringCondition.NextIsSingleKanji],
            -30),

        new("conjunctive-particle-verb-link",
            [ScoringCondition.CandidateIsPredicateHost],
            [ScoringCondition.NextIsConditionalParticle],
            20),

        new("adv-to-to-synergy",
            [ScoringCondition.CandidateIsAdvTo],
            [ScoringCondition.NextIsToParticle],
            25),

        new("verb-te-form-aux-synergy",
            [ScoringCondition.CandidateIsVerb],
            [ScoringCondition.NextIsTeFormAux],
            25),

        new("noun-no-noun-synergy",
            [ScoringCondition.CandidateIsNounLike],
            [ScoringCondition.PrevIsNoParticle],
            20),

        // A 連用形 never follows genitive の (子供たちの群れ is the noun); real noun+verb compounds are fused upstream.
        new("genitive-infinitive-penalty",
            [ScoringCondition.CandidateIsVerb, ScoringCondition.CandidateIsInfinitiveOrImperative],
            [ScoringCondition.PrevIsNoParticle],
            -60),

        new("noun-adjacent-infinitive-penalty",
            [ScoringCondition.CandidateIsVerb, ScoringCondition.CandidateIsInfinitiveOrImperative],
            [ScoringCondition.PrevIsNounLike],
            -70),

        new("na-adj-no-connector-penalty",
            [ScoringCondition.CandidateIsNaAdj, ScoringCondition.CandidateIsNotAdverb],
            [ScoringCondition.NextIsNotNaAdjConnector],
            -20),

        new("verb-ba-form-conditional-synergy",
            [ScoringCondition.CandidateIsPredicateHost],
            [ScoringCondition.NextIsBaParticle],
            20),

        new("prenominal-adj-noun-synergy",
            [ScoringCondition.CandidateIsPrenounAdjectival],
            [ScoringCondition.NextIsNounLike],
            30),

        new("prenominal-adj-not-noun-penalty",
            [ScoringCondition.CandidateIsPrenounAdjectival],
            [ScoringCondition.NextIsNotNounLike],
            -200),

        new("conjunction-at-boundary-synergy",
            [ScoringCondition.CandidateIsConjunction],
            [ScoringCondition.IsSentenceInitial],
            15),

        new("interjection-at-boundary-synergy",
            [ScoringCondition.CandidateIsInterjection],
            [ScoringCondition.IsSentenceInitial],
            15),

        new("interjection-after-predicate-penalty",
            [ScoringCondition.CandidateIsInterjection],
            [ScoringCondition.PrevIsVerbAuxOrIAdj],
            -120),

        new("noun-suru-synergy",
            [ScoringCondition.CandidateIsSuruNoun],
            [ScoringCondition.NextIsSuru],
            25),

        new("verb-after-case-particle-synergy",
            [ScoringCondition.CandidateIsVerb],
            [ScoringCondition.PrevIsCaseParticle],
            35),

        new("noun-after-case-particle-demotion",
            [ScoringCondition.CandidateIsNounLike, ScoringCondition.CandidateIsNotAdverb],
            [ScoringCondition.PrevIsCaseParticle],
            -25),

        new("name-honorific-synergy",
            [ScoringCondition.CandidateIsName],
            [ScoringCondition.NextIsHonorific],
            20),

        new("verb-sentence-final-synergy",
            [ScoringCondition.CandidateIsPredicateHost],
            [ScoringCondition.IsSentenceFinal],
            10),

        new("adverb-before-noun-penalty",
            [ScoringCondition.CandidateIsAdverb, ScoringCondition.CandidateIsNotNounLike],
            [ScoringCondition.NextIsNounLike],
            -15),

        new("interjection-adverb-noun-exempt",
            [ScoringCondition.CandidateIsInterjection],
            [ScoringCondition.NextIsNounLike],
            15),

        new("suffix-after-noun-synergy",
            [ScoringCondition.CandidateIsNounSuffix],
            [ScoringCondition.PrevIsNounLike],
            15),

        new("honorific-after-name-synergy",
            [ScoringCondition.CandidateIsHonorific],
            [ScoringCondition.PrevIsName],
            30),

        new("noun-after-aux-penalty",
            [ScoringCondition.CandidateIsNounLike],
            [ScoringCondition.PrevIsAuxiliary],
            -45),

        new("particle-after-noun-synergy",
            [ScoringCondition.CandidateIsParticle, ScoringCondition.CandidateIsNotNounLike],
            [ScoringCondition.PrevIsNounLike],
            20),

        new("adverb-na-adj-synergy",
            [ScoringCondition.CandidateIsAdverb],
            [ScoringCondition.NextIsNaAdj],
            20),

        new("verb-after-quotation-to-synergy",
            [ScoringCondition.CandidateIsVerb],
            [ScoringCondition.PrevIsToParticle],
            20),

        new("volitional-to-suru-synergy",
            [ScoringCondition.CandidateIsVerb, ScoringCondition.CandidateHasVolitionalChain],
            [ScoringCondition.NextIsVolitionalToVerb],
            40),
    ];

    internal static readonly TransitionRule[] HardRules =
    [
        new(
            Id: "leading-aux-strip",
            WhenToken: [MatchCondition.IsSentenceInitial, MatchCondition.IsVerbAttachingAux],
            ValidIf: [],
            OnViolation: ViolationAction.RemoveCurrent),

        new(
            Id: "aux-must-follow-verb",
            WhenToken: [MatchCondition.IsVerbOnlyAux],
            ValidIf: [MatchCondition.PrevIsVerbOrAux],
            OnViolation: ViolationAction.MergeWithPrevious),

        new(
            Id: "verb-or-adj-aux-must-follow-content",
            WhenToken: [MatchCondition.IsVerbOrAdjAux],
            ValidIf: [MatchCondition.PrevIsVerbAuxIAdjOrSfp],
            OnViolation: ViolationAction.MergeWithPrevious),

        new(
            Id: "counter-must-follow-numberlike",
            WhenToken: [MatchCondition.IsCounter],
            ValidIf: [MatchCondition.PrevExists, MatchCondition.PrevIsNumericOrNoun],
            OnViolation: ViolationAction.ReclassifyCurrentAsNoun),

        // Plain predicates host an SFP in unpunctuated speech (欲しいな); ぞ/ぜ never merge (出るぞ無名！).
        new(
            Id: "sfp-must-be-near-clause-end",
            WhenToken: [MatchCondition.IsSentenceEndingParticle, MatchCondition.IsNotEmphaticSfp, MatchCondition.NextIsContentWord, MatchCondition.NextIsNotQuotative],
            ValidIf: [MatchCondition.PrevIsSfpValidHost],
            OnViolation: ViolationAction.MergeWithPrevious),

        new(
            Id: "prefix-at-sentence-end",
            WhenToken: [MatchCondition.IsPrefix, MatchCondition.IsSentenceFinal],
            ValidIf: [],
            OnViolation: ViolationAction.ReclassifyCurrentAsNoun),

        new(
            Id: "prefix-before-particle",
            WhenToken: [MatchCondition.IsPrefix, MatchCondition.NextIsParticle],
            ValidIf: [],
            OnViolation: ViolationAction.ReclassifyCurrentAsNoun),

        new(
            Id: "suffix-must-follow-content",
            WhenToken: [MatchCondition.IsSuffix, MatchCondition.IsSentenceInitial],
            ValidIf: [],
            OnViolation: ViolationAction.ReclassifyCurrentAsNoun),

        // A following content word makes it a valid fragment start (がいないと).
        new(
            Id: "particle-at-sentence-start",
            WhenToken: [MatchCondition.IsSentenceInitial, MatchCondition.IsStrictCaseMarkingParticle],
            ValidIf: [MatchCondition.NextIsContentWord],
            OnViolation: ViolationAction.RemoveCurrent),
    ];
}
