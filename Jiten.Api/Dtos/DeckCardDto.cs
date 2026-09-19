using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckCardDto : IDeckCoverageTarget
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "Unknown";
    public string RomajiTitle { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public MediaType MediaType { get; set; }
    public string CoverName { get; set; } = "nocover.jpg";
    public int? ParentDeckId { get; set; }
    public int CharacterCount { get; set; }
    public int UniqueWordCount { get; set; }
    public long SpeechDuration { get; set; }
    public int Difficulty { get; set; }
    public float DifficultyRaw { get; set; }
    public float DifficultyAlgorithmic { get; set; }
    public int DistinctVoterCount { get; set; }
    public decimal UserAdjustment { get; set; }
    public decimal AdjustmentConfidence { get; set; }
    public int SelectedWordOccurrences { get; set; }
    public float Coverage { get; set; }
    public float UniqueCoverage { get; set; }
    public float YoungCoverage { get; set; }
    public float YoungUniqueCoverage { get; set; }

    public DeckCardDto() { }

    public DeckCardDto(Deck deck)
    {
        DeckId = deck.DeckId;
        OriginalTitle = deck.OriginalTitle;
        RomajiTitle = deck.RomajiTitle ?? "";
        EnglishTitle = deck.EnglishTitle ?? "";
        MediaType = deck.MediaType;
        CoverName = deck.CoverName;
        ParentDeckId = deck.ParentDeckId;
        CharacterCount = deck.CharacterCount;
        UniqueWordCount = deck.UniqueWordCount;
        SpeechDuration = deck.SpeechDuration;
        DifficultyAlgorithmic = deck.GetDifficulty();
        DifficultyRaw = DifficultyMapper.GetAdjustedDifficulty(deck);
        Difficulty = DifficultyMapper.MapDifficulty(DifficultyRaw);
        var dd = deck.DeckDifficulty;
        if (dd == null) return;
        DistinctVoterCount = dd.DistinctVoterCount;
        UserAdjustment = dd.UserAdjustment;
        AdjustmentConfidence = dd.AdjustmentConfidence;
    }
}

public interface IDeckCoverageTarget
{
    int DeckId { get; }
    float Coverage { get; set; }
    float UniqueCoverage { get; set; }
    float YoungCoverage { get; set; }
    float YoungUniqueCoverage { get; set; }
}
