namespace Jiten.Api.Dtos.Requests;

public class ImportFromIdsRequest
{
    public List<long> WordIds { get; set; } = new();
    public List<long> BlacklistedWordIds { get; set; } = new();
    public List<long> SuspendedWordIds { get; set; } = new();
    public int? FrequencyThreshold { get; set; }

    /// <summary>Per-spelling cards; each marks only the form the user studied, unlike the id lists above.</summary>
    public List<ImportFromIdsCard>? Cards { get; set; }
}

public class ImportFromIdsCard
{
    public long WordId { get; set; }
    public string Spelling { get; set; } = "";

    /// <summary>"known", "blacklisted" or "suspended".</summary>
    public string State { get; set; } = "";
}
