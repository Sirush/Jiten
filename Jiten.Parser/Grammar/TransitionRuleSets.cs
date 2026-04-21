using Jiten.Parser.Scoring;

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

    internal static readonly HashSet<string> HonorificSuffixes =
    [
        "さん", "くん", "ちゃん", "様", "殿", "氏"
    ];

    internal static readonly HashSet<string> NounSuffixes =
    [
        "的", "性", "化", "中", "用", "式", "風"
    ];

    // §11 port: o-prefix surfaces (お/御 attached to nouns — おにいちゃん, お下がり, etc.).
    // Ichiran treats the prefix as Prefix POS; adjacency with a following noun yields +10.
    internal static readonly HashSet<string> OPrefixes = ["お", "御"];

    // §11 port: kanji prefixes. Bound strongly to a following noun (未成年,
    // 不景気, 過剰). Ichiran gives +15 to kanji-prefix + noun.
    // Ichiran set: 未/不/過 (seqs 2242840, 1922780, 2423740) — dict-grammar.lisp kanji-prefix.
    internal static readonly HashSet<string> NegationKanjiPrefixes = ["未", "不", "過"];

    // §11 port: "ぶり" as a suffix is much more distinctive than the generic noun
    // suffix set (ぶり = duration-interval, e.g. 10年ぶり). Ichiran gives +40 vs the
    // generic +10-15. Tracked as a separate rule so the extra weight is additive.
    internal const string BuriSuffix = "ぶり";

    // Ichiran's `*noun-particles*` set (dict-grammar.lisp) — full parity port
    // by surface. Feeds the noun-particle synergy with formula 10 + 4*len(r).
    // Seqs: 2028920 は, 2028930 が, 2028990 に, 2028980 で, 2029000 へ,
    // 1007340 だけ, 1579080 ごろ, 1525680 まで, 2028940 も, 1582300 など,
    // 2215430 には, 1469800 の, 1009990 のみ, 2029010 を, 1005120 さえ/すら,
    // 2034520 でさえ, 1008490 と, 1008530 とか, 1008590 として, 2028950 とは,
    // 2028960 や, 1009600 にとって.
    internal static readonly HashSet<string> IchiranCompoundNounParticles =
    [
        "は", "が", "に", "で", "へ",
        "だけ", "ごろ", "まで", "も", "など",
        "には", "の", "のみ", "を", "さえ",
        "でさえ", "すら", "と", "とか", "として",
        "とは", "や", "にとって",
    ];

    // §13.1 shicha-ikenai right-hand surfaces (must be prohibition predicates).
    internal static readonly HashSet<string> ShichaIkenaiRightTexts =
    [
        "いけない", "いけません", "だめ", "ダメ", "いかん", "いや"
    ];

    // §13.3 penalty-semi-final: Ichiran's *semi-final-prt* = *final-prt* + (さ, し,
    // な, ね, わ). Particles with meaning only (or primarily) at clause-final
    // position. Seq IDs from dict-errata.lisp:*final-prt* / *semi-final-prt*.
    // Applied to a segment's compound seq-set (Splits.CompoundSeqSets[wordId])
    // or its own seq — penalty fires when the segment contains one of these AND
    // a right neighbour exists (i.e. it's not actually the final segment).
    internal static readonly HashSet<int> SemiFinalPrtSeqs =
    [
        // *final-prt*
        2017770, // かい
        2425930, // なの
        2130430, // け / っけ
        2029130, // ぞ
        2834812, // ぜ
        2718360, // がな
        2201380, // わい
        2722170, // のう
        2751630, // かいな
        // semi-final additions
        2029120, // さ
        2086640, // し
        2029110, // な
        2029080, // ね
        2029100, // わ
    ];

    // §13.1 na-adjective connector set. Ichiran's na-adjectives rule is な/に only
    // (dict-grammar.lisp). で is NOT a na-adj connector in Ichiran.
    internal static readonly HashSet<string> NaAdjConnectors = ["な", "に"];

    // §13.1 no-da right-hand copula set. Ichiran dict-grammar.lisp:848 uses the
    // seq-set {2089020 だ, 1007370 だけど, 1928670 だろう}. We approximate via text
    // since rule evaluation is text-based; extra entries (です/でしょう) kept for
    // our own coverage — they don't regress vs Ichiran because Ichiran's scoring
    // gives them bonuses via other paths.
    //
    // だから (1007310) included because in Ichiran's model it's a compound-text with
    // primary seq=2089020 (だ), so filter-in-seq-set against compound seq-set matches.
    // Our scorer doesn't track compound seq-sets, so we inline the match at the surface
    // level instead. Required for 魔術師|な|ん|だから to outscore 魔術師|なん|だから.
    internal static readonly HashSet<string> NoDaCopulas = ["だ", "です", "だろう", "でしょう", "だけど", "だから"];

    // Start-of-obligation compound surfaces (なければならない / なきゃいけない / なくてはいけない / ...)
    internal static readonly HashSet<string> ObligationStarts =
    [
        "なければ", "なきゃ", "なくては", "ねば", "なけりゃ"
    ];

    internal static readonly ScoringRule[] SoftRules =
    [
        new("noun-particle-synergy",
            [ScoringCondition.CandidateIsNounLike],
            [ScoringCondition.NextIsCommonParticle],
            40),

        new("noun-copula-synergy",
            [ScoringCondition.CandidateIsNounLike],
            [ScoringCondition.NextIsCopula],
            30),

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
            15),

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

        // Lesson 1a — shika particle binds strongly to a following predicate (typically negated).
        // We can't verify negation from the candidate alone, but しか+predicate is the correct shape.
        new("shika-predicate-cohesion",
            [ScoringCondition.CandidateIsPredicateHost],
            [ScoringCondition.PrevIsShikaParticle],
            25),

        // Lesson 1a — obligation compound start (なければ/なきゃ/なくては) follows a negative stem.
        // Binds the preceding predicate to the obligation fragment.
        new("obligation-start-synergy",
            [ScoringCondition.CandidateIsPredicateHost],
            [ScoringCondition.NextIsObligationStart],
            25),

        // Lesson 7b — two copula forms in a row are effectively impossible in modern Japanese.
        // Forbidden-grade penalty: dominates realistic score margins, but not a hard block.
        new("double-copula-penalty",
            [ScoringCondition.CandidateIsCopulaForm],
            [ScoringCondition.PrevIsCopulaForm],
            Constants.ForbiddenPenalty),
    ];

    // Ichiran §11 synergies — separate array from SoftRules, consumed only by the beam's
    // pure-Ichiran path. Kept distinct from SoftRules so additions here don't affect the
    // Sudachi-mode AdjacentWordScorer (which uses SoftRules raw for single-candidate
    // tiebreaking) or the hybrid beam path (which halves SoftRules to prevent accumulated
    // synergies from dominating additive node scores). In Ichiran mode node scores are
    // multiplicative (prop × length-multiplier, typically 200-800 per edge), so Ichiran's
    // native small-signal values (+10 / +15 / +40) are appropriately sized and don't need
    // halving or scaling. Ported from Ichiran's dict-grammar.lisp.
    internal static readonly ScoringRule[] IchiranSynergies =
    [
        // o-prefix (+10 Ichiran): mirrors Ichiran's
        // `(filter-is-pos ("n") (segment k p c l) (or k l))` — right must be pos=n
        // AND (has-kanji OR length ≥ 4). Narrower than NextIsNounLike, which
        // accepted Pronoun / Name / NaAdjective too.
        new("ichiran-o-prefix-noun-synergy",
            [ScoringCondition.CandidateIsOPrefix],
            [ScoringCondition.NextIsOPrefixEligibleNoun],
            10),

        // kanji-negation-prefix (+15 Ichiran): 未成年, 不景気, 非公式.
        new("ichiran-kanji-prefix-noun-synergy",
            [ScoringCondition.CandidateIsNegationKanjiPrefix],
            [ScoringCondition.NextIsNounLike],
            15),

        // buri kanji-suffix (+40 Ichiran): distinctive duration-interval binding.
        // 10年ぶり, 久しぶり.
        new("ichiran-kanji-suffix-buri-synergy",
            [ScoringCondition.CandidateIsBuriSuffix],
            [ScoringCondition.PrevIsSubstantiveNoun],
            40),

        // no-toori (+50 Ichiran): の + 通り binds as "as told / in accordance with".
        new("ichiran-no-toori-synergy",
            [ScoringCondition.CandidateIsToori],
            [ScoringCondition.PrevIsNoParticle],
            50),

        // counter-oki (+20 Ichiran): counter + おき/置き (一日おき, 3メートル置き).
        new("ichiran-counter-oki-synergy",
            [ScoringCondition.CandidateIsOki],
            [ScoringCondition.PrevIsCounter],
            20),

        // synergy-noun-particle is now ported as a length formula (10 + 4*len(r))
        // in TransitionRuleEngine.EvaluateLengthFormulas, covering the full
        // Ichiran *noun-particles* surface set.

        // §13.3 penalty-short (-9 Ichiran): 1-char kana × 1-char kana, both not と.
        // Ichiran's get-penalties returns ONE penalty per pair (dict-grammar.lisp:1011);
        // firing both left/right forms double-counted the signal per pair. Using only
        // the left variant so each pair is scored exactly once (via the right segment's
        // prev-context perspective). Matches Ichiran's per-pair accounting.
        new("ichiran-penalty-short-left",
            [ScoringCondition.CandidateIsShortKanaNotTo],
            [ScoringCondition.PrevIsShortKanaNotTo],
            -9),

        // §13.1 suffix-tachi (+10): noun + たち / 達 (friends, folks).
        new("ichiran-suffix-tachi-synergy",
            [ScoringCondition.CandidateIsTachiSuffix],
            [ScoringCondition.PrevIsSubstantiveNoun],
            10),

        // §13.1 suffix-chu (+12): noun + 中 / ちゅう (during, in progress).
        new("ichiran-suffix-chu-synergy",
            [ScoringCondition.CandidateIsChuSuffix],
            [ScoringCondition.PrevIsSubstantiveNoun],
            12),

        // §13.1 suffix-sei (+12): noun + 性 (-ness, -ity).
        new("ichiran-suffix-sei-synergy",
            [ScoringCondition.CandidateIsSeiSuffix],
            [ScoringCondition.PrevIsSubstantiveNoun],
            12),

        // §13.1 no-da (+15): の/んの + だ/です/だろう.
        // Explanatory-nominal predication. Ichiran's formula: raw +15.
        // Our existing SoftRule `no-da-synergy` gates on PrevIsVerbAuxOrIAdj +
        // NextIsCopula — a narrower pattern. This Ichiran port fires on plain
        // の-particle + copula regardless of what preceded の.
        new("ichiran-no-da-synergy",
            [ScoringCondition.CandidateIsNoOrNnoParticle],
            [ScoringCondition.NextIsDaDesuDaroo],
            15),

        // §13.1 no-adjectives (+15): adj-no (JMDict) + の.
        // Binds 鉄の, 金の, etc. — a noun that can modify another via の.
        new("ichiran-no-adjectives-synergy",
            [ScoringCondition.CandidateIsAdjNo],
            [ScoringCondition.NextIsNoParticle],
            15),

        // §13.1 na-adjectives (+15 Ichiran, raw): adj-na + な/に.
        new("ichiran-na-adjectives-synergy",
            [ScoringCondition.CandidateIsNaAdjForIchiran],
            [ScoringCondition.NextIsNaAdjConnector],
            15),

        // §13.1 shika-negative (+50): しか + negative conjugation on the right.
        // Approximated via right surface ending in ない/ねえ/ぬ/ん — any of
        // those forms signals a negated predicate. Full Ichiran port would
        // check conj-type, but surface approximation catches the common
        // patterns (しか…ない).
        new("ichiran-shika-negative-synergy",
            [ScoringCondition.CandidateIsShikaParticle],
            [ScoringCondition.NextIsNegativeConjugation],
            50),

        // §13.1 shicha-ikenai (+50): compound ending in は + (いけない/
        // いけません/だめ/いかん/いや). Binds ては-style prohibition. We
        // approximate "compound-end は" by "prev text ends with は" since
        // ScoringWindow doesn't carry compound-internal structure.
        new("ichiran-shicha-ikenai-synergy",
            [ScoringCondition.NextIsShichaIkenai],
            [ScoringCondition.PrevEndsWithHa],
            50),

        // §13.3 penalty-semi-final (-15 Ichiran): left segment's seq (or
        // compound seq-set) contains a *semi-final-prt* entry, AND a right
        // neighbour exists. Flags clause-final particles (さ/し/な/ね/わ/ぞ/ぜ/
        // かい/なの/け/がな/わい/のう/かいな) occurring mid-clause. Now seq-based
        // rather than surface-based, so it no longer over-fires on high-frequency
        // surfaces like の/こと/もの.
        //
        // Pair-penalty disambiguation: Ichiran's get-penalties (dict-grammar.lisp:1013)
        // iterates penalties in order and returns FIRST match. penalty-short comes
        // first. For a pair like (な, ん) where both are 1-char kana, penalty-short
        // fires (-9) and penalty-semi-final does NOT. Our rules fire on opposite
        // sides of the pair (short on R, semi-final on L), so without a guard we'd
        // stack both. PairNotBothShortKanaNotTo gates this rule out when the pair
        // would trigger penalty-short on the right-side evaluation.
        new("ichiran-penalty-semi-final",
            [ScoringCondition.CandidateIsSemiFinalParticle, ScoringCondition.PairNotBothShortKanaNotTo],
            [ScoringCondition.NextExists],
            -15),

        // §13.1 synergy-noun-da (+10 Ichiran): noun + だ (seq 2089020).
        // Ichiran's `(def-generic-synergy synergy-noun-da ... :score 10)` — fires
        // on substantive noun followed by the copula だ. Complements no-da-synergy
        // (which is の/んの + だ); this covers plain noun + だ.
        //
        // Gate: Ichiran's filter-is-noun requires (long-p OR kanji-p OR (primary-p
        // AND common-p)). For 2-char kana matches of kanji-primary words (e.g. なん
        // of 何, ord=1 in kana), filter-is-noun fails. Using the stricter
        // CandidateIsSubstantiveNounKanjiOrLong (HasKanji OR len>=3) approximates
        // this: 2-char kana pronouns are excluded while legitimate noun-da cases
        // (魔術師だ, 学生だ, 先生だ, 彼だ) still fire.
        new("ichiran-noun-da-synergy",
            [ScoringCondition.CandidateIsSubstantiveNounKanjiOrLong],
            [ScoringCondition.NextIsDaCopula],
            10),

        // §13.1 sou-nanda (+50): そう + なんだ (so that's it / I see).
        // Ichiran tags this a "hack" but keeps it in at +50 — it's the idiomatic
        // interjection that otherwise fragments into そう|な|ん|だ.
        new("ichiran-sou-nanda-synergy",
            [ScoringCondition.CandidateIsSou],
            [ScoringCondition.NextIsNanda],
            50),
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
        new(
            Id: "sfp-must-be-near-clause-end",
            Severity: RuleSeverity.Hard,
            WhenToken: [MatchCondition.IsSentenceEndingParticle, MatchCondition.NextIsContentWord],
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
