using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

public class MediaRequestGenre
{
    public int MediaRequestId { get; set; }
    public Genre Genre { get; set; }

    [JsonIgnore]
    public MediaRequest MediaRequest { get; set; } = null!;
}
