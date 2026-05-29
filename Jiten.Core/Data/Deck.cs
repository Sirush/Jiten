using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WanaKanaShaapu;

namespace Jiten.Core.Data;

public class Deck
{
    /// <summary>
    /// Autoincrement id, starting at 1
    /// </summary>
    public int DeckId { get; set; }

    public DateTimeOffset CreationDate { get; set; } = DateTime.UtcNow;

    public DateTimeOffset LastUpdate { get; set; } = DateTime.UtcNow;

    public DateOnly ReleaseDate { get; set; } = default;

    /// <summary>
    /// Name of the image cover file
    /// </summary>
    public string CoverName { get; set; } = "nocover.jpg";

    /// <summary>
    /// Type of media the deck belongs to
    /// </summary>
    public MediaType MediaType { get; set; } = new();

    /// <summary>
    /// Original title of the work, generally in kanji
    /// </summary>
    public string OriginalTitle { get; set; } = "Unknown";

    /// <summary>
    /// Romaji transcription of the title
    /// </summary>
    public string? RomajiTitle { get; set; }

    /// <summary>
    /// English translation of the title, if it exists
    /// </summary>
    public string? EnglishTitle { get; set; }

    /// <summary>
    /// Description or summary for media decks
    /// </summary>
    [MaxLength(2000)]
    public string? Description { get; set; }

    /// <summary>
    /// Total character count, without punctuation
    /// </summary>
    public int CharacterCount { get; set; }

    /// <summary>
    /// Total word count, non-unique
    /// </summary>
    public int WordCount { get; set; }

    /// <summary>
    /// Total unique word count after deconjugation
    /// </summary>
    public int UniqueWordCount { get; set; }

    /// <summary>
    /// Total unique word count after deconjugation, used only once
    /// </summary>
    public int UniqueWordUsedOnceCount { get; set; }

    /// <summary>
    /// Total unique kanji count
    /// </summary>
    public int UniqueKanjiCount { get; set; } // Unique Kanji count

    /// <summary>
    /// Total unique kanji count, used only once
    /// </summary>
    public int UniqueKanjiUsedOnceCount { get; set; }

    /// <summary>
    ///  Difficulty rating from 0 to 5
    /// </summary>
    public float Difficulty { get; set; }

    /// <summary>
    /// Manual override of difficulty that will replace Difficulty if it's not equal to -1
    /// </summary>
    public float DifficultyOverride { get; set; }
    
    /// <summary>
    /// External rating from 0 to 100, pulled from metadata providers
    /// </summary>
    public byte ExternalRating { get; set; }

    /// <summary>
    /// Amount of sentences
    /// </summary>
    public int SentenceCount { get; set; }

    /// <summary>
    /// Total duration of the speech extracted from the subtitles milliseconds (merged intervals)
    /// </summary>
    public long SpeechDuration { get; set; }

    /// <summary>
    /// Total mora count for the speech extracted from the subtitles text (after reading conversion)
    /// </summary>
    public long SpeechMoraCount { get; set; }

    /// <summary>
    /// Mora per minute, computed from SpeechMoraCount and SpeechDuration
    /// </summary>
    [NotMapped]
    public double SpeechSpeed => SpeechDuration > 0 ? SpeechMoraCount / (SpeechDuration / 60000.0) : 0;

    /// <summary>
    /// Average sentence length with decimal precision
    /// </summary>
    public float AverageSentenceLength => SentenceCount is 0 or 1 ? 0 : (float)CharacterCount / SentenceCount;

    /// <summary>
    /// Percentage of dialogue in the whole text
    /// </summary>
    public float DialoguePercentage
    {
        get
        {
            if (MediaType is MediaType.Anime or MediaType.Drama or MediaType.Movie or MediaType.Manga or MediaType.VideoGame or MediaType.Audio)
                return 100;

            return _dialoguePercentage;
        }
        set => _dialoguePercentage = value;
    }
    
    /// <summary>
    /// Whether to hide the dialogue percentage on the deck card
    /// </summary>
    public bool HideDialoguePercentage { get; set; } = false;
    public bool HideAverageSentenceLength { get; set; } = false;

    /// <summary>
    /// Parent deck, null if no parent
    /// </summary>
    public int? ParentDeckId { get; set; }

    /// <summary>
    /// Parent deck, null if no parent
    /// </summary>
    public Deck? ParentDeck { get; set; }

    /// <summary>
    /// Order of the deck if it's a child
    /// </summary>
    public int DeckOrder { get; set; }

    public ICollection<Deck> Children { get; set; } = new List<Deck>();

    public List<DeckDictionaryEntry> DictionaryEntries { get; set; } = new();

    /// <summary>
    /// List of words that appear in this deck
    /// </summary>
    public ICollection<DeckWord> DeckWords { get; set; } = new List<DeckWord>();

    /// <summary>
    /// List of links to external websites
    /// </summary>
    public List<Link> Links { get; set; } = new List<Link>();

    /// <summary>
    /// Raw text from which the deck was parsed with
    /// </summary>
    public DeckRawText? RawText { get; set; }

    /// <summary>
    /// Example sentences in this deck
    /// </summary>
    public ICollection<ExampleSentence>? ExampleSentences { get; set; } = new List<ExampleSentence>();

    /// <summary>
    /// Advanced deck statistics
    /// </summary>
    public DeckStats? DeckStats { get; set; }

