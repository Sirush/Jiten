using Jiten.Core.Data.User;

namespace Jiten.Api.Services;

public static class CardMediaStorage
{
    /// <summary>
    /// Mints a storage path for one card-media file. The version suffix makes every write land on a fresh
    /// path, so a replaced file is never overwritten in place behind the pull-zone cache.
    /// </summary>
    public static string PathFor(string userId, int wordId, byte readingIndex, CardMediaKind kind, string extension)
    {
        var version = Guid.NewGuid().ToString("N")[..8];
        return $"card-media/{userId}/{wordId}_{readingIndex}_{kind.ToString().ToLowerInvariant()}_{version}.{extension}";
    }
}
