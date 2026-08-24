namespace Jiten.Core.Data.User;

public class UserStudyDeck
{
    public int UserStudyDeckId { get; set; }
    public string UserId { get; set; } = default!;
    public int? DeckId { get; set; }
    public StudyDeckType DeckType { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public int DownloadType { get; set; }
    public int Order { get; set; }
    public int MinFrequency { get; set; }
    public int MaxFrequency { get; set; }
    public float? TargetPercentage { get; set; }
    public int? MinOccurrences { get; set; }
    public int? MaxOccurrences { get; set; }
    public bool IsActive { get; set; } = true;
    public bool ExcludeKana { get; set; }
    public bool StartFromKnown { get; set; }

    public int? MinGlobalFrequency { get; set; }
    public int? MaxGlobalFrequency { get; set; }
    public string? PosFilter { get; set; }

    /// <summary>Frequency source for a GlobalDynamic deck; both null means the site-wide ranking.</summary>
    public MediaType? FrequencyMediaType { get; set; }

    public long? FrequencyListId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<UserStudyDeckWord> Words { get; set; } = new();
}
