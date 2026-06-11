namespace Jiten.Parser.Grammar;

internal static class TransitionRuleSets
{
    // Auxiliaries that can only attach to verbs (passive/causative/desire/polite)
    internal static readonly HashSet<string> VerbOnlyAuxDictForms =
    [
        "られる", "れる", "せる", "させる",
        "たい", "たがる",
        "ます"
    ];

    // Auxiliaries that can attach to verbs OR i-adjectives (past, negative)
    internal static readonly HashSet<string> VerbOrAdjAuxDictForms = ["た", "ぬ"];

    internal static readonly HashSet<string> CommonParticles =
    [
        "が", "を", "に", "で", "へ", "は", "の", "も", "や",
        "から", "まで", "より", "だけ", "しか", "ばかり", "など", "さえ"
    ];

    internal static readonly HashSet<string> CaseMarkingParticles = ["が", "を", "に", "で", "へ"];

    // Strictly impossible case-marking particles at sentence start (topic/conjunctive particles excluded)
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

    // 〜(volitional)とする "try to" — the と+する after a volitional ending demands the
    // volitional reading (走り出そう = 走り出す volitional, not 走り出る + seemingness そう)
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

    // Noun→verb collocations: when a nearby resolved token matches the key wordId,
    // the target verb candidate gets a scoring bonus to beat homophones.
    // (contextWordId) → (targetWordId, bonus)
    internal static readonly Dictionary<int, (int TargetWordId, int Bonus)> NounVerbCollocations = new()
    {
        { 1172400, (1444150, 30) }, // 嘘 → 吐く (to tell a lie), not 点く/付く
        { 1597190, (1444150, 30) }, // ため息 → 吐く (to sigh), not 付く
        { 1371260, (1229610, 30) }, // 水 → 汲む (to draw water), not 組む
    };

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

        new("numeral-counter-cohesion",
            [ScoringCondition.CandidateIsCounter],
            [ScoringCondition.PrevIsNumeral],
            40),

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

        // A 連用形 cannot directly follow the genitive の (子供たちの群れ is the noun 群れ,
        // never 群れる's infinitive); after a bare noun it's nearly as implausible
        // (群れ、群れ、群れ enumerations) — real compounds are fused upstream.
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

    // Parity rules encoding current ValidateGrammaticalSequences behavior (phases 1–3)
    internal static readonly TransitionRule[] HardRules =
    [
        // Phase 1: leading auxiliaries can never begin a clause
        new(
            Id: "leading-aux-strip",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsSentenceInitial, MatchCondition.IsVerbAttachingAux],
            ValidIf: [],
            OnViolation: ViolationAction.RemoveCurrent),

        // Phase 2a: passive/causative/desire/polite/polite-past aux must follow verb or aux
        new(
            Id: "aux-must-follow-verb",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsVerbOnlyAux],
            ValidIf: [MatchCondition.PrevIsVerbOrAux],
            OnViolation: ViolationAction.MergeWithPrevious),

        // Phase 2b: past/negative aux must follow verb, aux, i-adjective, or sentence-ending particle
        new(
            Id: "verb-or-adj-aux-must-follow-content",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsVerbOrAdjAux],
            ValidIf: [MatchCondition.PrevIsVerbAuxIAdjOrSfp],
            OnViolation: ViolationAction.MergeWithPrevious),

        // Phase 3: counter suffix must follow a number or noun-like token
        new(
            Id: "counter-must-follow-numberlike",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsCounter],
            ValidIf: [MatchCondition.PrevExists, MatchCondition.PrevIsNumericOrNoun],
            OnViolation: ViolationAction.ReclassifyCurrentAsNoun),

        // Phase 4: sentence-final particles (よ/ね/な/ぞ/ぜ/わ) must be near clause end
        // Exception: SFP after an auxiliary or particle (e.g. だな, はね) is valid
        // Emphatic ぞ/ぜ are excluded: they never merge into a longer expression, and vocatives
        // follow them directly (出るぞ無名！) — merging would just delete the particle.
        new(
            Id: "sfp-must-be-near-clause-end",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsSentenceEndingParticle, MatchCondition.IsNotEmphaticSfp, MatchCondition.NextIsContentWord, MatchCondition.NextIsNotQuotative],
            ValidIf: [MatchCondition.PrevIsAuxiliaryOrParticle],
            OnViolation: ViolationAction.MergeWithPrevious),

        // Phase 5a: prefix at sentence-end is almost always a misparse → reclassify as noun
        new(
            Id: "prefix-at-sentence-end",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsPrefix, MatchCondition.IsSentenceFinal],
            ValidIf: [],
            OnViolation: ViolationAction.ReclassifyCurrentAsNoun),

        // Phase 5b: prefix before a particle is almost always a misparse → reclassify as noun
        new(
            Id: "prefix-before-particle",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsPrefix, MatchCondition.NextIsParticle],
            ValidIf: [],
            OnViolation: ViolationAction.ReclassifyCurrentAsNoun),

        // Phase 6: suffix at sentence start has no content to attach to → reclassify as noun
        new(
            Id: "suffix-must-follow-content",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsSuffix, MatchCondition.IsSentenceInitial],
            ValidIf: [],
            OnViolation: ViolationAction.ReclassifyCurrentAsNoun),

        // Phase 7: case-marking particles (を/が/へ) at sentence start are almost always misparsed
        // Topic/conjunctive particles (は/も/で/でも/けど) can legitimately start sentences
        // Exception: if followed by a content word, the fragment is a valid sentence-start (e.g. がいないと)
        new(
            Id: "particle-at-sentence-start",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsSentenceInitial, MatchCondition.IsStrictCaseMarkingParticle],
            ValidIf: [MatchCondition.NextIsContentWord],
            OnViolation: ViolationAction.RemoveCurrent),
    ];
}
