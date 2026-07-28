namespace Jiten.Api.Dtos;

public class GrowthPointDto
{
    public DateOnly Date { get; set; }

    /// <summary>Distinct mature words known by this bucket (the whole known set on the global series).</summary>
    public int KnownWords { get; set; }

    public int KnownWordsCombined { get; set; }
}

public class JourneyPointDto : GrowthPointDto
{
    /// <summary>Mature coverage of the deck's running text, in percent.</summary>
    public float Coverage { get; set; }

    /// <summary>Mature + young coverage of the deck's running text, in percent.</summary>
    public float CombinedCoverage { get; set; }

    public float UniqueCoverage { get; set; }
    public float CombinedUniqueCoverage { get; set; }
}

public class JourneyMilestoneDto
{
    public int Threshold { get; set; }
    public DateOnly ReachedAt { get; set; }

    /// <summary>Distinguishes the unique-coverage thresholds from the total-coverage ones in a single list.</summary>
    public bool Unique { get; set; }
}

public class JourneyDto
{
    public int DeckId { get; set; }
    public string Granularity { get; set; } = "monthly";
    public List<JourneyPointDto> Points { get; set; } = [];
    public List<JourneyMilestoneDto> Milestones { get; set; } = [];
    public DateOnly? StartDate { get; set; }
    public float StartCoverage { get; set; }
    public float CurrentCoverage { get; set; }
    public float StartUniqueCoverage { get; set; }
    public float CurrentUniqueCoverage { get; set; }

    /// <summary>False when the user knows nothing in the deck or the history spans a single bucket.</summary>
    public bool HasEnoughHistory { get; set; }

    /// <summary>The coverage refresh the series was built against; the endpoint equals the coverage shown elsewhere as of this moment.</summary>
    public DateTime? AsOf { get; set; }
}

public class GlobalGrowthDto
{
    public string Granularity { get; set; } = "monthly";
    public List<GrowthPointDto> Points { get; set; } = [];
    public bool HasEnoughHistory { get; set; }
}
