namespace Jiten.Api.Dtos.Requests;

public class AddWebNovelDeckRequest
{
    /// <summary>
    /// Work URL, a page linking to one (a Kakuyomu book page), or a bare ncode
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// Optional override of the title the source reports
    /// </summary>
    public string? OriginalTitle { get; set; }

    /// <summary>
    /// Optional: the source only carries the Japanese title, so romaji is typed or auto-romanised at add time
    /// </summary>
    public string? RomajiTitle { get; set; }

    public string? EnglishTitle { get; set; }

    /// <summary>
    /// Optional: uploaded or generated in the browser. Syosetu works have no cover art of their own.
    /// </summary>
    public IFormFile? CoverImage { get; set; }

    /// <summary>
    /// Without an uploaded cover, use the art the source hosts for the work, when it has some
    /// </summary>
    public bool UseSourceCover { get; set; }

    /// <summary>
    /// Optional per-novel override of the subdeck character budget
    /// </summary>
    public int? ChunkCharBudget { get; set; }
}
