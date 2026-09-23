namespace Jiten.Core.Data.JMDict;

public class KanjiComponent
{
    public string KanjiCharacter { get; set; } = "";

    /// <summary>Pre-order position in the kanji's tree, so ordering by it follows the KanjiVG drawing order.</summary>
    public short NodeIndex { get; set; }

    /// <summary>Null for a direct part of the kanji.</summary>
    public short? ParentIndex { get; set; }

    public string Component { get; set; } = "";

    /// <summary>The full form a variant component stands for, e.g. 艸 for 艹.</summary>
    public string? Original { get; set; }

    public bool IsRadical { get; set; }
    public bool IsPhonetic { get; set; }
}
