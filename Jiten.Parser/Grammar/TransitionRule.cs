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
    IsStrictCaseMarkingParticle, // Particle with DictionaryForm in StrictCaseMarkingParticles (が/を/へ)
    NextIsNotQuotative // Next token text does NOT start with quotative と (excludes embedded question patterns like かというと)
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
    CandidateIsNotHonorific,
    PrevIsName,
    PrevIsAuxiliary,
    NextIsNaAdj,
    PrevIsToParticle,
    CandidateIsNotName,
}

internal sealed record ScoringRule(
    string Id,
    ScoringCondition[] CandidateMatch,
    ScoringCondition[] ContextMatch,
    int Delta)
{
    internal uint RequiredCandidateMask { get; init; } = ComputeRequiredCandidateMask(CandidateMatch);

    private static uint ComputeRequiredCandidateMask(ScoringCondition[] conditions)
    {
        uint mask = 0;
        foreach (var c in conditions)
        {
            mask |= c switch
            {
                ScoringCondition.CandidateIsNounLike => PosMask.NounLike,
                ScoringCondition.CandidateIsNaAdj => PosMask.NaAdjective,
                ScoringCondition.CandidateIsAdverb => PosMask.AdverbGroup,
                ScoringCondition.CandidateIsAuxiliary => PosMask.Auxiliary,
                ScoringCondition.CandidateIsParticle => PosMask.Particle,
                ScoringCondition.CandidateIsPredicateHost => PosMask.PredicateHost,
                ScoringCondition.CandidateIsCounter => PosMask.Counter,
                ScoringCondition.CandidateIsAdvTo => PosMask.AdverbTo,
                ScoringCondition.CandidateIsVerb => PosMask.Verb,
                ScoringCondition.CandidateIsPrenounAdjectival => PosMask.PrenounAdjectival,
                ScoringCondition.CandidateIsConjunction => PosMask.Conjunction,
                ScoringCondition.CandidateIsInterjection => PosMask.Interjection,
                ScoringCondition.CandidateIsName => PosMask.NameBit,
                ScoringCondition.CandidateIsNoParticle => PosMask.Particle,
                ScoringCondition.CandidateIsNounSuffix => PosMask.SuffixGroup,
                ScoringCondition.CandidateIsHonorific => PosMask.SuffixGroup,
                _ => 0u
            };
        }
        return mask;
    }
}

internal readonly record struct ScoringWindow(
    FormCandidate Candidate,
    uint PrevMask,
    bool HasPrev,
    string? PrevText,
    uint NextMask,
    bool HasNext,
    string? NextText);
