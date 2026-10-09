using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class SeriesRefDto
{
    public int SeriesId { get; set; }
    public string Name { get; set; } = "";
    public SeriesKind Kind { get; set; }
}

public class SeriesSummaryDto : SeriesRefDto
{
    public int DeckCount { get; set; }
}

public class SeriesDetailDto
{
    public int SeriesId { get; set; }
    public string Name { get; set; } = "";
    public SeriesKind Kind { get; set; }

    /// <summary>Franchise holding most of the members; null for a setting or when no member has one.</summary>
    public int? FranchiseId { get; set; }

    public List<FranchiseNodeDto> Members { get; set; } = new();
}
