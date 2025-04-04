using System.Text.Json.Serialization;

namespace Jiten.Cli.Data.Tmdb;

public class TmdbMovie
{
    [JsonPropertyName("id")] public int Id { get; set; }

    /// <summary>
    /// Japanese title
    /// </summary>
    [JsonPropertyName("original_title")]
    public string OriginalTitle { get; set; }

    /// <summary>
    /// English or romaji title
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }

    [JsonPropertyName("release_date")] public DateTime ReleaseDate { get; set; }

    [JsonPropertyName("poster_path")] public string PosterPath { get; set; }
}