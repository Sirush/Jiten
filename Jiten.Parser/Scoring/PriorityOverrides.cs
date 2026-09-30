using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Scoring;

/// <summary>Adds a "jiten" priority tag to DB-loaded JMDict words or single forms to win homograph ties.</summary>
internal static class PriorityOverrides
{
    private static readonly HashSet<int> WordLevelJitenIds =
    [
        1204860, // 各 (かく): beats 各々 おのおの by 1pt
        1300520, // ３時 (さんじ): beats 三次, whose ３時 form wins by ruby priors
        1545300, // 妖怪: beats 溶解, whose NormalizedForm bonus inflates its score
        1922120, // 兼ねない: standalone beats conjugated 兼ねる
        2579880, // コホン (ahem): beats 古本 こほん
        1571330, // 舐る (ねぶる): beats 眠る's rare ねぶる reading
        1709300, // 数度 (すうど): beats the surname すどう that Sudachi guesses
        1467400, // 忍び: beats the fern しのぶ (2179930) on bare 忍, whose ruby priors come from the name Shinobu
    ];

    private static readonly HashSet<(int WordId, byte ReadingIndex)> FormLevelJitenIds =
    [
        (1168660, 4), // 依る よる: beats 寄る
        (1313580, 2), // 事 こと: beats 琴
        (1495740, 2), // 付く つく: beats 点く
        (1508300, 2), // 柄 ガラ: exempts it from the short-kana gate
        (1593500, 2), // 轟々 ごうごう: beats 囂々, avoiding a margin=0 reseg
        (2013900, 4), // 赤 あか: beats 垢 and 銅
        (1529560, 1), // 無し なし: beats 梨 on the casual katakana negation ナシ
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
