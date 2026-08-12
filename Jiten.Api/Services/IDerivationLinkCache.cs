using Jiten.Core.Data.JMDict;

namespace Jiten.Api.Services;

/// <summary>A key that covers another. ViaCategory is the edge touching the covered key, so the UI can name
/// the grammar point even when the covering card sits two hops away through the family's base word.</summary>
public readonly record struct DerivationCover(
    int WordId,
    byte ReadingIndex,
    DerivationCategory ViaCategory);

public readonly record struct DerivationLink(
    int WordId,
    byte ReadingIndex,
    DerivationCategory Category,
    DerivationDirection Direction);

public interface IDerivationLinkCache
{
    bool IsEmpty { get; }

    /// <summary>Every key whose knowledge covers this one, walking only enabled categories. Coverage is
    /// transitive through the family's base word, so an enabled path of any length conducts.</summary>
    IReadOnlyList<DerivationCover> GetCoveringKeys(int wordId, byte readingIndex,
                                                    IReadOnlySet<DerivationCategory> categories);

    /// <summary>The mirror of <see cref="GetCoveringKeys"/>: every key made redundant by knowing this one.</summary>
    IReadOnlyList<DerivationCover> GetCoveredKeys(int wordId, byte readingIndex,
                                                   IReadOnlySet<DerivationCategory> categories);

    /// <summary>Entries this form is derived from.</summary>
    IReadOnlyList<DerivationLink> GetBaseLinks(int wordId, byte readingIndex);

    /// <summary>Entries derived from this form.</summary>
    IReadOnlyList<DerivationLink> GetDerivedLinks(int wordId, byte readingIndex);

    IReadOnlyDictionary<DerivationCategory, int> PairCounts { get; }

    void Reload();
}
