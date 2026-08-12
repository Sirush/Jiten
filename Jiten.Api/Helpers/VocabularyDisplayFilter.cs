using Jiten.Core.Data;

namespace Jiten.Api.Helpers;

public enum VocabularyTier
{
    Unknown,
    Learning,
    Young,
    Mature,
    Mastered,
    Blacklisted
}

/// <summary>Whether a modifier state (suspended, redundant) is hidden, tolerated, or required.</summary>
public enum ModifierMode
{
    Show,
    Hide,
    Only
}

/// <summary>
/// The vocabulary list's Display control: an OR-ed set of tiers plus a mode per modifier state.
/// Suspended and Redundant ride on top of a tier instead of replacing it, so they gate the result
/// rather than joining the tier set.
/// </summary>
public sealed class VocabularyDisplayFilter
{
    private static readonly VocabularyTier[] KnownTiers =
        [VocabularyTier.Learning, VocabularyTier.Young, VocabularyTier.Mature, VocabularyTier.Mastered, VocabularyTier.Blacklisted];

    private readonly HashSet<VocabularyTier> _tiers = [];

    public IReadOnlyCollection<VocabularyTier> Tiers => _tiers;
    public ModifierMode Suspended { get; private init; }
    public ModifierMode Redundant { get; private init; }

    public bool IsActive => _tiers.Count > 0 || Suspended != ModifierMode.Show || Redundant != ModifierMode.Show;

    public static VocabularyDisplayFilter Parse(string? displayFilter, string? suspended, string? redundant)
    {
        var filter = new VocabularyDisplayFilter
        {
            Suspended = ParseMode(suspended),
            Redundant = ParseMode(redundant)
        };

        foreach (var token in VocabularyFilterHelper.ParseCommaSeparatedTags(displayFilter))
        {
            switch (token.ToLowerInvariant())
            {
                case "all":
                    filter._tiers.Clear();
                    return filter;
                // Pre-checkbox links sent a single value; "known" was every tier except Unknown.
                case "known":
                    foreach (var tier in KnownTiers) filter._tiers.Add(tier);
                    break;
                case "unknown" or "new":
                    filter._tiers.Add(VocabularyTier.Unknown);
                    break;
                case "learning":
                    filter._tiers.Add(VocabularyTier.Learning);
                    break;
                case "young":
                    filter._tiers.Add(VocabularyTier.Young);
                    break;
                case "mature":
                    filter._tiers.Add(VocabularyTier.Mature);
                    break;
                case "mastered":
                    filter._tiers.Add(VocabularyTier.Mastered);
                    break;
                case "blacklisted":
                    filter._tiers.Add(VocabularyTier.Blacklisted);
                    break;
            }
        }

        return filter;
    }

    private static ModifierMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "hide" => ModifierMode.Hide,
        "only" => ModifierMode.Only,
        _ => ModifierMode.Show
    };

    public bool Matches(IReadOnlyCollection<KnownState> states)
    {
        if (!MatchesMode(Suspended, states.Contains(KnownState.Suspended))) return false;
        if (!MatchesMode(Redundant, states.Contains(KnownState.Redundant))) return false;

        return _tiers.Count == 0 || _tiers.Contains(ResolveTier(states));
    }

    private static bool MatchesMode(ModifierMode mode, bool present) => mode switch
    {
        ModifierMode.Hide => !present,
        ModifierMode.Only => present,
        _ => true
    };

    /// <summary>
    /// Collapses a word's state list onto the single tier the Display control offers.
    /// A redundant form carrying no inherited tier is covered by a card that was never reviewed, so it
    /// belongs with Learning: its New is a placeholder for that card, not a word the user does not know.
    /// </summary>
    public static VocabularyTier ResolveTier(IReadOnlyCollection<KnownState> states)
    {
        if (states.Contains(KnownState.Blacklisted)) return VocabularyTier.Blacklisted;
        if (states.Contains(KnownState.Mastered)) return VocabularyTier.Mastered;
        if (states.Contains(KnownState.Mature)) return VocabularyTier.Mature;
        if (states.Contains(KnownState.Young)) return VocabularyTier.Young;
        if (states.Contains(KnownState.Redundant)) return VocabularyTier.Learning;
        if (states.Contains(KnownState.New)) return VocabularyTier.Unknown;
        return VocabularyTier.Learning;
    }
}
