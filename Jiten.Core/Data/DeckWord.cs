using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

public class DeckWord
{
    public long DeckWordId { get; set; }
    
    /// <summary>
    /// Corresponding deck id
    /// </summary>
    public int DeckId { get; set; }
    
    /// <summary>
    /// Corresponding word id
    /// </summary>
    public int WordId { get; set; }
    
    /// <summary>
    /// Original text before any deconjugation
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public string OriginalText { get; set; } = string.Empty;
    
    /// <summary>
    /// The index of the reading in the list of readings
    /// </summary>
    public byte ReadingIndex { get; set; }
    
    /// <summary>
    /// Number of times the exact word & reading appears in the deck
    /// </summary>
    public int Occurrences { get; set; }
    
    /// <summary>
    /// The list of conjugation strings, reconstructed from the byte indices when accessed
    /// </summary>
    // [JsonIgnore]
    [NotMapped]
    public List<string> Conjugations 
    { 
        get => _conjugationIndices.Select(ConjugationCache.GetString).ToList();
        set => _conjugationIndices = value.Select(ConjugationCache.GetOrAddByte).ToList();
    }

    [NotMapped]
    public List<PartOfSpeech> PartsOfSpeech { get; set; } = [];
    
    [NotMapped]
    public WordOrigin Origin { get; set; }

    [NotMapped]
    [JsonIgnore]
    public string SudachiReading { get; set; } = string.Empty;

    [NotMapped]
    [JsonIgnore]
    public PartOfSpeech SudachiPartOfSpeech { get; set; }

    [NotMapped]
    public int? CachedMargin { get; set; }

    /// Materialised on first read only; parse output creates hundreds of thousands of DeckWords that never touch it.
    [JsonIgnore]
    [IgnoreDataMember]
    public Deck Deck { get => _deck ??= new(); set => _deck = value; }

    private Deck? _deck;
    
    /// <summary>Last conjugation step without materialising the list, or null when there is none.</summary>
    [NotMapped]
    [JsonIgnore]
    [IgnoreDataMember]
    public string? LastConjugation =>
        _conjugationIndices.Count > 0 ? ConjugationCache.GetString(_conjugationIndices[^1]) : null;

    [NotMapped]
    [JsonIgnore]
    [IgnoreDataMember]
    public int ConjugationCount => _conjugationIndices.Count;

    /// <summary>Copies the conjugation chain without the string round trip the Conjugations property does.</summary>
    public void CopyConjugationsFrom(DeckWord source) => _conjugationIndices = new List<byte>(source._conjugationIndices);

    /// <summary>Field-for-field copy that shares no mutable state with the source; Deck is left unmaterialised.</summary>
    public DeckWord Clone() => new()
    {
        DeckWordId = DeckWordId,
        DeckId = DeckId,
        WordId = WordId,
        OriginalText = OriginalText,
        ReadingIndex = ReadingIndex,
        Occurrences = Occurrences,
        _conjugationIndices = new List<byte>(_conjugationIndices),
        PartsOfSpeech = new List<PartOfSpeech>(PartsOfSpeech),
        Origin = Origin,
        SudachiReading = SudachiReading,
        SudachiPartOfSpeech = SudachiPartOfSpeech,
        CachedMargin = CachedMargin,
    };

    /// <summary>
    /// The conjugation bytes that reference the cached conjugation strings
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    private List<byte> _conjugationIndices = new();
}
