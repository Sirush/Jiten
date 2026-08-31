namespace Jiten.Core.Data;

public enum MediaTypeGroup
{
    Prose,
    VisualText,
    AudioVisual,
    NonFiction,
    Unknown
}

public static class MediaTypeGroups
{
    public static readonly HashSet<MediaType> Prose =
    [
        MediaType.Novel, MediaType.WebNovel, MediaType.VisualNovel
    ];

    public static readonly HashSet<MediaType> VisualText =
    [
        MediaType.Manga, MediaType.VideoGame
    ];

    public static readonly HashSet<MediaType> AudioVisual =
    [
        MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio, MediaType.YouTube
    ];

    public static readonly HashSet<MediaType> NonFictionGroup =
    [
        MediaType.NonFiction
    ];

    private static readonly HashSet<MediaType> AllText =
        [..Prose, ..VisualText, ..NonFictionGroup];

    public static bool AreComparable(MediaType a, MediaType b)
    {
        if ((AllText.Contains(a) && AudioVisual.Contains(b)) ||
            (AudioVisual.Contains(a) && AllText.Contains(b)))
            return false;

        return true;
    }

    public static decimal GetComparisonWeight(MediaType a, MediaType b)
    {
        if (a == b) return 1.0m;

        var groupA = GetGroup(a);
        var groupB = GetGroup(b);

        if (groupA == groupB)
        {
            if (a == MediaType.Audio || b == MediaType.Audio)
                return 0.3m;
            
            return 0.7m;
        }

        if (AllText.Contains(a) && AllText.Contains(b))
        {
            if (NonFictionGroup.Contains(a) || NonFictionGroup.Contains(b))
                return 0.3m;
            
            return 0.5m;
        }

        return 0m;
    }

    public static MediaTypeGroup GetGroup(MediaType t)
    {
        if (Prose.Contains(t)) return MediaTypeGroup.Prose;
        if (VisualText.Contains(t)) return MediaTypeGroup.VisualText;
        if (AudioVisual.Contains(t)) return MediaTypeGroup.AudioVisual;
        if (NonFictionGroup.Contains(t)) return MediaTypeGroup.NonFiction;
        return MediaTypeGroup.Unknown;
    }
}
