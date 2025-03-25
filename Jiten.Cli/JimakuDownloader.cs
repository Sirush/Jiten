using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Jiten.Cli.Data.Anilist;
using Jiten.Cli.Data.Jimaku;
using Jiten.Cli.Data.Tmdb;
using Jiten.Core.Data;
using Microsoft.Extensions.Configuration;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Jiten.Cli;

public class JimakuDownloader
{
    private static readonly HttpClient _jimakuHttpClient = new HttpClient();
    private static string _jimakuApiKey;
    private static string _tmdbApiKey;

    private static readonly List<string> _supportedExtensions = [".ass", ".srt", ".ssa"];

    public static async Task Download(string? baseDirectory, int startRange, int endRange)
    {
        if (string.IsNullOrEmpty(_jimakuApiKey))
        {
            var configuration = new ConfigurationBuilder()
                                .SetBasePath(Directory.GetCurrentDirectory())
                                .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "..", "Shared", "sharedsettings.json"), optional:true)
                                .AddJsonFile("sharedsettings.json", optional:true)
                                .AddJsonFile("appsettings.json", optional:true)
                                .Build();

            _jimakuApiKey = configuration["JimakuApiKey"] ?? string.Empty;
            _tmdbApiKey = configuration["TmdbApiKey"] ?? string.Empty;
            _jimakuHttpClient.DefaultRequestHeaders.Add("Authorization", _jimakuApiKey);
        }
        
        for (int n = startRange; n <= endRange; n++)
        {
            var entry = await GetEntryAsync(n);
            if (entry == null) continue;

            var currentDirectory = Path.Combine(baseDirectory, entry.Flags.Anime ? "anime" : entry.Flags.Movie ? "movies" : "dramas",
                                                entry.Id.ToString());

            var files = await GetFilesAsync(n);
            if (files == null) continue;
            files = SortFilenames(files.Select(f => f.Name).ToList()).Select(name => files.First(f => f.Name == name)).ToList();

            Console.WriteLine($"Files for entry {n} - {entry.Name}:");
            for (int j = 0; j < files.Count; j++)
            {
                Console.WriteLine($"{j + 1}. {files[j].Name}");
            }

            Console.WriteLine("Enter file numbers to download (comma-separated), 'a' for all, 'e' to exclude, 's' to select by prefix, or 'x' to select by suffix:");
            var input = Console.ReadLine()?.Trim().ToLower();

            List<JimakuFile> selectedFiles;
            if (input == "a")
            {
                selectedFiles = files;
            }
            else if (input?.StartsWith("e") == true)
            {
                Console.WriteLine("Enter file numbers to exclude (comma-separated):");
                input = Console.ReadLine()?.Trim().ToLower();

                var excludeNumbers = input.Split(',').Select(s => int.Parse(s.Trim()) - 1).ToList();
                selectedFiles = files.Where((file, index) => !excludeNumbers.Contains(index)).ToList();
            }
            else if (input?.StartsWith("s") == true)
            {
                Console.WriteLine("Enter the prefix to select files:");
                var prefix = Console.ReadLine()?.Trim().ToLower();

                if (string.IsNullOrEmpty(prefix))
                    continue;

                selectedFiles = files.Where(file => file.Name.ToLower().StartsWith(prefix)).ToList();
            }
            else if (input?.StartsWith("x") == true)
            {
                Console.WriteLine("Enter the suffix to select files:");
                var suffix = Console.ReadLine()?.Trim().ToLower();

                if (string.IsNullOrEmpty(suffix))
                    continue;

                selectedFiles = files.Where(file => file.Name.ToLower().EndsWith(suffix)).ToList();
            }
            else
            {
                if (string.IsNullOrEmpty(input))
                    continue;

                selectedFiles = input?.Split(',').Select(s => files[int.Parse(s.Trim()) - 1]).ToList() ?? [];
            }

            if (selectedFiles == null || selectedFiles.Count == 0) continue;

            entry.Files = selectedFiles;

            var serializerOptions =
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

            Directory.CreateDirectory(currentDirectory);

            await File.WriteAllTextAsync(Path.Combine(currentDirectory, "jimaku.json"), JsonSerializer.Serialize(entry, serializerOptions));

            foreach (var file in selectedFiles)
            {
                var filePath = Path.Combine(currentDirectory, file.Name);
                await DownloadFileAsync(file.Url, filePath);

                if (!filePath.EndsWith(".zip") && !filePath.EndsWith(".rar") && !filePath.EndsWith(".7z")) continue;

                using (var archive = ArchiveFactory.Open(filePath))
                {
                    foreach (var e in archive.Entries.Where(entry => !entry.IsDirectory))
                    {
                        string? entryFileName = Path.GetFileName(e.Key);
                        if (entryFileName == null) continue;

                        string fullZipToPath = Path.Combine(currentDirectory, entryFileName);

                        e.WriteToFile(fullZipToPath, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                    }
                }

                Console.WriteLine($"Extracted zip file {filePath}.\n");
            }

            // Preprocess ass files for subtitle parser
            var assFiles = Directory.GetFiles(currentDirectory, "*.ass");
            foreach (var assFile in assFiles)
            {
                var ssaFile = Path.ChangeExtension(assFile, ".ssa");
                var lines = await File.ReadAllLinesAsync(assFile);
                var filteredLines = lines.Where(line => !line.TrimStart().StartsWith(";")).ToList();
                await File.WriteAllLinesAsync(ssaFile, filteredLines);
                File.Delete(assFile);
            }


            var subtitleFiles = Directory.GetFiles(currentDirectory)
                                         .Where(f => _supportedExtensions.Contains(Path.GetExtension(f)))
                                         .ToList();
            Metadata metadata;

            if (entry.Flags.Anime && entry.AnilistId.HasValue)
            {
                metadata = await AnilistApi(entry.AnilistId.Value);
            }
            else if (entry.Flags.Movie && entry.TmdbId != null)
            {
                metadata = await TmdbMovieApi(entry.TmdbId.Replace("movie:", ""));
                metadata.OriginalTitle = entry.JapaneseName;
                metadata.EnglishTitle = entry.EnglishName;
                metadata.RomajiTitle = entry.Name;
            }
            else if (entry.TmdbId != null)
            {
                metadata = await TmdbTvApi(entry.TmdbId.Replace("tv:", ""));
                metadata.OriginalTitle = entry.JapaneseName;
                metadata.EnglishTitle = entry.EnglishName;
                metadata.RomajiTitle = entry.Name;
            }
            else
            {
                Console.WriteLine("No metadata found for this entry.");
                continue;
            }

            List<string> extractedFiles = new();
            foreach (var file in subtitleFiles)
            {
                var parser = new SubtitlesParser.Classes.Parsers.SubParser();

                await using var fileStream = File.OpenRead(file);
                var items = parser.ParseStream(fileStream);
                List<string> lines = items.SelectMany(it => it.PlaintextLines).ToList();
                var txtPath = Path.ChangeExtension(file, ".txt");
                await File.WriteAllLinesAsync(txtPath, lines);
                extractedFiles.Add(txtPath);
            }

            if (!string.IsNullOrEmpty(metadata.Image))
            {
                var coverImagePath = Path.Combine(currentDirectory, "cover.jpg");
                using var httpClient = new HttpClient();
                var imageBytes = await httpClient.GetByteArrayAsync(metadata.Image);
                await File.WriteAllBytesAsync(coverImagePath, imageBytes);
            }

            if (extractedFiles.Count > 1)
            {
                for (var i = 0; i < extractedFiles.Count; i++)
                {
                    string? file = extractedFiles[i];
                    metadata.Children.Add(new Metadata() { FilePath = file, OriginalTitle = $"Episode {i + 1}" });
                }
            }
            else
            {
                if (extractedFiles.Count == 0)
                    continue;

                metadata.FilePath = extractedFiles.First();
            }

            var metadataPath = Path.Combine(currentDirectory, "metadata.json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, serializerOptions));
        }
    }

    private static async Task<JimakuEntry?> GetEntryAsync(int id)
    {
        var response = await _jimakuHttpClient.GetAsync($"https://jimaku.cc/api/entries/{id}");
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JimakuEntry>(content);
    }

    private static async Task<List<JimakuFile>?> GetFilesAsync(int id)
    {
        var response = await _jimakuHttpClient.GetAsync($"https://jimaku.cc/api/entries/{id}/files");
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<JimakuFile>>(content);
    }

    private static async Task DownloadFileAsync(string url, string filePath)
    {
        var response = await _jimakuHttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(filePath, FileMode.OpenOrCreate);
        await response.Content.CopyToAsync(fs);
    }

    private static async Task<Metadata> AnilistApi(int anilistId)
    {
        var requestBody = new
                          {
                              query = @"
        query ($id: Int) {
            Media (id: $id) {
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
        }",
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

        return new Metadata
               {
                   OriginalTitle = result.Data.Media.Title.Native,
                   RomajiTitle = result.Data.Media.Title.Romaji,
                   EnglishTitle = result.Data.Media.Title.English,
                   ReleaseDate = result.Data.Media.ReleaseDate,
                   Links =
                   [
                       new Link { LinkType = LinkType.Anilist, Url = $"https://anilist.co/anime/{result.Data.Media.Id}" },
                       new Link { LinkType = LinkType.Mal, Url = $"https://myanimelist.net/anime/{result.Data.Media.IdMal}" }
                   ],
                   Image = result.Data.Media.CoverImage.ExtraLarge
               } ?? new Metadata();
    }

    private static async Task<Metadata> TmdbMovieApi(string tmdbId)
    {
        var response = await _jimakuHttpClient.GetAsync($"https://api.themoviedb.org/3/movie/{tmdbId}?api_key={_tmdbApiKey}");
        if (!response.IsSuccessStatusCode) return new Metadata();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<TmdbMovie>(content);

        if (result == null)
            return new Metadata();
        
        if (result.PosterPath != null)
            result.PosterPath = $"https://image.tmdb.org/t/p/w500/{result.PosterPath}";

        var links = new List<Link>();
        if (result.ImdbId != null)
        {
            links.Add(new Link { LinkType = LinkType.Imdb, Url = $"https://www.imdb.com/title/{result.ImdbId}" });
        }

        links.Add(new Link { LinkType = LinkType.Tmdb, Url = $"https://www.themoviedb.org/movie/{tmdbId}" });

        return new Metadata
               {
                   OriginalTitle = result.OriginalTitle,
                   EnglishTitle = result.Title,
                   ReleaseDate = result.ReleaseDate,
                   Links = links,
                   Image = result.PosterPath,
               };
    }

    private static async Task<Metadata> TmdbTvApi(string tmdbId)
    {
        var response = await _jimakuHttpClient.GetAsync($"https://api.themoviedb.org/3/tv/{tmdbId}?api_key={_tmdbApiKey}");
        if (!response.IsSuccessStatusCode) return new Metadata();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<TmdbTv>(content);

        if (result == null)
            return new Metadata();

        if (result.PosterPath != null)
            result.PosterPath = $"https://image.tmdb.org/t/p/w500/{result.PosterPath}";
        
        
        return new Metadata
               {
                   OriginalTitle = result.OriginalName,
                   EnglishTitle = result.Name,
                   ReleaseDate = result.FirstAirDate,
                   Image = result.PosterPath,
                   Links = new List<Link> { new() { LinkType = LinkType.Tmdb, Url = $"https://www.themoviedb.org/tv/{tmdbId}" } }
               };
    }

    private static List<string> SortFilenames(List<string> filenames)
    {
        return filenames.OrderBy(filename =>
        {
            var match = Regex.Match(filename, @"(?:S\d{2}E([0-9]{2,4}))|([0-9]{2,4})");
            
            if (!match.Success) 
                return int.MaxValue;
            
            var group1 = match.Groups[1].Value;
            var group2 = match.Groups[2].Value;
            return !string.IsNullOrEmpty(group1) ? int.Parse(group1) : int.Parse(group2);

        }).ToList();
    }
}