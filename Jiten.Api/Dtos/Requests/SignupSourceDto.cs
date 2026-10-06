namespace Jiten.Api.Dtos.Requests;

/// <summary>Where a visitor first arrived before signing up</summary>
public class SignupSourceDto
{
    /// <summary>Route pattern of the first page viewed, e.g. "/decks/media/:id()/detail".</summary>
    public string? Route { get; set; }

    /// <summary>Referrer host of the first visit, empty for direct visits.</summary>
    public string? Referrer { get; set; }

    public string? Utm { get; set; }

    /// <summary>Days between first visit and sign-up, bucketed: "0", "1-6", "7-29" or "30+".</summary>
    public string? Age { get; set; }

    /// <summary>Last guest prompt clicked before signing up, e.g. "download_dialog".</summary>
    public string? Prompt { get; set; }
}
