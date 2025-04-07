using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Jiten.Cli.Data.Anilist;
using Jiten.Cli.Data.GoogleBooks;
using Jiten.Cli.Data.Vndb;
using Jiten.Core.Data;

namespace Jiten.Cli;

public static class MetadataDownloader
{
    private static Func<string, Task<List<Metadata>>> _currentApi;
    private static bool _autoName;
    private static string _autoNamePrefix = "";

    public static async Task DownloadMetadata(string? directory, string? api, bool autoName = false, string autoNamePrefix = "")
    {
        if (!Directory.Exists(directory))
        {
            Console.WriteLine("Folder does not exist");
            return;
        }

        _currentApi = api.Trim() switch
        {
            "vndb" => VndbApi,
            "books" => GoogleBooksApi,
            "anilist" => AnilistNovelApi,
            "anilist-manga" => AnilistMangaApi,
            _ => throw new ArgumentException("Invalid API")
        };

        _autoName = autoName;
        _autoNamePrefix = autoNamePrefix;

        var directories = Directory.GetDirectories(directory);
        foreach (var dir in directories)
        {
            await ProcessDirectory(dir);
        }
    }

    private static async Task ProcessDirectory(string directoryPath)
    {
        var metadataPath = Path.Combine(directoryPath, "metadata.json");
        if (File.Exists(metadataPath))
        {
            Console.WriteLine($"Skipping {directoryPath} - metadata.json exists");
            return;
        }

        var files = Directory.GetFiles(directoryPath)
                             .Where(f => f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)
                                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                             .ToList();

        if (!files.Any())
        {
            return;
        }

        files.Sort();

        var orderedFiles = await GetFileOrder(files);
        if (!orderedFiles.Any())
        {
            return;
        }

        var metadatas = new List<Metadata>();
        int i = 1;
        foreach (var file in orderedFiles)
        {
            var apiResult = await ProcessFileWithApi(file, i);
            if (apiResult == null) continue;

            apiResult.FilePath = file;
            metadatas.Add(apiResult);
            i++;
        }

        if (metadatas.Count == 0)
        {
            return;
        }

        string? originalTitle = null;
        string? romajiTitle = null;
        string? englishTitle = null;

        var suggestedOriginalTitle = metadatas.First().OriginalTitle;
        var suggestedRomajiTitle = metadatas.First().RomajiTitle;
        var suggestedEnglishTitle = metadatas.First().EnglishTitle;


        if (!_autoName)
        {
            Console.WriteLine($"Enter Original Title (suggested: {suggestedOriginalTitle}):");
            originalTitle = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(originalTitle))
                originalTitle = suggestedOriginalTitle;

            Console.WriteLine($"Enter Romaji Title (suggested: {suggestedRomajiTitle}):");
            romajiTitle = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(romajiTitle))
                romajiTitle = suggestedRomajiTitle;

