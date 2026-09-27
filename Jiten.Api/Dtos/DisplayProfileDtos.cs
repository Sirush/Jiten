using System.Text.Json;

namespace Jiten.Api.Dtos;

public class DisplayProfileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, JsonElement> Values { get; set; } = new();

    /// <summary>Visible media-card stat ids per card column, top to bottom, always three columns; null keeps the built-in layout.</summary>
    public List<List<string>>? StatColumns { get; set; }

    /// <summary>Unix milliseconds, set by the server on every write.</summary>
    public long UpdatedAt { get; set; }
}

public class DisplayProfilesDto
{
    public List<DisplayProfileDto> Profiles { get; set; } = new();
    public int MaxProfiles { get; set; }
}

public class DisplayProfileUpsertRequest
{
    public string Name { get; set; } = "";
    public Dictionary<string, JsonElement>? Values { get; set; }
    public List<List<string>>? StatColumns { get; set; }
}
