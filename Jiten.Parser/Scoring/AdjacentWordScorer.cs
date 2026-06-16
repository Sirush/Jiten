using Jiten.Core.Data;
using Jiten.Parser.Grammar;

namespace Jiten.Parser.Scoring;

internal static class AdjacentWordScorer
{
    internal readonly record struct AdjacentContext(
        uint PrevMask,
        bool HasPrev,
        string? PrevText,
        uint NextMask,
        bool HasNext,
        string? NextText,
        ulong ApplicableRuleMask)
    {
        public static AdjacentContext Create(
            List<PartOfSpeech>? prevPOS, string? prevText,
            List<PartOfSpeech>? nextPOS, string? nextText)
        {
            uint prevMask = prevPOS != null ? PosMask.FromList(prevPOS) : 0;
            bool hasPrev = prevPOS != null;
            uint nextMask = nextPOS != null ? PosMask.FromList(nextPOS) : 0;
            bool hasNext = nextPOS != null;

            // Soft-rule ContextMatch depends only on context, so compute the applicable-rule set once
            // per token here rather than re-evaluating it for every candidate in EvaluateSoftRules.
            ulong applicable = TransitionRuleEngine.ComputeContextApplicableMask(
                prevMask, hasPrev, prevText, nextMask, hasNext, nextText);

            return new(prevMask, hasPrev, prevText, nextMask, hasNext, nextText, applicable);
        }
    }

    internal static int CalculateContextBonus(
        FormCandidate candidate,
        AdjacentContext context,
        List<string>? rulesMatched = null)
    {
        var window = new ScoringWindow(
            candidate,
            context.PrevMask, context.HasPrev, context.PrevText,
            context.NextMask, context.HasNext, context.NextText);

        return TransitionRuleEngine.EvaluateSoftRules(window, context.ApplicableRuleMask, rulesMatched);
    }
}