            Console.WriteLine($"Enter English Title (suggested: {suggestedEnglishTitle}):");
            englishTitle = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(englishTitle))
                englishTitle = suggestedEnglishTitle;
        }
        else
        {
            originalTitle = suggestedOriginalTitle;
            romajiTitle = suggestedRomajiTitle;
            englishTitle = suggestedEnglishTitle;

            Console.WriteLine("Autonaming enabled:");
            Console.WriteLine($"Original Title: {originalTitle}");
            Console.WriteLine($"Romaji Title: {romajiTitle}");
            Console.WriteLine($"English Title: {englishTitle}");
        }

        var imageUrl = metadatas.First().Image;
        if (!string.IsNullOrEmpty(imageUrl))
        {
            var coverImagePath = Path.Combine(directoryPath, "cover.jpg");
            using var httpClient = new HttpClient();
            var imageBytes = await httpClient.GetByteArrayAsync(imageUrl);
            await File.WriteAllBytesAsync(coverImagePath, imageBytes);
        }

        // Encoder necessary to not have \uXXXX in the output
        var serializerOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

        if (metadatas.Count > 1)
        {
            var image = metadatas.First().Image;
            var links = metadatas.First().Links;
            var releaseDate = metadatas.First().ReleaseDate;


            string? input = "";
            if (!_autoName)
            {
                // Ask if you want to name the first metadata originaltitle
                Console.WriteLine($"What should **{Path.GetFileNameWithoutExtension(metadatas.First().FilePath)}** be named? Or press enter to keep the name {metadatas.First().OriginalTitle}");

                input = Console.ReadLine()?.Trim();
            }
            else
            {
                input = $"{_autoNamePrefix} 1";
            }

            if (!string.IsNullOrEmpty(input))
            {
                metadatas.First().OriginalTitle = input;
                metadatas.First().EnglishTitle = null;
                metadatas.First().RomajiTitle = null;
                metadatas.First().Image = null;
                metadatas.First().Links = [];
            }

            var metadata = new Metadata
                           {
                               OriginalTitle = originalTitle, RomajiTitle = romajiTitle, EnglishTitle = englishTitle,
                               ReleaseDate = releaseDate, Image = image, Links = links, Children = metadatas,
                           };

            await File.WriteAllTextAsync(metadataPath,
                                         JsonSerializer.Serialize(metadata, serializerOptions));
        }
        else
        {
            var metadata = metadatas.First();
            metadata.OriginalTitle = originalTitle;
            metadata.RomajiTitle = romajiTitle;
            metadata.EnglishTitle = englishTitle;

            await File.WriteAllTextAsync(metadataPath,
                                         JsonSerializer.Serialize(metadata, serializerOptions));
        }

        Console.WriteLine("====================================");
    }

    private static async Task<List<string>> GetFileOrder(List<string> files)
    {
        string? input = "";

        var directory = new DirectoryInfo(Path.GetDirectoryName(files.First()) ?? string.Empty);
        Console.WriteLine($"Directory: {directory.Name}");

        Console.WriteLine("Files in directory:");
        for (int i = 0; i < files.Count; i++)
        {
            Console.WriteLine($"{i + 1} - {Path.GetFileName(files[i])}");
        }

        if (files.Count <= 1)
        {
            Console.WriteLine("Only one file found, press enter to proceed or 's' to skip:");
            input = Console.ReadLine()?.Trim().ToLower();
            return input is "s" or "skip" ? [] : files;
        }

        Console.WriteLine("\nEnter file order (comma-separated numbers), 'a' for all or 's' to skip:");
        input = Console.ReadLine()?.Trim().ToLower();

        if (string.IsNullOrEmpty(input) || input == "s" || input == "skip")
            return [];

        if (input == "a" || input == "all")
            return files;

        var orderedFiles = new List<string>();
        var numbers = input.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var num in numbers)
        {
            if (int.TryParse(num.Trim(), out int index) && index >= 1 && index <= files.Count)
            {
                orderedFiles.Add(files[index - 1]);
            }
        }

        return orderedFiles;
    }

    private static async Task<Metadata?> ProcessFileWithApi(string filePath, int fileIndex)
    {
        var fileName = new DirectoryInfo(Path.GetDirectoryName(filePath)!).Name;

        while (true)
        {
            Console.WriteLine($"File: {Path.GetFileNameWithoutExtension(filePath)}");

            if (_autoName && fileIndex > 1)
            {
                Console.WriteLine($"Auto naming enabled, named {_autoNamePrefix} {fileIndex}");
                return new Metadata() { OriginalTitle = $"{_autoNamePrefix} {fileIndex}" };
            }

            Console.WriteLine($"Press enter to query **{fileName}**, press 'q' or 'query' to write a custom query, 'a' or 'abort' to abort or type a string to give a custom title and return immediately:");

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
            {
                input = fileName;
            }
            else if (input.Equals("q", StringComparison.OrdinalIgnoreCase) || input.Equals("query", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Enter new query text:");
                var queryText = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(queryText))
                {
                    fileName = queryText;
                    continue;
                }
            }
            else if (input.Equals("a", StringComparison.OrdinalIgnoreCase) || input.Equals("abort", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            else
            {
                return new Metadata { OriginalTitle = input };
            }

            var results = await _currentApi.Invoke(input);

            if (results.Count == 0)
            {
                Console.WriteLine("No results found. Try a different query.");
                continue;
            }

            Console.WriteLine($"\nResults for {Path.GetFileName(fileName)}:");

            for (int i = 0; i < results.Count; i++)
            {
                Console.WriteLine($"{i + 1} - {results[i].OriginalTitle} - {results[i].RomajiTitle} - {results[i].EnglishTitle} ({results[i].ReleaseDate})");
            }

            Console.WriteLine("Choose a number or 'q' to query with different text:");
            input = Console.ReadLine()?.Trim().ToLower();

            if (string.IsNullOrEmpty(input))
                return null;

            if (input is "q" or "query")
            {
                Console.WriteLine("Enter new query text:");
                var queryText = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(queryText))
                {
                    fileName = queryText;
                    continue;
                }
            }

            if (int.TryParse(input, out int choice) && choice >= 1 && choice <= results.Count)
            {
                return results[choice - 1];
            }

            Console.WriteLine("Invalid choice");
        }
    }

    private static async Task<List<Metadata>> VndbApi(string query)
    {
        List<VndbRequestResult> requestResults = new List<VndbRequestResult>();

        VnDbRequestPageResult? result = new VnDbRequestPageResult();
        var filter = new List<object> { "search", "=", query };

        var requestContent = new StringContent(JsonSerializer.Serialize(new
                                                                        {
                                                                            filters = filter, fields =
                                                                                "id,title,released,titles{main,official,lang,title,latin},image{url,sexual}, extlinks{label,url, name}",
                                                                            results = 10, page = 1
                                                                        }));
        var http = new HttpClient();
        requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var response = await http.PostAsync("https://api.vndb.org/kana/vn", requestContent);

        if (response.IsSuccessStatusCode)
        {
            var contentStream = await response.Content.ReadAsStringAsync();
            var serializerOptions =
                new JsonSerializerOptions { Converters = { new VndbDateTimeConverter() }, PropertyNameCaseInsensitive = true };

            result = JsonSerializer.Deserialize<VnDbRequestPageResult>(contentStream, serializerOptions);

            requestResults.AddRange(result!.Results);
        }

        List<Metadata> metadatas = [];
        foreach (var requestResult in requestResults)
        {
            var metadata = new Metadata
                           {
                               OriginalTitle = requestResult.Titles.FirstOrDefault(t => t.Lang == "ja")?.Title ?? requestResult.Title,
                               RomajiTitle = requestResult.Titles.FirstOrDefault(t => t.Lang == "ja")?.Latin,
                               EnglishTitle = requestResult.Titles.FirstOrDefault(t => t.Lang == "en")?.Title,
                               ReleaseDate = requestResult.Released,
                               Links = [new Link { LinkType = LinkType.Vndb, Url = $"https://vndb.org/{requestResult.Id}" }],
                               Image = requestResult.Image?.Url
                           };

            metadatas.Add(metadata);
        }

        return metadatas;
    }

    private static async Task<List<Metadata>> GoogleBooksApi(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/books/v1/volumes?q={query}");
        var http = new HttpClient();
        var response = await http.SendAsync(request);

        List<Metadata> metadatas = [];

        if (response.IsSuccessStatusCode)
        {
            var contentStream = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<GoogleBooksRequestResult>(contentStream,
                                                                              new JsonSerializerOptions
                                                                              {
                                                                                  Converters = { new VndbDateTimeConverter() },
                                                                                  PropertyNameCaseInsensitive = true
                                                                              });

            metadatas = result!.Items.Select(i => new Metadata
                                                  {
                                                      OriginalTitle = i.VolumeInfo.Title, RomajiTitle = null, EnglishTitle = null,
                                                      ReleaseDate = i.VolumeInfo.PublishedDate,
                                                      Links = [new Link { LinkType = LinkType.GoogleBooks, Url = i.SelfLink }],
                                                      Image = i.VolumeInfo.ImageLinks?.Thumbnail
                                                  }).ToList();
        }

        return metadatas;
    }

    private static async Task<List<Metadata>> AnilistNovelApi(string query)
    {
        return await AnilistApi(query, "NOVEL");
    }

    private static async Task<List<Metadata>> AnilistMangaApi(string query)
    {
        return await AnilistApi(query, "MANGA");
    }

    private static async Task<List<Metadata>> AnilistApi(string query, string format)
    {
        var requestBody = new
                          {
                              query = @"
        query ($search: String, $type: MediaType, $format: MediaFormat) {
          Page {
            media (search: $search, type: $type, format: $format) {
              id
              idMal
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
              }
            }
          }
        }",
                              variables = new { search = query, type = "MANGA", format = format }
                          };

        var httpClient = new HttpClient();
        var requestContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("https://graphql.anilist.co", requestContent);

        if (!response.IsSuccessStatusCode)
        {
            return new List<Metadata>();
        }

        var contentStream = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AnilistResult>(contentStream,
                                                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return result?.Data?.Page?.Media.Select(media => new Metadata
                                                         {
                                                             OriginalTitle = media.Title.Native, RomajiTitle = media.Title.Romaji,
                                                             EnglishTitle = media.Title.English, ReleaseDate = media.ReleaseDate, Links =
                                                             [
                                                                 new Link
                                                                 {
                                                                     LinkType = LinkType.Anilist,
                                                                     Url = $"https://anilist.co/manga/{media.Id}"
                                                                 }
                                                             ],
                                                             Image = media.CoverImage.ExtraLarge
                                                         }).ToList() ?? [];
    }
}