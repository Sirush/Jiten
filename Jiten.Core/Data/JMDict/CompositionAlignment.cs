namespace Jiten.Core.Data.JMDict;

/// <param name="Reading">Hiragana reading of the surface within its parent form, or null when the form's ruby cannot place it.</param>
public readonly record struct CompositionSlot(int WordId, short ReadingIndex, string Surface, bool IsGrammatical, string? Reading = null)
{
    public bool HasKanji => Surface.Any(JapaneseTextHelper.IsKanji);
}

public static class CompositionAlignment
{
    /// <summary>
    /// The component word that the kanji-surface slots of sibling forms pin for this kana slot, or null when no sibling
    /// offers one, siblings disagree, or a kanji sibling already confirms the current word.
    /// </summary>
    /// <param name="canTake">Whether the sibling slot (second) may replace the word of the kana slot (first).</param>
    public static int? FindKanjiDonor(IReadOnlyList<CompositionSlot> form, int index,
                                      IEnumerable<IReadOnlyList<CompositionSlot>> siblings, Func<CompositionSlot, CompositionSlot, bool> canTake)
    {
        var slot = form[index];
        if (slot.IsGrammatical || slot.HasKanji) return null;

        int? donor = null;
        foreach (var sibling in siblings)
        {
            if (sibling.Any(s => s.WordId == slot.WordId))
            {
                if (sibling.Any(s => s.WordId == slot.WordId && s.HasKanji)) return null;
                continue;
            }

            for (int j = 0; j < sibling.Count; j++)
            {
                var candidate = sibling[j];
                if (candidate.IsGrammatical || !candidate.HasKanji || !canTake(slot, candidate)) continue;
                if (!PreservesOrder(form, index, sibling, j)) continue;
                if (donor != null && donor != candidate.WordId) return null;
                donor = candidate.WordId;
            }
        }

        return donor;
    }

    /// <summary>Pairing slot i with sibling slot j must not cross a pair of slots that already share a word.</summary>
    private static bool PreservesOrder(IReadOnlyList<CompositionSlot> form, int i, IReadOnlyList<CompositionSlot> sibling, int j)
    {
        for (int a = 0; a < form.Count; a++)
        {
            if (a == i) continue;
            for (int b = 0; b < sibling.Count; b++)
            {
                if (sibling[b].WordId != form[a].WordId) continue;
                if (b == j || (a < i) != (b < j)) return false;
            }
        }

        return true;
    }
}
