using Jiten.Core.Data;
using Jiten.Parser.Grammar;

namespace Jiten.Parser.Scoring;

internal static class AdjacentWordScorer
{
    internal readonly record struct AdjacentContext(
        List<PartOfSpeech>? PrevResolvedPOS,
        List<PartOfSpeech>? NextResolvedPOS,
        string? PrevText,
        string? NextText,
        IReadOnlyList<string>? NextConjChain = null);

    internal static (int bonus, List<string> rulesMatched) CalculateContextBonus(
        FormCandidate candidate,
        AdjacentContext context)
    {
        var window = new ScoringWindow(
            candidate,
            context.PrevResolvedPOS,
            context.NextResolvedPOS,
            context.PrevText,
            context.NextText,
            context.NextConjChain);

        return TransitionRuleEngine.EvaluateSoftRules(window);
    }

    // Ichiran-mode synergies — separate channel used only by the pure-Ichiran beam.
    // Returns a raw additive bonus (no halving); SoftRules remain the Sudachi-mode
    // tiebreaker path and are unaffected.
    internal static int CalculateIchiranSynergies(
        FormCandidate candidate,
        AdjacentContext context)
    {
        var window = new ScoringWindow(
            candidate,
            context.PrevResolvedPOS,
            context.NextResolvedPOS,
            context.PrevText,
            context.NextText,
            context.NextConjChain);

        return TransitionRuleEngine.EvaluateIchiranSynergies(window);
    }
}
