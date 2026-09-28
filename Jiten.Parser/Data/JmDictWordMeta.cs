using Jiten.Core.Data;

namespace Jiten.Parser.Data;

internal readonly struct JmDictWordMeta(PartOfSpeech[] pos, short priorityScoreKana, short priorityScoreKanji, WordOrigin origin,
                                        bool isTrueName = false)
{
    public readonly PartOfSpeech[] Pos = pos;
    public readonly short PriorityScoreKana = priorityScoreKana;
    public readonly short PriorityScoreKanji = priorityScoreKanji;
    public readonly WordOrigin Origin = origin;

    /// <summary>False for JMnedict "unclass" entries, which cover slang like ダサ despite mapping to Name.</summary>
    public readonly bool IsTrueName = isTrueName;

    public int GetPriorityScore(bool isKana) => isKana ? PriorityScoreKana : PriorityScoreKanji;

    public PartOfSpeech GetPrimaryPos()
    {
        foreach (var p in Pos)
            if (p is not (PartOfSpeech.Name or PartOfSpeech.Unknown))
                return p;
        return PartOfSpeech.Noun;
    }
}
