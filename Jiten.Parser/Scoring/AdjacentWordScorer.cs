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

            // JMDict tags ordinals as plain nouns (第二 [n]), but counters after them read as after a numeral (第二話 = だいにわ).
            if (hasPrev && IsNumericSurface(prevText))
                prevMask |= PosMask.Numeral;

            // Soft-rule ContextMatch depends only on context, so it is computed once per token, not per candidate.
            ulong applicable = TransitionRuleEngine.ComputeContextApplicableMask(
                prevMask, hasPrev, prevText, nextMask, hasNext, nextText);

            return new(prevMask, hasPrev, prevText, nextMask, hasNext, nextText, applicable);
        }
    }

    // Digits of any script, optionally opened by 第 or the quantity interrogative/approximator 何 or 数 (何話, 数分).
    internal static bool IsNumericSurface(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int i = text[0] is '第' or '何' or '数' ? 1 : 0;
        if (i == text.Length) return text[0] is '何' or '数';
        for (; i < text.Length; i++)
        {
            if (!Jiten.Core.JapaneseTextHelper.IsNumeralChar(text[i])) return false;
        }

        return true;
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
