namespace Jiten.Api.Dtos.Requests;

public class ImportFromWaniKaniRequest
{
    public const int MaxWords = 10000;

    public List<WaniKaniWord> Words { get; set; } = new();
}

public class WaniKaniWord
{
    /// <summary>The spelling WaniKani shows, including any 〜 affix marker.</summary>
    public string Characters { get; set; } = "";

    /// <summary>Primary reading in kana; the characters themselves for kana-only vocabulary.</summary>
    public string Reading { get; set; } = "";

    /// <summary>WaniKani SRS stage, 1 (Apprentice 1) to 9 (Burned).</summary>
    public int SrsStage { get; set; }

    /// <summary>When WaniKani would next show the word; null for burned words.</summary>
    public DateTime? AvailableAt { get; set; }

    public DateTime? StartedAt { get; set; }

    /// <summary>First time the word reached Guru.</summary>
    public DateTime? PassedAt { get; set; }

    public DateTime? BurnedAt { get; set; }

    /// <summary>Reviews WaniKani counted for the word, failed ones included.</summary>
    public int Reviews { get; set; }

    /// <summary>Wrong answers across meaning and reading.</summary>
    public int Incorrect { get; set; }
}
