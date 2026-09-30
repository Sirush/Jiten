namespace Jiten.Api.Dtos;

public enum DeckOrder
{
    Chronological = 1,
    GlobalFrequency = 2,
    DeckFrequency = 3,
    ImportOrder = 4,
    Random = 5,

    /// <summary>Media study decks only (Jiten+): words that make the most of the title's sentences readable come first. Resolves as DeckFrequency everywhere else.</summary>
    SentenceUnlock = 6
}