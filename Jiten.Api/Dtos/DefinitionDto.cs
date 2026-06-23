namespace Jiten.Api.Dtos;

public class DefinitionDto
{
    public int Index { get; set; }
    public List<string> Meanings { get; set; } = new();
    public List<string> PartsOfSpeech { get; set; } = new();
    public List<string>? Pos { get; set; }
    public List<string>? Misc { get; set; }
    public List<string>? Field { get; set; }
    public List<string>? Dial { get; set; }

    /// <summary>s_inf usage notes shown under the sense.</summary>
    public List<string>? SenseInfo { get; set; }

    /// <summary>g_type per meaning, index-aligned with <see cref="Meanings"/>. "" = plain gloss.</summary>
    public List<string>? GlossTypes { get; set; }

    public List<short>? RestrictedToReadingIndices { get; set; }

    /// <summary>Cross-references (see/ant/syn) attached to this sense.</summary>
    public List<CrossReferenceDto>? CrossReferences { get; set; }
}

public class CrossReferenceDto
{
    public string Type { get; set; } = "see"; // see | ant | syn
    public int? TargetWordId { get; set; }
    public string TargetText { get; set; } = "";
    public string? TargetKanji { get; set; }
    public string? TargetReading { get; set; }
    public short? TargetSenseIndex { get; set; }
}