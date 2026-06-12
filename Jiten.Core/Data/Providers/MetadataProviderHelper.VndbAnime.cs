using System.Text.Json;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;

namespace Jiten.Core;

public static partial class MetadataProviderHelper
{
    private static readonly Lazy<Dictionary<string, int[]>> VndbAnimeMalMap = new(LoadVndbAnimeMalMap);
    private static Dictionary<string, int[]> LoadVndbAnimeMalMap()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "resources", "vndb_anime_mal.json");
            if (!File.Exists(path))
                return new Dictionary<string, int[]>();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, int[]>>(json) ?? new Dictionary<string, int[]>();
        }
        catch
        {
            return new Dictionary<string, int[]>();
        }
    }

    /// <summary>
    /// Returns the anime adaptation relations for a VNDB id (e.g. "v4"), one per related MAL id.
    /// VNDB anime relations carry no sub-type, so they are always Adaptation with the VN as the
    /// source material (SwapDirection = false => DeckRelationship(VN, anime, Adaptation)).
    /// </summary>
    public static List<MetadataRelation> GetVndbAnimeRelations(string vndbId)
    {
        if (string.IsNullOrEmpty(vndbId) || !VndbAnimeMalMap.Value.TryGetValue(vndbId, out var malIds))
            return [];

        return malIds.Select(malId => new MetadataRelation
        {
            ExternalId = malId.ToString(),
            LinkType = LinkType.Mal,
            RelationshipType = DeckRelationshipType.Adaptation,
            TargetMediaType = MediaType.Anime,
            SwapDirection = false
        }).ToList();
    }
}
