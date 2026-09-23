namespace Jiten.Core.Data.JMDict;

/// <summary>KanjiVG stroke paths in writing order, drawn on a 109x109 viewBox.</summary>
public class KanjiStrokes
{
    public string Character { get; set; } = "";
    public List<string> Paths { get; set; } = [];

    /// <summary>Stroke number label positions, flattened as x0, y0, x1, y1, ...</summary>
    public List<float> NumberPositions { get; set; } = [];
}
