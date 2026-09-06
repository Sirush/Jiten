namespace Jiten.Core.Data
{
    public enum MediaType
    {
        Anime = 1,
        Drama = 2,
        Movie = 3,
        Novel = 4,
        NonFiction = 5,
        VideoGame = 6,
        VisualNovel = 7,
        WebNovel = 8,
        Manga = 9,
        Audio = 10,
        YouTube = 11
    }

    public static class MediaTypes
    {
        /// Types accepted for media requests but kept out of browsing, stats and frequency downloads until their decks ship.
        private static readonly HashSet<MediaType> Unlisted = [];

        public static bool IsListed(MediaType type) => !Unlisted.Contains(type);

        public static IEnumerable<MediaType> Listed => Enum.GetValues<MediaType>().Where(IsListed);
    }
}
