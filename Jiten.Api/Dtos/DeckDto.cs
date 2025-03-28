using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckDto
{
    public int DeckId { get; set; }
    public string CoverName { get; set; } = "nocover.jpg";
    public MediaType MediaType { get; set; } = new();
    public string OriginalTitle { get; set; } = "Unknown";
    public string RomajiTitle { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public int CharacterCount { get; set; }
    public int WordCount { get; set; }
    public int UniqueWordCount { get; set; }
    public int UniqueWordUsedOnceCount { get; set; }
    public int UniqueKanjiCount { get; set; } // Unique Kanji count
    public int UniqueKanjiUsedOnceCount { get; set; }
    public int Difficulty { get; set; }
    public int SentenceCount { get; set; }
    public float AverageSentenceLength { get; set; }
    public int? ParentDeckId { get; set; }
    public List<Link> Links { get; set; } = new List<Link>();
    public int ChildrenDeckCount { get; set; }

    public DeckDto(){}
    
    public DeckDto(Deck deck)
    {
        DeckId = deck.DeckId;
        CoverName = deck.CoverName;
        MediaType = deck.MediaType;
        OriginalTitle = deck.OriginalTitle;
        RomajiTitle = deck.RomajiTitle!;
        EnglishTitle = deck.EnglishTitle!;
        CharacterCount = deck.CharacterCount;
        WordCount = deck.WordCount;
        UniqueWordCount = deck.UniqueWordCount;
        UniqueWordUsedOnceCount = deck.UniqueWordUsedOnceCount;
        UniqueKanjiCount = deck.UniqueKanjiCount;
        UniqueKanjiUsedOnceCount = deck.UniqueKanjiUsedOnceCount;
        Difficulty = deck.Difficulty;
        SentenceCount = deck.SentenceCount;
        AverageSentenceLength = deck.AverageSentenceLength;
        ParentDeckId = deck.ParentDeckId;
        Links = deck.Links;
        ChildrenDeckCount = deck.Children.Count;
    }
}