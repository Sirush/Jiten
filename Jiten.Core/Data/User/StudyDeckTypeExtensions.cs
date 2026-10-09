namespace Jiten.Core.Data.User;

public static class StudyDeckTypeExtensions
{
    public static bool HasMaterialisedWords(this StudyDeckType type) => type is StudyDeckType.StaticWordList or StudyDeckType.Smart;

    /// <summary>Media and media group decks draw from deck word lists with the same filters and orders.</summary>
    public static bool DrawsFromDecks(this StudyDeckType type) => type is StudyDeckType.MediaDeck or StudyDeckType.MediaGroup;
}
