using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DescriptionSearchResponseDto
{
    /// <summary>The query exactly as received.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>What was ranked on after media type words were removed.</summary>
    public string SearchedText { get; set; } = string.Empty;

    /// <summary>Media type named inside the query, if any, regardless of an explicit filter.</summary>
    public MediaType? DetectedMediaType { get; set; }

    /// <summary>Filter actually applied: the explicit parameter, else the detected type.</summary>
    public MediaType? MediaType { get; set; }

    public List<SimilarDeckDto> Results { get; set; } = [];
}
