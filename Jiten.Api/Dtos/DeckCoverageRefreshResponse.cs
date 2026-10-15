namespace Jiten.Api.Dtos;

public class DeckCoverageRefreshResponse
{
    /// <summary>"refreshed", "not_eligible" (too few tracked words) or "no_baseline" (no account-wide coverage computed yet).</summary>
    public required string Status { get; set; }
    public float? Coverage { get; set; }
    public float? UniqueCoverage { get; set; }
    public float? YoungCoverage { get; set; }
    public float? YoungUniqueCoverage { get; set; }

    /// <summary>Jiten+ only; null when the media has no stored sentence metrics.</summary>
    public float? ReadableSentences { get; set; }

    public float? IPlusOneSentences { get; set; }
}
