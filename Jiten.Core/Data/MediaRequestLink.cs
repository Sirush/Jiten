using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>Another catalogue's page for the same media, as listed by the request's provider (AniList's MAL id, TMDB's IMDb id).</summary>
public class MediaRequestLink
{
    public int Id { get; set; }
    public int MediaRequestId { get; set; }
    public LinkType LinkType { get; set; }
    public required string Url { get; set; }

    [JsonIgnore]
    public MediaRequest MediaRequest { get; set; } = null!;
}
