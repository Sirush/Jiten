namespace Jiten.Core.Data.User;

public static class StudyDeckTypeExtensions
{
    public static bool HasMaterialisedWords(this StudyDeckType type) => type is StudyDeckType.StaticWordList or StudyDeckType.Smart;
}
