using Jiten.Core.Data;
using Jiten.Parser;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Grammar;

internal enum RuleSeverity { Hard, Soft }

internal enum ViolationAction
{
    None,
    RemoveCurrent,
    MergeWithPrevious,
    ReclassifyCurrentAsNoun,
    RequestResegmentation
}

internal enum MatchCondition
{
    IsVerbOnlyAux,       // Auxiliary with DictionaryForm in VerbOnlyAuxDictForms
    IsVerbOrAdjAux,      // Auxiliary with DictionaryForm in VerbOrAdjAuxDictForms
    IsVerbAttachingAux,  // IsVerbOnlyAux || IsVerbOrAdjAux (for leading-strip rule)
    IsAuxiliary,         // PartOfSpeech == Auxiliary
    IsCounter,           // PartOfSpeech == Counter || (Suffix with Counter section)
    IsSentenceInitial,   // Index == 0
    PrevIsVerbOrAux,             // Prev.PartOfSpeech is Verb or Auxiliary
    PrevIsVerbAuxOrIAdj,         // Prev.PartOfSpeech is Verb, Auxiliary, or IAdjective
    PrevIsVerbAuxIAdjOrSfp,      // Prev.PartOfSpeech is Verb, Auxiliary, IAdjective, or sentence-ending particle
    PrevIsAuxiliary,             // Prev.PartOfSpeech is Auxiliary
    PrevIsAuxiliaryOrParticle,   // Prev.PartOfSpeech is Auxiliary or Particle
    PrevIsNumericOrNoun, // Prev.PartOfSpeech is Numeral, Noun, CommonNoun, Pronoun, or Name
    PrevExists,          // Prev != null
    IsSentenceEndingParticle, // PartOfSpeech == Particle && Section == SentenceEndingParticle
    NextIsContentWord,   // Next.PartOfSpeech is a content-bearing POS (noun/verb/adj/adverb/etc.)
    IsPrefix,            // PartOfSpeech == Prefix
    IsSentenceFinal,     // Index == Count - 1
    NextIsParticle,      // Next.PartOfSpeech == Particle
    IsSuffix,            // PartOfSpeech == Suffix
    IsStrictCaseMarkingParticle // Particle with DictionaryForm in StrictCaseMarkingParticles (が/を/へ)
}

internal sealed record TransitionRule(
    string Id,
    RuleSeverity Severity,
    MatchCondition[] WhenToken,
    MatchCondition[] ValidIf,
    ViolationAction OnViolation,
    int SoftDelta = 0);

internal readonly record struct TokenWindow(
    WordInfo? Prev,
    WordInfo Current,
    WordInfo? Next,
    int Index,
    int Count);

internal enum ScoringCondition
{
    CandidateIsNounLike,
    CandidateIsNaAdj,
    CandidateIsAdverb,
    CandidateIsAuxiliary,
    CandidateIsParticle,
    CandidateIsSingleKanaNonParticle,

    NextIsCommonParticle,
    NextIsCopula,
    NextIsNaConnector,
    NextIsVerbOrIAdj,
    PrevIsVerbOrIAdj,
    PrevIsParticle,
    NextIsParticle,
    PrevIsSingleKanaNonParticle,
    NextIsSingleKanaNonParticle,
    CandidateIsPredicateHost,
    CandidateIsNoParticle,
    NextIsExplanatoryN,
    PrevIsVerbAuxOrIAdj,
    CandidateIsCounter,
    PrevIsNumeral,
    PrevIsNotNumericLike,
    CandidateIsSingleKanji,
    PrevIsSingleKanji,
    NextIsSingleKanji,
    NextIsConditionalParticle,
    CandidateIsAdvTo,
    NextIsToParticle,
    CandidateIsVerb,
    NextIsTeFormAux,
    PrevIsNoParticle,
    NextIsNotNaAdjConnector,
    NextIsBaParticle,
    CandidateIsPrenounAdjectival,
    NextIsNounLike,
    CandidateIsConjunction,
    IsSentenceInitial,
    CandidateIsInterjection,
    CandidateIsSuruNoun,
    NextIsSuru,
    PrevIsCaseParticle,
    CandidateIsName,
    NextIsHonorific,
    IsSentenceFinal,
    CandidateIsNounSuffix,
    PrevIsNounLike,
    CandidateIsNotNounLike,
    CandidateIsNotAdverb,
    NextIsNotNounLike,
    CandidateIsHonorific,
    PrevIsName,
    PrevIsAuxiliary,
    PrevIsShikaParticle,
    NextIsObligationStart,
    CandidateIsCopulaForm,
    PrevIsCopulaForm,

    // §11 synergy ports (Ichiran dict-grammar.lisp).
    CandidateIsOPrefix,           // お / 御 + Prefix POS
    CandidateIsNegationKanjiPrefix, // 未 / 不 / 非 / 反 + Prefix POS
    CandidateIsBuriSuffix,        // ぶり + Suffix / NounSuffix POS
    CandidateIsToori,             // 通り following の-particle (no-toori-synergy)
    PrevIsCounter,                // Prev POS is Counter
    CandidateIsOki,               // おき / 置き — counter+おき cohesion

