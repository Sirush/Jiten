using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>A name the request's provider knows the media by; the requester's own title stays on <see cref="MediaRequest.Title"/>.</summary>
public class MediaRequestTitle
{
    public int Id { get; set; }
    public int MediaRequestId { get; set; }
    public required string Title { get; set; }
    public DeckTitleType TitleType { get; set; }

    [JsonIgnore]
    public MediaRequest MediaRequest { get; set; } = null!;
}
