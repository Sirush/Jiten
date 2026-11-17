namespace Jiten.Core.Data;

public enum DeckDictionaryEntryType
{
    Name,

    /// <summary>Found by the parser in the deck's own text; replaced on every full parse and never shown as a curated name.</summary>
    DiscoveredName
}
