using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckDto
{
    public int DeckId { get; set; }
    public DateTimeOffset CreationDate { get; set; }
    public DateTime ReleaseDate { get; set; }
    public string CoverName { get; set; } = "nocover.jpg";
    public MediaType MediaType { get; set; } = new();
    public string OriginalTitle { get; set; } = "Unknown";
    public string RomajiTitle { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public string Description { get; set; } = "";
    public int CharacterCount { get; set; }
    public int WordCount { get; set; }
    public int UniqueWordCount { get; set; }
    public int UniqueWordUsedOnceCount { get; set; }
    public int UniqueKanjiCount { get; set; }
    public int UniqueKanjiUsedOnceCount { get; set; }
    public int Difficulty { get; set; }
    public float DifficultyRaw { get; set; }
    public float DifficultyOverride { get; set; }
    public float DifficultyAlgorithmic { get; set; }
    public int SentenceCount { get; set; }
    public long SpeechDuration { get; set; }
    public long SpeechMoraCount { get; set; }
    public float SpeechSpeed { get; set; }
    public float AverageSentenceLength { get; set; }
    public int? ParentDeckId { get; set; }
    public List<Link> Links { get; set; } = new();
    public List<string> Aliases { get; set; } = new();
    public List<DeckDictionaryEntryDto> DictionaryEntries { get; set; } = new();
    public int ChildrenDeckCount { get; set; }
    public int SelectedWordOccurrences { get; set; }
    public float DialoguePercentage { get; set; }
    public bool HideDialoguePercentage { get; set; }
    public bool HideAverageSentenceLength { get; set; }
    public float Coverage { get; set; }
    public float UniqueCoverage { get; set; }
    public float YoungCoverage { get; set; }
    public float YoungUniqueCoverage { get; set; }
    public byte ExternalRating { get; set; }
    public ExampleSentenceDto? ExampleSentence { get; set; }
    public List<Genre> Genres { get; set; } = new();
    public List<TagWithPercentageDto> Tags { get; set; } = new();
    public List<DeckRelationshipDto> Relationships { get; set; } = new();
    public DeckStatus? Status { get; set; }
    public bool? IsFavourite { get; set; }
    public bool? IsIgnored { get; set; }
    public int DistinctVoterCount { get; set; }
    public decimal UserAdjustment { get; set; }

    public DeckDto()
    {
    }

    public DeckDto(Deck deck, int occurrences, ExampleSentenceDto? exampleSentence = null)
    {
        DeckId = deck.DeckId;
        CreationDate = deck.CreationDate;
        ReleaseDate = deck.ReleaseDate.ToDateTime(new TimeOnly());
        CoverName = deck.CoverName;
        MediaType = deck.MediaType;
        OriginalTitle = deck.OriginalTitle;
        RomajiTitle = deck.RomajiTitle!;
        EnglishTitle = deck.EnglishTitle!;
        Description = deck.Description ?? "";
        CharacterCount = deck.CharacterCount;
        WordCount = deck.WordCount;
        UniqueWordCount = deck.UniqueWordCount;
        UniqueWordUsedOnceCount = deck.UniqueWordUsedOnceCount;
        UniqueKanjiCount = deck.UniqueKanjiCount;
        UniqueKanjiUsedOnceCount = deck.UniqueKanjiUsedOnceCount;
        DifficultyAlgorithmic = deck.GetDifficulty();
        var adjusted = GetAdjustedDifficulty(deck);
        DifficultyRaw = adjusted;
        Difficulty = MapDifficulty(adjusted);
        DifficultyOverride = deck.DifficultyOverride;
        SentenceCount = deck.SentenceCount;
        SpeechDuration = deck.SpeechDuration;
        SpeechMoraCount = deck.SpeechMoraCount;
        SpeechSpeed = (float)deck.SpeechSpeed;
        AverageSentenceLength = deck.AverageSentenceLength;
        ParentDeckId = deck.ParentDeckId;
        Links = deck.Links;
        ChildrenDeckCount = deck.Children.Count;
        SelectedWordOccurrences = occurrences;
        DialoguePercentage = deck.DialoguePercentage;
        HideDialoguePercentage = deck.HideDialoguePercentage;
        HideAverageSentenceLength = deck.HideAverageSentenceLength;
        Aliases = deck.Titles.Where(t => t.TitleType == DeckTitleType.Alias).Select(t => t.Title).ToList();
        DictionaryEntries = deck.DictionaryEntries.Select(e => new DeckDictionaryEntryDto { Surface = e.Surface, EntryType = e.EntryType }).ToList();
        ExternalRating = deck.ExternalRating;
        ExampleSentence = exampleSentence;
        Genres = deck.DeckGenres.Select(dg => dg.Genre).OrderBy(g => g.ToString()).ToList();
        Tags = deck.DeckTags.Select(dt => new TagWithPercentageDto
        {
            TagId = dt.TagId,
            Name = dt.Tag.Name,
            Percentage = dt.Percentage
        }).OrderByDescending(t => t.Percentage).ToList();
        MapVoteFields(deck);
    }

    public DeckDto(Deck deck, ExampleSentenceDto? exampleSentence = null)
    {
        DeckId = deck.DeckId;
        CreationDate = deck.CreationDate;
        ReleaseDate = deck.ReleaseDate.ToDateTime(new TimeOnly());
        CoverName = deck.CoverName;
        MediaType = deck.MediaType;
        OriginalTitle = deck.OriginalTitle;
        RomajiTitle = deck.RomajiTitle!;
        EnglishTitle = deck.EnglishTitle!;
        Description = deck.Description ?? "";
        CharacterCount = deck.CharacterCount;
        WordCount = deck.WordCount;
        UniqueWordCount = deck.UniqueWordCount;
        UniqueWordUsedOnceCount = deck.UniqueWordUsedOnceCount;
        UniqueKanjiCount = deck.UniqueKanjiCount;
        UniqueKanjiUsedOnceCount = deck.UniqueKanjiUsedOnceCount;
        DifficultyAlgorithmic = deck.GetDifficulty();
        var adjusted = GetAdjustedDifficulty(deck);
        DifficultyRaw = adjusted;
        Difficulty = MapDifficulty(adjusted);
        DifficultyOverride = deck.DifficultyOverride;
        SentenceCount = deck.SentenceCount;
        SpeechDuration = deck.SpeechDuration;
        SpeechMoraCount = deck.SpeechMoraCount;
        SpeechSpeed = (float)deck.SpeechSpeed;
        AverageSentenceLength = deck.AverageSentenceLength;
        ParentDeckId = deck.ParentDeckId;
        Links = deck.Links;
        ChildrenDeckCount = deck.Children.Count;
        DialoguePercentage = deck.DialoguePercentage;
        HideDialoguePercentage = deck.HideDialoguePercentage;
        HideAverageSentenceLength = deck.HideAverageSentenceLength;
        Aliases = deck.Titles.Where(t => t.TitleType == DeckTitleType.Alias).Select(t => t.Title).ToList();
        DictionaryEntries = deck.DictionaryEntries.Select(e => new DeckDictionaryEntryDto { Surface = e.Surface, EntryType = e.EntryType }).ToList();
        ExternalRating = deck.ExternalRating;
        ExampleSentence = exampleSentence;
        Genres = deck.DeckGenres.Select(dg => dg.Genre).OrderBy(g => g.ToString()).ToList();
        Tags = deck.DeckTags.Select(dt => new TagWithPercentageDto
        {
            TagId = dt.TagId,
            Name = dt.Tag.Name,
            Percentage = dt.Percentage
        }).OrderByDescending(t => t.Percentage).ToList();
        MapVoteFields(deck);
    }

    private void MapVoteFields(Deck deck)
    {
        var dd = deck.DeckDifficulty;
        if (dd == null) return;
        DistinctVoterCount = dd.DistinctVoterCount;
        UserAdjustment = dd.UserAdjustment;
    }

    private float GetAdjustedDifficulty(Deck deck)
    {
        var baseDifficulty = deck.GetDifficulty();
        var adjustment = deck.DeckDifficulty?.UserAdjustment ?? 0;
        return (float)(baseDifficulty + (float)adjustment);
    }

    /// <summary>
    /// Remap the difficulty to an int while taking into account the biases of the model
    /// This is subject to change with a different training
    /// </summary>
    private int MapDifficulty(float difficulty)
    {
        if (difficulty < 1.01)
            return 0;

        if (difficulty < 2.01)
            return 1;

        if (difficulty < 3.01)
            return 2;

        if (difficulty < 4.01)
            return 3;

        if (difficulty < 4.95)
            return 4;

        return 5;
    }
}

public class DeckRelationshipDto
{
    public int TargetDeckId { get; set; }
    public DeckDto TargetDeck { get; set; } = new();
    public DeckRelationshipType RelationshipType { get; set; }
    public bool IsInverse { get; set; }

    public static List<DeckRelationshipDto> FromDeck(
        ICollection<DeckRelationship> asSource,
        ICollection<DeckRelationship> asTarget)
    {
        var direct = asSource.Select(r => new DeckRelationshipDto
        {
            TargetDeckId = r.TargetDeckId,
            TargetDeck = new DeckDto(r.TargetDeck),
            RelationshipType = r.RelationshipType,
            IsInverse = false
        });

        var inverse = asTarget.Select(r => new DeckRelationshipDto
        {
            TargetDeckId = r.SourceDeckId,
            TargetDeck = new DeckDto(r.SourceDeck),
            RelationshipType = DeckRelationship.GetInverse(r.RelationshipType),
            IsInverse = true
        });

        return direct.Concat(inverse).ToList();
    }
}
