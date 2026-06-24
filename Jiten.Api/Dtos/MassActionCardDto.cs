using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Dtos;

public class MassActionCardDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Reading { get; set; } = "";
    public string? MainDefinition { get; set; }
    public int FrequencyRank { get; set; }
    public FsrsState State { get; set; }
    public DateTime Due { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Composition-inference maturity bucket: "new", "learning" or "mature". Null for other endpoints.
    /// </summary>
    public string? Category { get; set; }
}
