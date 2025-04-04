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
    ///  Difficulty rating from 0 to 100
    /// </summary>
    public int Difficulty { get; set; }

    /// <summary>
    /// Amount of sentences
    /// </summary>
    public int SentenceCount { get; set; }

    /// <summary>
    /// Average sentence length with decimal precision
    /// </summary>
    public float AverageSentenceLength => SentenceCount == 0 ? 0 : (float)CharacterCount / SentenceCount;

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

    /// <summary>
    /// Child decks
    ///  </summary>
    public ICollection<Deck> Children { get; set; } = new List<Deck>();

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

    public async Task AddChildDeckWords(JitenDbContext context)
    {
        if (Children.Count == 0)
            return;

        DeckWords = new List<DeckWord>();

        foreach (var child in Children)
        {
            foreach (var childDeckWord in child.DeckWords)
            {
                var existingDeckWord = DeckWords.FirstOrDefault(dw => dw.WordId == childDeckWord.WordId &&
                                                                      dw.ReadingIndex == childDeckWord.ReadingIndex);
                if (existingDeckWord != null)
                {
                    existingDeckWord.Occurrences += childDeckWord.Occurrences;
                }
                else
                {
                    DeckWords.Add(new DeckWord
                                  {
                                      DeckId = DeckId,
                                      WordId = childDeckWord.WordId,
                                      ReadingIndex = childDeckWord.ReadingIndex,
                                      Occurrences = childDeckWord.Occurrences,
                                      OriginalText = childDeckWord.OriginalText,
                                      Deck = this
                                  });
                }
            }
        }

        CharacterCount = Children.Sum(c => c.CharacterCount);
        WordCount = Children.Sum(c => c.WordCount);
        UniqueWordCount = DeckWords.Select(dw => dw.WordId).Distinct().Count();
        UniqueWordUsedOnceCount = DeckWords.Where(dw => dw.Occurrences == 1).Select(dw => dw.WordId).Distinct().Count();
        SentenceCount = Children.Sum(c => c.SentenceCount);

        // Not the most efficient or elegant way to do it, rebuilding the text, but it works and I don't have a better idea for now

        StringBuilder sb = new();

        var wordIds = DeckWords.Select(dw => dw.WordId).ToList();

        var jmdictWords = context.JMDictWords.AsNoTracking()
                                 .Where(w => wordIds.Contains(w.WordId))
                                 .Include(w => w.Definitions)
                                 .ToList();

        var words = DeckWords.Select(dw => new { dw, jmDictWord = jmdictWords.FirstOrDefault(w => w.WordId == dw.WordId) })
                             .OrderBy(dw => wordIds.IndexOf(dw.dw.WordId))
                             .ToList();
        foreach (var word in words)
        {
            var reading = word.jmDictWord!.Readings[word.dw.ReadingIndex];

            for (int i = 0; i < word.dw.Occurrences; i++)
            {
                sb.Append(reading);
            }
        }

        var rebuiltText = sb.ToString();
        UniqueKanjiCount = rebuiltText.Distinct().Count(c => WanaKana.IsKanji(c.ToString()));
        UniqueKanjiUsedOnceCount = rebuiltText.GroupBy(c => c).Count(g => g.Count() == 1 && WanaKana.IsKanji(g.Key.ToString()));
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