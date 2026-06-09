namespace Jiten.Api.Services;

public class WordFormInfo
{
    /// <summary>Forward adjacency: source reading index -> reading indexes it renders redundant.</summary>
    public Dictionary<byte, byte[]> RedundantBySource { get; init; } = new();

    /// <summary>Reverse adjacency: redundant reading index -> source reading indexes that dominate it.</summary>
    public Dictionary<byte, byte[]> SourcesByRedundant { get; init; } = new();
}

public interface IWordFormSiblingCache
{
    /// <summary>
    /// Reading indexes that become redundant when the form at <paramref name="readingIndex"/> is known
    /// (forward redundancy adjacency), or null if knowing it makes nothing redundant.
    /// </summary>
    byte[]? GetKanaIndexesForKanji(int wordId, byte readingIndex);

    /// <summary>
    /// Reading indexes whose knowledge makes the form at <paramref name="readingIndex"/> redundant
    /// (reverse redundancy adjacency), or null if it is never redundant.
    /// </summary>
    byte[]? GetKanjiIndexesForKana(int wordId, byte readingIndex);

    WordFormInfo? GetWordFormInfo(int wordId);
    void Reload();
}
