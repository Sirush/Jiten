namespace Jiten.Core.Data.User;

/// <summary>
/// Senses the user has hidden for a word, as a bitmask over the 1-based definition index shown in the UI
/// (sense 1 = bit 0), so indices above <see cref="MaxDefinitionIndex"/> cannot be hidden. Applies to the
/// whole word, not a single reading. A row with a zero mask is deleted rather than stored.
/// </summary>
public class UserHiddenDefinition
{
    public const int MaxDefinitionIndex = 63;

    public string UserId { get; set; } = default!;
    public int WordId { get; set; }
    public long HiddenMask { get; set; }

    public static long ToMask(IEnumerable<int> indices)
    {
        long mask = 0;
        foreach (var index in indices)
        {
            if (index is < 1 or > MaxDefinitionIndex) continue;
            mask |= 1L << (index - 1);
        }

        return mask;
    }

    public static List<int> ToIndices(long mask)
    {
        var indices = new List<int>();
        for (var bit = 0; bit < MaxDefinitionIndex; bit++)
        {
            if ((mask & (1L << bit)) != 0)
                indices.Add(bit + 1);
        }

        return indices;
    }
}
