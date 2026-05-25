using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Scoring;

/// <summary>
/// Code-defined priority overrides applied on top of DB-loaded JMDict words.
/// Equivalent to Ichiran's dict-errata set-common — reproducible without DB changes.
/// Supports both word-level and per-form (readingIndex) overrides.
/// </summary>
internal static class PriorityOverrides
{
    private static readonly HashSet<int> WordLevelJitenIds =
    [
        1204860, // 各 (かく, pref) — prefix "each", beats 各々 おのおの by 1pt
        1545300, // 妖怪 (ようかい, n) — ghost/yokai, beats 溶解 (dissolution) whose NormalizedForm bonus inflates its score
        1922120, // 兼ねない (かねない, exp/suf) — "might", standalone beats conjugated 兼ねる
        2579880, // コホン/こほん (int) — cough/ahem onomatopoeia, beats 古本 こほん (secondhand book)
    ];

    private static readonly HashSet<(int WordId, byte ReadingIndex)> FormLevelJitenIds =
    [
        (1168660, 4), // 依る reading index 4 = よる (kana) — most common kana-only よる, beats 寄る
        (1313580, 2), // 事 reading index 2 = こと (kana) — top-frequency nominalizer, beats 琴 (instrument)
        (1495740, 2), // 付く reading index 2 = つく (kana) — most general つく, beats 点く (to be lit)
        (1508300, 2), // 柄 reading index 2 = ガラ (katakana) — "character/nature", exempts from short-kana gate
        (2013900, 4), // 赤 reading index 4 = あか (kana) — "red", beats 垢 (dirt) and 銅 (copper) homophones
    ];

    public static void Apply(JmDictWord word)
    {
        if (WordLevelJitenIds.Contains(word.WordId))
        {
            word.Priorities ??= [];
            if (!word.Priorities.Contains("jiten"))
                word.Priorities.Add("jiten");
        }

        foreach (var form in word.Forms)
        {
            if (!FormLevelJitenIds.Contains((word.WordId, (byte)form.ReadingIndex)))
                continue;

            form.Priorities ??= [];
            if (!form.Priorities.Contains("jiten"))
                form.Priorities.Add("jiten");
        }
    }

    public static void ApplyAll(IEnumerable<JmDictWord> words)
    {
        foreach (var word in words)
            Apply(word);
    }
}