    // Right is a JMDict "n" noun AND (has kanji OR length ≥ 4).
    // Mirrors Ichiran's `(filter-is-pos ("n") (segment k p c l) (or k l))` gate
    // on synergy-o-prefix. Narrower than NextIsNounLike, which accepts Pronoun /
    // Name / NaAdjective / NominalAdjective — Ichiran's kpcl test fires only on
    // plain nouns with substance (kanji content or long-enough kana).
    NextIsOPrefixEligibleNoun,

    // Candidate POS is noun-like AND the surface is "substantive" — either has
    // kanji content, or length ≥ 2. Mirrors Ichiran's filter-is-noun gate
    // (`long-p OR kanji-p OR ...`) — screens out 1-char kana readings of kanji
    // nouns (e.g. 出's kana reading で) that would otherwise pull compound-noun
    // synergies onto an unintended split.
    CandidateIsSubstantiveNoun,
    PrevIsSubstantiveNoun,

    // Single-char kana (hiragana/katakana), excluding と. Ichiran's penalty-short
    // fires when BOTH the left and right segments satisfy this predicate — a -9
    // signal that targets kana noise chains without the stronger -40 SoftRule
    // treatment of CandidateIsSingleKanaNonParticle (which excludes particles).
    CandidateIsShortKanaNotTo,
    PrevIsShortKanaNotTo,
    NextIsShortKanaNotTo,

    // Ichiran §13.1 suffix-chu / suffix-tachi / suffix-sei / sou-nanda surfaces.
    // Single-surface conditions kept distinct from NounSuffixes so the raw
    // Ichiran weights (+10/+12/+50) don't compete with our generic suffix signal.
    CandidateIsTachiSuffix,
    CandidateIsChuSuffix,
    CandidateIsSeiSuffix,
    CandidateIsSou,
    NextIsNanda,

    // Right text is one of Ichiran's compound noun-attaching particles (see
    // TransitionRuleSets.IchiranCompoundNounParticles for the authoritative list).
    // Ichiran's `*noun-particles*` list covers both simple (は, が, で) and
    // compound particles as a unit — our CommonParticles set only has the simple
    // ones, leaving the compound particles under-merged. Checked alongside
    // CandidateIsNounLike in an IchiranSynergies rule so noun+compound-particle
    // wins over noun+particle+particle splits. Accepts Particle / Auxiliary /
    // Expression POS since JMDict tags some entries (e.g. ごとき = aux-v) outside
    // the narrow Particle bucket.
    NextIsIchiranCompoundNounParticle,

    // Ichiran §13.1 additional synergy ports.
    CandidateIsNoOrNnoParticle,       // Left ∈ {の, ん} — Ichiran synergy-no-da seq-set {1469800, 2139720}
    NextIsDaDesuDaroo,                // Right text ∈ {だ, です, だろう}
    CandidateIsAdjNo,                 // JMDict POS "adj-no" on candidate
    NextIsNoParticle,                 // Next text = の (the particle)
    CandidateIsNaAdjForIchiran,       // JMDict POS "adj-na" — used for na-adj + な/に/で binding
    NextIsNaAdjConnector,             // Next text ∈ {な, に, で}
    NextIsToParticleExact,            // Next text = と (particle)
    CandidateIsShikaParticle,         // Left = しか
    NextIsNegativeConjugation,        // Right's form is a negative conjugation (best-effort: text ending ない/ねえ/ぬ/ん)
    PrevEndsWithHa,                   // Prev text ends with は (approximation of compound-end は)
    NextIsShichaIkenai,               // Right text ∈ {いけない, いけません, だめ, いかん, いや}
    CandidateIsSemiFinalParticle,     // Seq ∈ *semi-final-prt* (or compound seq-set overlaps)
    NextExists,                       // A right neighbour exists (not clause-final)
    NextIsDaCopula,                   // Right surface = だ and POS includes copula/auxiliary

    // Pair-penalty disambiguation: true when NOT (candidate is 1-char kana-not-to AND
    // next is 1-char kana-not-to). Used to gate penalty-semi-final so penalty-short
    // wins first per Ichiran's get-penalties "first-match-returns" semantics
    // (dict-grammar.lisp:1013).
    PairNotBothShortKanaNotTo,

    // Tighter noun gate matching Ichiran's filter-is-noun (dict-grammar.lisp): requires
    // noun-like POS AND (has-kanji OR length >= 3). Excludes 2-char kana pronoun matches
    // of kanji-primary words (e.g. なん for 何) from firing noun-da synergy. Less lax than
    // CandidateIsSubstantiveNoun which accepts 2-char kana outright.
    CandidateIsSubstantiveNounKanjiOrLong,
}

internal sealed record ScoringRule(
    string Id,
    ScoringCondition[] CandidateMatch,
    ScoringCondition[] ContextMatch,
    int Delta);

internal readonly record struct ScoringWindow(
    FormCandidate Candidate,
    List<PartOfSpeech>? PrevResolvedPOS,
    List<PartOfSpeech>? NextResolvedPOS,
    string? PrevText,
    string? NextText,
    IReadOnlyList<string>? NextConjChain = null);
