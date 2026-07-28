namespace Jiten.Api.Dtos;

/// <summary>Public subset of <see cref="KnownWordAmountDto"/>; blacklist and form-level counts stay owner-only.</summary>
public class ProfileVocabularyStatsDto
{
    public int Young { get; set; }
    public int Mature { get; set; }
    public int Mastered { get; set; }
    public int WordSetMastered { get; set; }
}