    /// <summary>
    /// Detailed difficulty metrics
    /// </summary>
    public DeckDifficulty? DeckDifficulty { get; set; }

    /// <summary>
    /// List of titles and aliases
    /// </summary>
    public ICollection<DeckTitle> Titles { get; set; } = new List<DeckTitle>();

    /// <summary>
    /// List of genres associated with this deck
    /// </summary>
    public ICollection<DeckGenre> DeckGenres { get; set; } = new List<DeckGenre>();

    /// <summary>
    /// List of tags associated with this deck
    /// </summary>
    public ICollection<DeckTag> DeckTags { get; set; } = new List<DeckTag>();

    /// <summary>
    /// Relationships where this deck is the source
    /// </summary>
    public ICollection<DeckRelationship> RelationshipsAsSource { get; set; } = new List<DeckRelationship>();

    /// <summary>
    /// Relationships where this deck is the target
    /// </summary>
    public ICollection<DeckRelationship> RelationshipsAsTarget { get; set; } = new List<DeckRelationship>();


    private float _dialoguePercentage;

    public float GetDifficulty() => DifficultyOverride > -1 ? DifficultyOverride : Difficulty;

    public Task AddChildDeckWords(JitenDbContext context)
    {
        if (Children.Count == 0)
            return Task.CompletedTask;

        DeckWords = new List<DeckWord>();
        var deckWordLookup = new Dictionary<(int, byte), DeckWord>();

        foreach (var child in Children.OrderBy(c => c.DeckOrder))
        {
            foreach (var childDeckWord in child.DeckWords)
            {
                var key = (childDeckWord.WordId, childDeckWord.ReadingIndex);
                if (deckWordLookup.TryGetValue(key, out var existingDeckWord))
                {
                    existingDeckWord.Occurrences += childDeckWord.Occurrences;
                }
                else
                {
                    var newDeckWord = new DeckWord
                                     {
                                         DeckId = DeckId, WordId = childDeckWord.WordId, ReadingIndex = childDeckWord.ReadingIndex,
                                         Occurrences = childDeckWord.Occurrences, OriginalText = childDeckWord.OriginalText, Deck = this
                                     };
                    DeckWords.Add(newDeckWord);
                    deckWordLookup[key] = newDeckWord;
                }
            }
        }

        CharacterCount = Children.Sum(c => c.CharacterCount);
        WordCount = Children.Sum(c => c.WordCount);
        UniqueWordCount = DeckWords.Select(dw => new { dw.WordId, dw.ReadingIndex }).Distinct().Count();
        UniqueWordUsedOnceCount = DeckWords.Where(dw => dw.Occurrences == 1).Select(dw => new { dw.WordId, dw.ReadingIndex }).Distinct().Count();
        SentenceCount = Children.Sum(c => c.SentenceCount);
        var childrenWithSpeech = Children.Where(c => c.SpeechDuration > 0).ToList();
        if (childrenWithSpeech.Count > 0)
        {
            var avgSpeed = childrenWithSpeech.Average(c => c.SpeechSpeed);
            SpeechDuration = childrenWithSpeech.Sum(c => c.SpeechDuration);
            SpeechMoraCount = (long)(avgSpeed * (SpeechDuration / 60000.0));
        }
        else
        {
            SpeechDuration = 0;
            SpeechMoraCount = 0;
        }
        Difficulty = Children.Average(c => c.Difficulty);
        DialoguePercentage = Children.Sum(c => c.DialoguePercentage) / Children.Count;

        // Not the most efficient or elegant way to do it, rebuilding the text, but it works and I don't have a better idea for now

        StringBuilder sb = new();

        var wordIds = DeckWords.Select(dw => dw.WordId).ToList();
        var uniqueWordIds = wordIds.Distinct().ToList();

        var wordFormTexts = context.WordForms.AsNoTracking()
                                   .Where(wf => uniqueWordIds.Contains(wf.WordId))
                                   .ToDictionary(wf => (wf.WordId, wf.ReadingIndex), wf => wf.Text);

        var wordIdOrder = new Dictionary<int, int>(capacity: wordIds.Count);
        for (int i = 0; i < wordIds.Count; i++)
        {
            wordIdOrder.TryAdd(wordIds[i], i);
        }

        var words = DeckWords.OrderBy(dw => wordIdOrder.GetValueOrDefault(dw.WordId, int.MaxValue)).ToList();
        foreach (var dw in words)
        {
            var reading = wordFormTexts.GetValueOrDefault((dw.WordId, (short)dw.ReadingIndex), "");

            for (int i = 0; i < dw.Occurrences; i++)
            {
                sb.Append(reading);
            }
        }

        var rebuiltText = sb.ToString();
        UniqueKanjiCount = rebuiltText.Distinct().Count(c => WanaKana.IsKanji(c.ToString()));
        UniqueKanjiUsedOnceCount = rebuiltText.GroupBy(c => c).Count(g => g.Count() == 1 && WanaKana.IsKanji(g.Key.ToString()));

        return Task.CompletedTask;
    }

    public void SetParentsAndDeckWordDeck(Deck deck)
    {
        foreach (var deckWord in deck.DeckWords)
        {
            deckWord.Deck ??= deck;
        }

        foreach (var child in deck.Children)
        {
            child.ParentDeck ??= deck;

            SetParentsAndDeckWordDeck(child);
        }
    }
}
