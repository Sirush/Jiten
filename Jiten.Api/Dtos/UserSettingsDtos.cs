namespace Jiten.Api.Dtos;

public class MediaFilterPresetDto
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Query { get; set; } = new();
    public long CreatedAt { get; set; }
}

public class MediaFilterPresetsDto
{
    public List<MediaFilterPresetDto> Presets { get; set; } = new();
    public string? DefaultPreset { get; set; }
}
