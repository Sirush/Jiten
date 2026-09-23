using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.Data.Providers.Anilist;

namespace Jiten.Core;

public static partial class MetadataProviderHelper
{
    public static async Task<List<Metadata>> AnilistNovelSearchApi(string query)
    {
        return await AnilistSearchApi(query, ["NOVEL"]);
    }

    public static async Task<List<Metadata>> AnilistMangaSearchApi(string query)
    {
        return await AnilistSearchApi(query, ["MANGA", "ONE_SHOT"]);
    }

    public static async Task<List<Metadata>> AnilistSearchApi(string query, string[] format)
    {
        var requestBody = new
                          {
                              query = """

                                              query ($search: String, $type: MediaType, $format: [MediaFormat]) {
                                                Page {
                                                  media (search: $search, type: $type, format_in: $format) {
                                                    id
                                                    idMal
                                                    description
                                                    genres
                                                    isAdult
                                                    countryOfOrigin
                                                    tags {
                                                      name
                                                      rank
                                                      isMediaSpoiler
                                                    }
                                                    title {
                                                      romaji
                                                      english
                                                      native
                                                    }
                                                    startDate {
                                                      day
                                                      month
                                                      year
                                                    }
                                                    bannerImage
                                                    coverImage {
                                                      extraLarge
                                                    },
                                                    synonyms,
                                                    averageScore,
                                                    meanScore,
                                                    relations {
                                                      edges {
                                                        relationType(version: 3)
                                                        node {
                                                          id
                                                          type
                                                          format
                                                        }
                                                      }
                                                    }
                                                    characters(sort: ROLE) {
                                                      nodes {
                                                        name {
                                                          native
                                                          first
                                                          last
                                                        }
                                                      }
                                                    }
                                                  }
                                                }
                                              }
                                      """,
                              variables = new { search = query, type = "MANGA", format = format }
                          };

        var httpClient = new HttpClient();
        var requestContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("https://graphql.anilist.co", requestContent);

        if (!response.IsSuccessStatusCode)
            return [];

        var contentStream = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AnilistResult>(contentStream,
                                                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var medias = result?.Data?.Page?.Media ?? [];
        var relationsById = await MapAnilistRelations(medias);

        return medias.Select(media => new Metadata
                                                         {
                                                             OriginalTitle = media.Title.Native, RomajiTitle = media.Title.Romaji,
                                                             EnglishTitle = media.Title.English, ReleaseDate = media.ReleaseDate,
                                                             Description = Regex.Replace(media.Description ?? "", "<.*?>", "").Trim(),
                                                             Links =
                                                             [
                                                                 new Link
                                                                 {
                                                                     LinkType = LinkType.Anilist,
                                                                     Url = $"https://anilist.co/manga/{media.Id}"
                                                                 }
                                                             ],
                                                             Image = media.CoverImage.ExtraLarge, Aliases = media.Synonyms,
                                                             Rating = media.AverageScore ?? media.MeanScore ?? 0,
                                                             Genres = media.Genres.Distinct().ToList(), Tags = media.Tags
                                                                 .Where(t => !t.IsMediaSpoiler).Distinct()
                                                                 .Select(tag => new MetadataTag
                                                                 {
                                                                     Name = tag.Name,
                                                                     Percentage = tag.Rank
                                                                 }).ToList(),
                                                             IsAdultOnly = media.IsAdult,
                                                             IsNotOriginallyJapanese = media.CountryOfOrigin != "JP",
                                                             Relations = relationsById[media.Id],
                                                             DictionaryEntries = ExtractAnilistCharacterNames(media.Characters)
                                                         }).ToList();
    }

    public static async Task<Metadata?> AnilistApi(int id)
    {
        var requestBody = new
                          {
                              query = """
                                              query ($id: Int) {
                                                  Media (id: $id) {
                                                    id
                                                    idMal
                                                    description
                                                    genres
                                                    isAdult
                                                    countryOfOrigin
                                                    tags {
                                                      name
                                                      rank
                                                      isMediaSpoiler
                                                    }
                                                    title {
                                                      romaji
                                                      english
                                                      native
                                                    }
                                                    startDate {
                                                      day
                                                      month
                                                      year
                                                    }
                                                    bannerImage
                                                    coverImage {
                                                      extraLarge
                                                    },
                                                    synonyms,
                                                    averageScore,
                                                    meanScore,
                                                    relations {
                                                      edges {
                                                        relationType(version: 3)
                                                        node {
                                                          id
                                                          type
                                                          format
                                                        }
                                                      }
                                                    }
                                                    characters(sort: ROLE) {
                                                      nodes {
                                                        name {
                                                          native
                                                          first
                                                          last
                                                        }
                                                      }
                                                    }
                                                  }
                                              }
                                      """,
                              variables = new { id = id }
                          };

        var httpClient = new HttpClient();
        var requestContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("https://graphql.anilist.co", requestContent);

        if (!response.IsSuccessStatusCode)
            return null;

        var contentStream = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AnilistResult>(contentStream,
                                                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var media = result?.Data?.Media;

        if (media == null)
            return null;

        var genres = media.Genres.Distinct().ToList();
        var tags = media.Tags.Where(t => !t.IsMediaSpoiler).Distinct().ToList();
        var relations = (await MapAnilistRelations([media]))[media.Id];

        return new Metadata
               {
                   OriginalTitle = media.Title.Native, RomajiTitle = media.Title.Romaji, EnglishTitle = media.Title.English,
                   ReleaseDate = media.ReleaseDate, Description = Regex.Replace(media.Description ?? "", "<.*?>", "").Trim(), Links =
                   [
                       new Link { LinkType = LinkType.Anilist, Url = $"https://anilist.co/manga/{media.Id}" }
                   ],
                   Image = media.CoverImage.ExtraLarge, Aliases = media.Synonyms, Rating = media.AverageScore ?? media.MeanScore ?? 0,
                   Genres = genres, Tags = tags.Select(tag => new MetadataTag
                   {
                       Name = tag.Name,
                       Percentage = tag.Rank
                   }).ToList(), IsAdultOnly = media.IsAdult,
                   IsNotOriginallyJapanese = media.CountryOfOrigin != "JP",
                   Relations = relations,
                   DictionaryEntries = ExtractAnilistCharacterNames(media.Characters)
               };
    }

    public static async Task<Metadata> AnilistAnimeApi(int anilistId)
    {
        var requestBody = new
                          {
                              query = """
                                              query ($id: Int) {
                                                  Media (id: $id) {
                                                    id
                                                    idMal
                                                    description
                                                    genres
                                                    isAdult
                                                    countryOfOrigin
                                                    tags {
                                                      name
                                                      rank
                                                      isMediaSpoiler
                                                    }
                                                    title {
                                                      romaji
                                                      english
                                                      native
                                                    }
                                                    startDate {
                                                      day
                                                      month
                                                      year
                                                    }
                                                    bannerImage
                                                    coverImage {
                                                      extraLarge
                                                    },
                                                    synonyms,
                                                    averageScore,
                                                    meanScore,
                                                    relations {
                                                      edges {
                                                        relationType(version: 3)
                                                        node {
                                                          id
                                                          type
                                                          format
                                                        }
                                                      }
                                                    }
                                                    characters(sort: ROLE) {
                                                      nodes {
                                                        name {
                                                          native
                                                          first
                                                          last
                                                        }
                                                      }
                                                    }
                                                  }
                                              }
                                      """,
                              variables = new { id = anilistId }
                          };

        var httpClient = new HttpClient();
        var requestContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("https://graphql.anilist.co", requestContent);

        if (!response.IsSuccessStatusCode)
        {
            return new Metadata();
        }

        var contentStream = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AnilistResult>(contentStream,
                                                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (result?.Data?.Media == null)
        {
            return new Metadata();
        }

        var media = result.Data.Media;

        var genres = media.Genres.Distinct().ToList();
        var tags = media.Tags.Where(t => !t.IsMediaSpoiler).Distinct().ToList();
        var relations = (await MapAnilistRelations([media]))[media.Id];

        return new Metadata
               {
                   OriginalTitle = media.Title.Native, RomajiTitle = media.Title.Romaji, EnglishTitle = media.Title.English,
                   ReleaseDate = media.ReleaseDate, Links =
                   [
                       new Link { LinkType = LinkType.Anilist, Url = $"https://anilist.co/anime/{media.Id}" },
                       new Link { LinkType = LinkType.Mal, Url = $"https://myanimelist.net/anime/{media.IdMal}" }
                   ],
                   Image = media.CoverImage.ExtraLarge, Aliases = media.Synonyms, Rating = media.AverageScore ?? media.MeanScore ?? 0,
                   Genres = genres, Tags = tags.Select(tag => new MetadataTag
                   {
                       Name = tag.Name,
                       Percentage = tag.Rank
                   }).ToList(), IsAdultOnly = media.IsAdult,
                   IsNotOriginallyJapanese = media.CountryOfOrigin != "JP",
                   Relations = relations,
                   DictionaryEntries = ExtractAnilistCharacterNames(media.Characters)
               };
    }

    private static List<DeckDictionaryEntry> ExtractAnilistCharacterNames(AnilistCharacterConnection? characters)
    {
        if (characters?.Nodes == null || characters.Nodes.Count == 0) return [];

        var names = characters.Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.Name.Native))
            .Select(n => (n.Name.Native, n.Name.First, n.Name.Last))
            .ToList();

        return BuildDictionaryEntriesFromNames(names);
    }

    private static async Task<Dictionary<int, List<MetadataRelation>>> MapAnilistRelations(List<AnilistMedia> medias)
    {
        var result = medias.ToDictionary(m => m.Id, _ => new List<MetadataRelation>());
        var parentEdges = new List<(int ChildId, AnilistRelationNode Parent)>();

        foreach (var media in medias)
        {
            foreach (var edge in media.Relations?.Edges ?? [])
            {
                if (edge.RelationType == "PARENT")
                {
                    parentEdges.Add((media.Id, edge.Node));
                    continue;
                }

                var mapping = MapAnilistRelationType(edge.RelationType);
                if (mapping == null)
                    continue;

                result[media.Id].Add(new MetadataRelation
                {
                    ExternalId = edge.Node.Id.ToString(),
                    LinkType = LinkType.Anilist,
                    RelationshipType = mapping.Value.Type,
                    TargetMediaType = MapAnilistTypeToMediaType(edge.Node.Type, edge.Node.Format),
                    SwapDirection = mapping.Value.SwapDirection
                });
            }
        }

        if (parentEdges.Count == 0)
            return result;

        var parentRelations = await FetchAnilistRelationEdges(parentEdges.Select(p => p.Parent.Id).Distinct().ToList());

        foreach (var (childId, parent) in parentEdges)
        {
            if (!parentRelations.TryGetValue(parent.Id, out var edges))
                continue;

            var backEdgeType = edges.FirstOrDefault(e => e.Node.Id == childId)?.RelationType;
            var mapping = ResolveParentRelation(backEdgeType);
            if (mapping == null)
                continue;

            result[childId].Add(new MetadataRelation
            {
                ExternalId = parent.Id.ToString(),
                LinkType = LinkType.Anilist,
                RelationshipType = mapping.Value.Type,
                TargetMediaType = MapAnilistTypeToMediaType(parent.Type, parent.Format),
                SwapDirection = mapping.Value.SwapDirection
            });
        }

        return result;
    }

    /// <summary>PARENT is the lossy inverse of SIDE_STORY, SPIN_OFF and SUMMARY; the parent's own edge back to the child names which.</summary>
    public static (DeckRelationshipType Type, bool SwapDirection)? ResolveParentRelation(string? parentBackEdgeType)
    {
        if (parentBackEdgeType == null)
            return (DeckRelationshipType.SideStory, false);

        var mapping = MapAnilistRelationType(parentBackEdgeType);
        return mapping == null ? null : (mapping.Value.Type, !mapping.Value.SwapDirection);
    }

    private static async Task<Dictionary<int, List<AnilistRelationEdge>>> FetchAnilistRelationEdges(List<int> ids)
    {
        var result = new Dictionary<int, List<AnilistRelationEdge>>();
        var httpClient = new HttpClient();

        foreach (var chunk in ids.Chunk(50))
        {
            var requestBody = new
                              {
                                  query = """
                                          query ($ids: [Int]) {
                                            Page (perPage: 50) {
                                              media (id_in: $ids) {
                                                id
                                                relations {
                                                  edges {
                                                    relationType(version: 3)
                                                    node {
                                                      id
                                                    }
                                                  }
                                                }
                                              }
                                            }
                                          }
                                          """,
                                  variables = new { ids = chunk }
                              };

            var requestContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("https://graphql.anilist.co", requestContent);
            if (!response.IsSuccessStatusCode)
                continue;

            var page = JsonSerializer.Deserialize<AnilistRelationEdgesResult>(await response.Content.ReadAsStringAsync(),
                                                                              new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (var media in page?.Data?.Page?.Media ?? [])
                result[media.Id] = media.Relations?.Edges ?? [];
        }

        return result;
    }

    private class AnilistRelationEdgesResult
    {
        public AnilistRelationEdgesData? Data { get; set; }
    }

    private class AnilistRelationEdgesData
    {
        public AnilistRelationEdgesPage? Page { get; set; }
    }

    private class AnilistRelationEdgesPage
    {
        public List<AnilistRelationEdgesMedia> Media { get; set; } = [];
    }

    private class AnilistRelationEdgesMedia
    {
        public int Id { get; set; }
        public AnilistRelations? Relations { get; set; }
    }

    private static (DeckRelationshipType Type, bool SwapDirection)? MapAnilistRelationType(string relationType)
    {
        return relationType switch
        {
            "SEQUEL" => (DeckRelationshipType.Sequel, true),
            "PREQUEL" => (DeckRelationshipType.Sequel, false),
            "SIDE_STORY" => (DeckRelationshipType.SideStory, true),
            "SPIN_OFF" => (DeckRelationshipType.Spinoff, true),
            "ALTERNATIVE" => (DeckRelationshipType.Alternative, false),
            "ADAPTATION" => (DeckRelationshipType.Adaptation, false),
            "SOURCE" => (DeckRelationshipType.Adaptation, true),
            "SAME_UNIVERSE" => (DeckRelationshipType.SameSetting, false),
            _ => null
        };
    }

    private static MediaType? MapAnilistTypeToMediaType(string type, string? format)
    {
        return (type, format) switch
        {
            ("ANIME", _) => MediaType.Anime,
            ("MANGA", "NOVEL") => MediaType.Novel,
            ("MANGA", "ONE_SHOT") => MediaType.Manga,
            ("MANGA", "MANGA") => MediaType.Manga,
            ("MANGA", _) => MediaType.Manga,
            _ => null
        };
    }
}