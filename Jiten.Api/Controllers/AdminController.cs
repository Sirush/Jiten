using System.Net;
using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Cli;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.Providers;
using Jiten.Parser;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SharpCompress.Archives;
using SharpCompress.Common;
using StackExchange.Redis;
using MetadataProviderHelper = Jiten.Core.MetadataProviderHelper;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/admin")]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize("RequiresAdmin")]
public partial class AdminController(
    IConfiguration config,
    HttpClient httpClient,
    IBackgroundJobClient backgroundJobs,
    JitenDbContext dbContext,
    UserDbContext userContext,
    ILogger<AdminController> logger)
    : ControllerBase
{
    [HttpGet("duplicate-check")]
    public async Task<IResult> DuplicateCheck([FromQuery] string? title, [FromQuery] MediaType? mediaType)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length < 2)
            return Results.Ok(new List<DuplicateCheckDeckDto>());

        var normalisedTitle = title.Trim();

        var query = dbContext.DeckTitles.AsNoTracking()
            .Where(dt => EF.Functions.ILike(dt.Title, $"%{normalisedTitle}%"));

        if (mediaType.HasValue)
            query = query.Where(dt => dt.Deck!.MediaType == mediaType.Value);

        var decks = await query
            .OrderBy(dt => dt.Title.Length)
            .Take(10)
            .Select(dt => new DuplicateCheckDeckDto
            {
                DeckId = dt.DeckId,
                Title = dt.Title,
                MediaType = dt.Deck!.MediaType
            })
            .ToListAsync();

        decks = decks
            .GroupBy(d => d.DeckId)
            .Select(g => g.First())
            .Take(5)
            .ToList();

        return Results.Ok(decks);
    }

    [HttpGet("recent-decks")]
    public async Task<List<MediaSuggestionDto>> GetRecentDecks([FromQuery] int limit = 12)
    {
        limit = Math.Clamp(limit, 1, 30);
        return await dbContext.Decks
            .AsNoTracking()
            .Where(d => d.ParentDeckId == null)
            .OrderByDescending(d => d.CreationDate)
            .Take(limit)
            .Select(d => new MediaSuggestionDto
            {
                DeckId = d.DeckId, OriginalTitle = d.OriginalTitle, RomajiTitle = d.RomajiTitle,
                EnglishTitle = d.EnglishTitle, MediaType = d.MediaType, CoverName = d.CoverName
            })
            .ToListAsync();
    }

    [HttpGet("search-media")]
    public async Task<IResult> SearchMedia(string provider, string query, string? author)
    {
        logger.LogInformation("Admin searching media: Provider={Provider}, Query={Query}, Author={Author}", provider, query, author);
        return Results.Ok(provider switch
        {
            "AnilistManga" => await MetadataProviderHelper.AnilistMangaSearchApi(query),
            "AnilistNovel" => await MetadataProviderHelper.AnilistNovelSearchApi(query),
            "JikanAnime" => await MetadataProviderHelper.JikanAnimeSearchApi(query),
            "JikanManga" => await MetadataProviderHelper.JikanMangaSearchApi(query),
            "JikanNovel" => await MetadataProviderHelper.JikanNovelSearchApi(query),
            "GoogleBooks" =>
                await MetadataProviderHelper.GoogleBooksSearchApi(query + (!string.IsNullOrEmpty(author) ? $"+inauthor:{author}" : "")),
            "Vndb" => await MetadataProviderHelper.VndbSearchApi(query),
            "Igdb" => await MetadataProviderHelper.IgdbSearchApi(config["IgdbClientId"]!, config["IgdbClientSecret"]!, query),
            "Bookmeter" => await MetadataProviderHelper.BookmeterSearchApi(query),
            _ => new List<Metadata>()
        });
    }

    [HttpPost("add-deck")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(254857600)]
    public async Task<IActionResult> AddMediaDeck([FromForm] AddMediaRequest model)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            string path = Path.Join(config["StaticFilesPath"], "tmp", Guid.NewGuid().ToString());
            Directory.CreateDirectory(path);
            string? coverImagePathOrUrl = Path.Join(path, "cover.jpg");
            Metadata metadata = new()
                                {
                                    ReleaseDate = model.ReleaseDate.ToDateTime(new TimeOnly()), OriginalTitle = model.OriginalTitle.Trim(),
                                    RomajiTitle = model.RomajiTitle?.Trim(), EnglishTitle = model.EnglishTitle?.Trim(),
                                    Description = model.Description?.Trim(), Image = coverImagePathOrUrl, Links = new List<Link>(),
                                    Rating = model.Rating,
                                    Genres = model.Genres,
                                    Tags = model.Tags.Select(t => new Core.Data.Providers.MetadataTag
                                    {
                                        Name = t.Name,
                                        Percentage = t.Percentage
                                    }).ToList(),
                                    IsAdultOnly = model.IsAdultOnly,
                                    IsNotOriginallyJapanese = model.IsNotOriginallyJapanese,
                                    DictionaryEntries = model.DictionaryEntries
                                        .Select(s => s.Trim())
                                        .Where(s => !string.IsNullOrEmpty(s))
                                        .Select(s => new DeckDictionaryEntry { Surface = s, EntryType = DeckDictionaryEntryType.Name })
                                        .ToList()
                                };

            // Parse links and aliases from form data
            for (int i = 0; i < Request.Form.Keys.Count; i++)
            {
                string urlKey = $"links[{i}].url";
                string linkTypeKey = $"links[{i}].linkType";

                if (Request.Form.TryGetValue(urlKey, out var urlValue) &&
                    Request.Form.TryGetValue(linkTypeKey, out var linkTypeValue) &&
                    !string.IsNullOrEmpty(urlValue) &&
                    !string.IsNullOrEmpty(linkTypeValue) &&
                    Enum.TryParse<LinkType>(linkTypeValue, out var linkType))
                {
                    metadata.Links.Add(new Link { Url = urlValue.ToString(), LinkType = linkType });
                }

                string aliasKey = $"aliases[{i}]";
                if (Request.Form.TryGetValue(aliasKey, out var aliasValue) && !string.IsNullOrEmpty(aliasValue))
                {
                    metadata.Aliases.Add(aliasValue.ToString());
                }
            }

            // Map relations from request
            if (model.Relations.Any())
            {
                metadata.Relations = model.Relations.Select(r => new Core.Data.Providers.MetadataRelation
                {
                    ExternalId = r.ExternalId,
                    LinkType = r.LinkType,
                    RelationshipType = r.RelationshipType,
                    TargetMediaType = r.TargetMediaType,
                    SwapDirection = r.SwapDirection
                }).ToList();
            }

            if (model.CoverImage is { Length: > 0 })
            {
                await using var stream = new FileStream(coverImagePathOrUrl, FileMode.Create);
                await model.CoverImage.CopyToAsync(stream);
            }
            else
            {
                // If there's no cover image uploaded, then it should be an URL instead
                if (Request.Form.TryGetValue("coverImage", out var coverImageUrlValue) && !string.IsNullOrEmpty(coverImageUrlValue))
                {
                    var imageUrl = coverImageUrlValue.ToString();

                    if (!IsValidExternalUrl(imageUrl))
                        return BadRequest("Invalid image URL. IP addresses and internal URLs are not allowed.");

                    try
                    {
                        var response = await httpClient.GetAsync(imageUrl);
                        response.EnsureSuccessStatusCode();

                        await using var imageStream = await response.Content.ReadAsStreamAsync();
                        await using var fileStream = new FileStream(coverImagePathOrUrl, FileMode.Create);
                        await imageStream.CopyToAsync(fileStream);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new ArgumentException($"Unable to download cover image from URL: {ex.Message}", ex);
                    }
                }
                else
                {
                    return BadRequest("No cover image or URL provided.");
                }
            }

            if (model.File is { Length: > 0 } && (model.Subdecks == null || !model.Subdecks.Any(sd => sd.File is { Length: > 0 })))
            {
                var mainFilePath = Path.Join(path, $"{Guid.NewGuid()}{Path.GetExtension(model.File.FileName)}");
                await using var stream = new FileStream(mainFilePath, FileMode.Create);
                await model.File.CopyToAsync(stream);

                metadata.FilePath = mainFilePath;
            }
            else if (model.Subdecks != null && model.Subdecks.Any(sd => sd.File is { Length: > 0 }))
            {
                metadata.Children = new List<Metadata>();

                foreach (var subdeck in model.Subdecks)
                {
                    if (subdeck.File is not { Length: > 0 }) continue;
                    var subdeckFilePath = Path.Join(path, $"{Guid.NewGuid()}{Path.GetExtension(subdeck.File.FileName)}");

                    await using var stream = new FileStream(subdeckFilePath, FileMode.Create);
                    await subdeck.File.CopyToAsync(stream);

                    var subdeckMetadata = new Metadata { OriginalTitle = subdeck.OriginalTitle, FilePath = subdeckFilePath };

                    metadata.Children.Add(subdeckMetadata);
                }
            }
            else
            {
                // Return error if no files provided
                return BadRequest("No media files provided. Please upload at least one file.");
            }

            backgroundJobs.Enqueue<ParseJob>(job => job.Parse(metadata, model.MediaType, bool.Parse(config["StoreRawText"] ?? "false")));

            logger.LogInformation("Admin added new deck: Title={Title}, MediaType={MediaType}, SubdeckCount={SubdeckCount}",
                                  model.OriginalTitle, model.MediaType, metadata.Children?.Count ?? 0);

            return Ok(new
                      {
                          Message = "Media added successfully.", Title = model.OriginalTitle, Path = path,
                          SubdeckCount = metadata.Children?.Count ?? 0
                      });
        }
        catch (ArgumentException argEx)
        {
            logger.LogWarning("Failed to add deck - invalid input: {Error}", argEx.Message);
            return BadRequest(new { Message = $"Invalid input: {argEx.Message}" });
        }
        catch (IOException ioEx)
        {
            logger.LogError(ioEx, "Failed to add deck - IO error while saving files");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "An error occurred while saving files." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add deck - unexpected error");
            return StatusCode(StatusCodes.Status500InternalServerError,
                              new { Message = "An unexpected error occurred while processing your request." });
        }
    }

    [HttpGet("deck/{id}")]
    public async Task<IActionResult> GetDeck(int id)
    {
        var deck = await dbContext.Decks.AsNoTracking()
                            .Include(d => d.Children)
                            .Include(d => d.Links)
                            .Include(d => d.Titles)
                            .Include(d => d.DeckGenres)
                            .Include(d => d.DeckTags)
                            .ThenInclude(dt => dt.Tag)
                            .Include(d => d.DictionaryEntries)
                            .Include(d => d.RelationshipsAsSource)
                            .ThenInclude(r => r.TargetDeck)
                            .Include(d => d.RelationshipsAsTarget)
                            .ThenInclude(r => r.SourceDeck)
                            .FirstOrDefaultAsync(d => d.DeckId == id);

        if (deck == null)
            return NotFound(new { Message = $"No deck found with ID {id}." });

        var subDecks = dbContext.Decks.AsNoTracking().Where(d => d.ParentDeckId == id);

        subDecks = subDecks
            .OrderBy(dw => dw.DeckOrder);

        var mainDeckDto = new DeckDto(deck);
        mainDeckDto.Relationships = DeckRelationshipDto.FromDeck(deck.RelationshipsAsSource, deck.RelationshipsAsTarget);

        List<DeckDto> subDeckDtos = new();

        foreach (var subDeck in subDecks)
            subDeckDtos.Add(new DeckDto(subDeck));

        var dto = new DeckDetailDto { MainDeck = mainDeckDto, SubDecks = subDeckDtos };

        return Ok(dto);
    }

    [HttpPost("update-deck")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(254857600)]
    public async Task<IActionResult> UpdateMediaDeck([FromForm] UpdateMediaRequest model)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var deck = await dbContext.Decks
                                  .Include(d => d.Links)
                                  .Include(d => d.RawText)
                                  .Include(d => d.Children)
                                  .ThenInclude(deck => deck.RawText)
                                  .Include(deck => deck.Titles)
                                  .Include(d => d.DeckGenres)
                                  .Include(d => d.DeckTags)
                                  .ThenInclude(dt => dt.Tag)
                                  .Include(d => d.DictionaryEntries)
                                  .Include(d => d.RelationshipsAsSource)
                                  .FirstOrDefaultAsync(d => d.DeckId == model.DeckId);

        if (deck == null)
            return NotFound(new { Message = $"No deck found with ID {model.DeckId}." });

        // Update basic properties
        deck.MediaType = model.MediaType;
        deck.OriginalTitle = model.OriginalTitle.Trim();
        deck.RomajiTitle = model.RomajiTitle?.Trim();
        deck.EnglishTitle = model.EnglishTitle?.Trim();
        deck.ReleaseDate = model.ReleaseDate;
        deck.Description = model.Description?.Trim();
        deck.DifficultyOverride = model.DifficultyOverride;
        deck.HideDialoguePercentage = model.HideDialoguePercentage;

        // Update cover image if provided
        if (model.CoverImage is { Length: > 0 })
        {
            using var memoryStream = new MemoryStream();
            await model.CoverImage.CopyToAsync(memoryStream);
            var cover = memoryStream.ToArray();

            var coverUrl = await BunnyCdnHelper.UploadFile(cover, $"{deck.DeckId}/cover.jpg");
            deck.CoverName = coverUrl;
        }

        string path = Path.Join(config["StaticFilesPath"], "tmp", Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);

        // Update text if provided
        if (model.File is { Length: > 0 })
        {
            var (text, stats) = await GetTextFromFile(model.File);
            if (deck.RawText != null)
                deck.RawText.RawText = text;
            else
                deck.RawText = new DeckRawText(text);
            if (stats.DurationMs > 0)
            {
                deck.SpeechDuration = stats.DurationMs;
                deck.SpeechMoraCount = stats.MoraCount;
            }
        }


        // Update links
        if (model.Links.Any())
        {
            var existingLinkIds = deck.Links.Select(l => l.LinkId).ToHashSet();
            var newLinkIds = model.Links.Where(l => l.LinkId > 0).Select(l => l.LinkId).ToHashSet();

            // Remove links that are no longer present
            var linksToRemove = deck.Links.Where(l => !newLinkIds.Contains(l.LinkId));
            dbContext.RemoveRange(linksToRemove);

            // Update existing links and add new ones
            var existingLinksById = deck.Links.ToDictionary(l => l.LinkId);
            foreach (var link in model.Links)
            {
                if (link.LinkId > 0 && existingLinkIds.Contains(link.LinkId))
                {
                    var existingLink = existingLinksById[link.LinkId];
                    existingLink.Url = link.Url;
                    existingLink.LinkType = link.LinkType;
                }
                else
                {
                    deck.Links.Add(link);
                }
            }
        }

        // Update aliases
        if (model.Aliases.Any())
        {
            var newAliases = model.Aliases.Where(a => !string.IsNullOrEmpty(a)).ToHashSet();

            // Remove aliases that are no longer present
            var aliasesToRemove = deck.Titles.Where(t => t.TitleType == DeckTitleType.Alias && !newAliases.Contains(t.Title));
            dbContext.RemoveRange(aliasesToRemove);

            // Add new aliases
            foreach (var alias in model.Aliases)
            {
                var trimmed = alias.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var existingAlias = deck.Titles.FirstOrDefault(l => l.Title == trimmed);
                if (existingAlias == null)
                {
                    deck.Titles.Add(new DeckTitle { DeckId = deck.DeckId, Title = trimmed, TitleType = DeckTitleType.Alias });
                }
            }
        }

        // Update dictionary entries (character names etc.)
        {
            var newSurfaces = model.DictionaryEntries
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet();

            var entriesToRemove = deck.DictionaryEntries.Where(e => !newSurfaces.Contains(e.Surface));
            dbContext.RemoveRange(entriesToRemove);

            foreach (var surface in newSurfaces)
            {
                if (deck.DictionaryEntries.All(e => e.Surface != surface))
                    deck.DictionaryEntries.Add(new DeckDictionaryEntry { DeckId = deck.DeckId, Surface = surface, EntryType = DeckDictionaryEntryType.Name });
            }
        }

        // Update genres
        if (model.Genres != null && model.Genres.Any())
        {
            var newGenres = model.Genres.ToHashSet();
            var existingGenres = deck.DeckGenres.Select(dg => (int)dg.Genre).ToHashSet();

            // Remove genres no longer present
            var genresToRemove = deck.DeckGenres.Where(dg => !newGenres.Contains((int)dg.Genre));
            dbContext.RemoveRange(genresToRemove);

            // Add new genres
            foreach (var genreValue in model.Genres)
            {
                if (!existingGenres.Contains(genreValue))
                {
                    deck.DeckGenres.Add(new DeckGenre { DeckId = deck.DeckId, Genre = (Genre)genreValue });
                }
            }
        }
        else if (model.Genres != null)
        {
            // Clear all if empty list provided
            dbContext.RemoveRange(deck.DeckGenres);
        }

        // Update tags
        if (model.Tags != null && model.Tags.Any())
        {
            var newTagIds = model.Tags.Select(t => t.TagId).ToHashSet();
            var existingTagIds = deck.DeckTags.Select(dt => dt.TagId).ToHashSet();

            // Remove tags no longer present
            var tagsToRemove = deck.DeckTags.Where(dt => !newTagIds.Contains(dt.TagId));
            dbContext.RemoveRange(tagsToRemove);

            // Update existing and add new tags
            foreach (var tag in model.Tags)
            {
                var existingTag = deck.DeckTags.FirstOrDefault(dt => dt.TagId == tag.TagId);
                if (existingTag != null)
                {
                    existingTag.Percentage = tag.Percentage;
                }
                else
                {
                    deck.DeckTags.Add(new DeckTag { DeckId = deck.DeckId, TagId = tag.TagId, Percentage = tag.Percentage });
                }
            }
        }
        else if (model.Tags != null)
        {
            // Clear all if empty list provided
            dbContext.RemoveRange(deck.DeckTags);
        }

        // Update subdecks if provided
        var newChildDecks = new List<Deck>();
        if (model.Subdecks != null && model.Subdecks.Count != 0)
        {
            var existingSubdeckIds = deck.Children.Select(d => d.DeckId).ToHashSet();
            var newSubdeckIds = model.Subdecks.Where(d => d.DeckId > 0).Select(d => d.DeckId).ToHashSet();

            // Remove subdecks that are no longer present
            var subdecksToRemove = deck.Children.Where(d => !newSubdeckIds.Contains(d.DeckId));
            dbContext.RemoveRange(subdecksToRemove);

            // Update existing subdecks and add new ones
            var existingSubdecksById = deck.Children.ToDictionary(d => d.DeckId);
            foreach (var subdeck in model.Subdecks)
            {
                if (subdeck.DeckId > 0 && existingSubdeckIds.Contains(subdeck.DeckId))
                {
                    var existingSubdeck = existingSubdecksById[subdeck.DeckId];
                    existingSubdeck.OriginalTitle = subdeck.OriginalTitle.Trim();
                    existingSubdeck.DeckOrder = subdeck.DeckOrder;
                    existingSubdeck.DifficultyOverride = subdeck.DifficultyOverride;

                    if (subdeck.File is { Length: > 0 })
                    {
                        var (text, stats) = await GetTextFromFile(subdeck.File);
                        existingSubdeck.RawText!.RawText = text;
                        if (stats.DurationMs > 0)
                        {
                            existingSubdeck.SpeechDuration = stats.DurationMs;
                            existingSubdeck.SpeechMoraCount = stats.MoraCount;
                        }
                    }
                }
                else
                {
                    var newDeck = new Deck
                                  {
                                      MediaType = deck.MediaType, OriginalTitle = subdeck.OriginalTitle.Trim(),
                                      DeckOrder = subdeck.DeckOrder, DifficultyOverride = subdeck.DifficultyOverride
                                  };

                    if (subdeck.File is { Length: > 0 })
                    {
                        var (text, stats) = await GetTextFromFile(subdeck.File);
                        newDeck.RawText = new DeckRawText(text);
                        newDeck.SpeechDuration = stats.DurationMs;
                        newDeck.SpeechMoraCount = stats.MoraCount;
                    }

                    deck.Children.Add(newDeck);
                    newChildDecks.Add(newDeck);
                }
            }
        }

        // Update relationships
        if (model.Relationships != null && model.Relationships.Any())
        {
            // Filter out inverse relationships from the input (only accept direct/primary relationships)
            var primaryRelationships = model.Relationships
                .Where(r => DeckRelationship.IsPrimaryRelationship(r.RelationshipType))
                .ToList();

            var existingRelationships = deck.RelationshipsAsSource.ToList();
            var newRelationshipKeys = primaryRelationships
                .Select(r => (r.TargetDeckId, r.RelationshipType))
                .ToHashSet();

            // Remove relationships no longer present
            foreach (var existing in existingRelationships)
            {
                if (!newRelationshipKeys.Contains((existing.TargetDeckId, existing.RelationshipType)))
                {
                    dbContext.DeckRelationships.Remove(existing);
                }
            }

            // Add new relationships
            var existingKeys = existingRelationships
                .Select(r => (r.TargetDeckId, r.RelationshipType))
                .ToHashSet();

            foreach (var rel in primaryRelationships)
            {
                if (existingKeys.Contains((rel.TargetDeckId, rel.RelationshipType)))
                    continue;

                // Check if the inverse relationship already exists
                var inverseType = DeckRelationship.GetInverse(rel.RelationshipType);
                var inverseExists = await dbContext.DeckRelationships.AnyAsync(r =>
                    r.SourceDeckId == rel.TargetDeckId &&
                    r.TargetDeckId == deck.DeckId &&
                    r.RelationshipType == inverseType);

                if (inverseExists)
                    continue;

                deck.RelationshipsAsSource.Add(new DeckRelationship
                {
                    SourceDeckId = deck.DeckId,
                    TargetDeckId = rel.TargetDeckId,
                    RelationshipType = rel.RelationshipType
                });
            }
        }
        else if (model.Relationships != null)
        {
            // Clear all if empty list provided
            dbContext.RemoveRange(deck.RelationshipsAsSource);
        }

        deck.LastUpdate = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();

        var newChildIdsWithText = newChildDecks
            .Where(d => d.RawText != null)
            .Select(d => d.DeckId)
            .ToList();
        if (newChildIdsWithText.Count > 0 && !model.Reparse)
            backgroundJobs.Enqueue<ParseNewSubdecksJob>(
                job => job.ParseNewSubdecks(deck.DeckId, newChildIdsWithText));

        if (model.Reparse)
            backgroundJobs.Enqueue<ReparseJob>(job => job.Reparse(deck.DeckId));

        logger.LogInformation("Admin updated deck: DeckId={DeckId}, Title={Title}, Reparse={Reparse}",
                              deck.DeckId, deck.OriginalTitle, model.Reparse);

        return Ok(new { Message = $"Media deck {deck.DeckId} updated successfully" });

        async Task<(string text, SubtitleStats stats)> GetTextFromFile(IFormFile file)
        {
            var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var filePath = Path.Join(path, $"{Guid.NewGuid()}{fileExtension}");
            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);
            stream.Close();

            var text = await ExtractTextFromFile(filePath, fileExtension);

            if (string.IsNullOrEmpty(text))
            {
                throw new Exception($"No text found in the {fileExtension} file.");
            }

            var stats = await ComputeSubtitleStatsIfNeeded(filePath, fileExtension);
            return (text, stats);
        }

        static async Task<string> ExtractTextFromFile(string filePath, string fileExtension)
        {
            if (fileExtension is ".ass" or ".srt" or ".ssa")
            {
                var extractor = new SubtitleExtractor();
                return await extractor.Extract(filePath);
            }

            if (fileExtension == ".epub")
                return await new EbookExtractor().ExtractTextFromEbook(filePath);

            if (fileExtension == ".mokuro")
                return await new MokuroExtractor().Extract(filePath, false);

            return await System.IO.File.ReadAllTextAsync(filePath);
        }

        static async Task<SubtitleStats> ComputeSubtitleStatsIfNeeded(string filePath, string fileExtension)
        {
            if (fileExtension is not (".ass" or ".srt" or ".ssa"))
                return SubtitleStats.Empty;

            var extractor = new SubtitleExtractor();
            var items = await extractor.ExtractItems(filePath);
            if (items.Count == 0)
                return SubtitleStats.Empty;

            return await SubtitleMoraRateCalculator.ComputeAsync(items);
        }
    }

    [HttpPost("reparse-media-by-type/{mediaType}")]
    public async Task<IActionResult> ReparseMediaByType(MediaType mediaType)
    {
        var mediaToReparse = await dbContext.Decks.AsNoTracking()
                                            .Where(d => d.MediaType == mediaType && d.ParentDeck == null)
                                            .ToListAsync();

        if (!mediaToReparse.Any())
            return NotFound(new { Message = $"No media found of type {mediaType}" });

        int count = 0;
        foreach (var deck in mediaToReparse)
        {
            backgroundJobs.Enqueue<ReparseJob>(job => job.Reparse(deck.DeckId));
            count++;
        }

        logger.LogInformation("Admin queued reparse for media type: MediaType={MediaType}, Count={Count}", mediaType, count);
        return Ok(new { Message = $"Reparsing {count} media items of type {mediaType}", Count = count });
    }

    [HttpPost("reparse-decks-before-date")]
    public async Task<IActionResult> ReparseDecksBeforeDate([FromBody] DateTimeOffset cutoffDate)
    {
        var decksToReparse = await dbContext.Decks.AsNoTracking()
            .Where(d => d.LastUpdate < cutoffDate && d.ParentDeck == null)
            .ToListAsync();

        if (!decksToReparse.Any())
            return NotFound(new { Message = $"No decks found with last update before {cutoffDate:g}" });

        int count = 0;
        foreach (var deck in decksToReparse)
        {
            backgroundJobs.Enqueue<ReparseJob>(job => job.Reparse(deck.DeckId));
            count++;
        }

        logger.LogInformation("Admin queued reparse for decks before: Date={Date}, Count={Count}", cutoffDate, count);
        return Ok(new { Message = $"Reparsing {count} decks updated before {cutoffDate:g}", Count = count });
    }

    [HttpPost("reparse-all-by-size")]
    public async Task<IActionResult> ReparseAllBySize()
    {
        var decksToReparse = await dbContext.Decks.AsNoTracking()
            .Where(d => d.ParentDeck == null)
            .OrderBy(d => d.CharacterCount)
            .ToListAsync();

        if (!decksToReparse.Any())
            return NotFound(new { Message = "No decks found" });

        int count = 0;
        foreach (var deck in decksToReparse)
        {
            backgroundJobs.Enqueue<ReparseJob>(job => job.Reparse(deck.DeckId));
            count++;
        }

        logger.LogInformation("Admin queued reparse all by size: Count={Count}", count);
        return Ok(new { Message = $"Reparsing {count} decks (smallest to largest)", Count = count });
    }

    [HttpPost("recompute-frequencies")]
    public IActionResult RecomputeFrequencies()
    {
        backgroundJobs.Enqueue<ComputationJob>(job => job.RecomputeFrequencies());

        logger.LogInformation("Admin queued recompute frequencies job");
        return Ok(new { Message = "Recomputing frequencies job has been queued" });
    }
    
    [HttpPost("recompute-kanji-frequencies")]
    public IActionResult RecomputeKanjiFrequencies()
    {
        backgroundJobs.Enqueue<ComputationJob>(job => job.RecomputeKanjiFrequencies());

        logger.LogInformation("Admin queued recompute kanji frequencies job");
        return Ok(new { Message = "Recomputing kanji frequencies job has been queued" });
    }

    [HttpPost("recompute-coverages")]
    public async Task<IActionResult> RecomputeUserCoverages()
    {
        var userIds = await userContext.Users.AsNoTracking().Select(u => u.Id).ToListAsync();

        foreach (var userId in userIds)
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        logger.LogInformation("Admin queued recompute coverages for all users: UserCount={UserCount}", userIds.Count);
        return Ok(new { Message = "Recomputing user coverages for all users has been queued" });
    }

    [HttpPost("recompute-accomplishments")]
    public async Task<IActionResult> RecomputeUserAccomplishments()
    {
        var userIds = await userContext.Users.AsNoTracking().Select(u => u.Id).ToListAsync();

        foreach (var userId in userIds)
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserAccomplishments(userId));

        logger.LogInformation("Admin queued recompute accomplishments for all users: UserCount={UserCount}", userIds.Count);
        return Ok(new { Message = $"Recomputing user accomplishments for {userIds.Count} users has been queued", Count = userIds.Count });
    }

    [HttpPost("recompute-kanji-grids")]
    public async Task<IActionResult> RecomputeUserKanjiGrids()
    {
        var userIds = await userContext.Users.AsNoTracking().Select(u => u.Id).ToListAsync();

        foreach (var userId in userIds)
            backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserKanjiGrid(userId));

        logger.LogInformation("Admin queued recompute kanji grids for all users: UserCount={UserCount}", userIds.Count);
        return Ok(new { Message = $"Recomputing kanji grids for {userIds.Count} users has been queued", Count = userIds.Count });
    }

    [HttpPost("recompute-coverage/{userId}")]
    public IActionResult RecomputeUserCoverage(string userId)
    {
        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));
        logger.LogInformation("Admin queued recompute coverage for user: UserId={UserId}", userId);
        return Ok(new { Message = $"Recomputing user coverage for user {userId} has been queued" });
    }
    
    [HttpPost("recompute-all-deck-stats")]
    public async Task<IActionResult> RecomputeAllDeckStats()
    {
        var deckIds = await dbContext.Decks
                                   .Select(d => d.DeckId)
                                   .ToListAsync();

        foreach (var deckId in deckIds)
        {
            backgroundJobs.Enqueue<StatsComputationJob>(job => job.ComputeDeckCoverageStats(deckId));
        }
        logger.LogInformation("Admin queued recompute for all deck stats");
        return Ok(new { Message = $"Recomputing all deck stats has been queued" });
    }

    /// <summary>
    /// Recompute difficulty for a single deck (and its children if applicable)
    /// </summary>
    [HttpPost("recompute-difficulty/{deckId}")]
    public async Task<IActionResult> RecomputeDeckDifficulty(int deckId)
    {
        var deck = await dbContext.Decks
            .Include(d => d.Children)
            .FirstOrDefaultAsync(d => d.DeckId == deckId);

        if (deck == null)
            return NotFound(new { Message = $"Deck {deckId} not found" });

        backgroundJobs.Enqueue<DifficultyComputationJob>(
            job => job.ComputeDeckDifficulty(deckId, true));

        logger.LogInformation("Admin queued difficulty recomputation for deck {DeckId}", deckId);
        return Ok(new { Message = $"Queued difficulty computation for deck {deckId}" });
    }

    [HttpPost("recompute-parent-speech-speed")]
    public async Task<IActionResult> RecomputeParentSpeechSpeed()
    {
        var parentDecks = await dbContext.Decks
            .Include(d => d.Children)
            .Where(d => d.ParentDeckId == null && d.Children.Any())
            .ToListAsync();

        int updated = 0;
        foreach (var parent in parentDecks)
        {
            var childrenWithSpeech = parent.Children.Where(c => c.SpeechDuration > 0).ToList();
            if (childrenWithSpeech.Count == 0) continue;

            var avgSpeed = childrenWithSpeech.Average(c => c.SpeechSpeed);
            var totalDuration = childrenWithSpeech.Sum(c => c.SpeechDuration);
            parent.SpeechDuration = totalDuration;
            parent.SpeechMoraCount = (long)(avgSpeed * (totalDuration / 60000.0));
            updated++;
        }

        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin recomputed speech speed for {Count} parent decks", updated);
        return Ok(new { Message = $"Recomputed speech speed for {updated} parent decks", Count = updated });
    }

    /// <summary>
    /// Reaggregate parent difficulties from their children
    /// </summary>
    [HttpPost("reaggregate-parent-difficulties")]
    public async Task<IActionResult> ReaggregateParentDifficulties()
    {
        // Get all parent decks (decks with children)
        var parentDecks = await dbContext.Decks
            .Where(d => d.ParentDeckId == null && d.Children.Any())
            .Select(d => d.DeckId)
            .ToListAsync();

        foreach (var id in parentDecks)
        {
            backgroundJobs.Enqueue<DifficultyComputationJob>(
                job => job.ReaggregateParentDifficulty(id));
        }

        logger.LogInformation("Admin queued difficulty reaggregation for {Count} parent decks", parentDecks.Count);
        return Ok(new { Message = $"Queued difficulty reaggregation for {parentDecks.Count} parent decks", Count = parentDecks.Count });
    }

    /// <summary>
    /// Reaggregate a single parent deck's difficulty from its children
    /// </summary>
    [HttpPost("reaggregate-parent-difficulty/{deckId}")]
    public async Task<IActionResult> ReaggregateParentDifficulty(int deckId)
    {
        var deck = await dbContext.Decks
            .Include(d => d.Children)
            .FirstOrDefaultAsync(d => d.DeckId == deckId);

        if (deck == null)
            return NotFound(new { Message = "Deck not found" });

        if (!deck.Children.Any())
            return BadRequest(new { Message = "Deck has no children to aggregate from" });

        backgroundJobs.Enqueue<DifficultyComputationJob>(
            job => job.ReaggregateParentDifficulty(deckId));

        logger.LogInformation("Admin queued difficulty reaggregation for deck {DeckId}", deckId);
        return Ok(new { Message = "Queued difficulty reaggregation", DeckId = deckId });
    }

    [HttpGet("issues")]
    public async Task<IActionResult> GetIssues()
    {
        var decks = await dbContext.Decks.AsNoTracking().Include(d => d.Links).Include(d => d.DeckGenres).Include(d => d.DeckTags).ToListAsync();
        var issues = new IssuesDto();

        // We always need the romaji title for ordering
        issues.MissingRomajiTitles = decks.Where(d => d.ParentDeckId == null)
                                          .Where(d => string.IsNullOrEmpty(d.RomajiTitle))
                                          .Select(d => d.DeckId).ToList();
        issues.ZeroCharacters = decks.Where(d => d.CharacterCount == 0).Select(d => d.DeckId).ToList();
        issues.MissingLinks = decks.Where(d => d.ParentDeckId == null).Where(d => d.Links.Count == 0).Select(d => d.DeckId).ToList();
        issues.MissingReleaseDate =
            decks.Where(d => d.ParentDeckId == null).Where(d => d.ReleaseDate == default).Select(d => d.DeckId).ToList();
        issues.MissingDescription = decks.Where(d => d.ParentDeckId == null).Where(d => string.IsNullOrEmpty(d.Description))
                                         .Select(d => d.DeckId).ToList();
        issues.MissingGenres = decks.Where(d => d.ParentDeckId == null)
                                    .Where(d => d.DeckGenres.Count == 0)
                                    .Select(d => d.DeckId).ToList();
        issues.MissingTags = decks.Where(d => d.ParentDeckId == null)
                                  .Where(d => d.DeckTags.Count == 0)
                                  .Select(d => d.DeckId).ToList();

        return Ok(issues);
    }

    [HttpGet("words/missing-furigana")]
    public async Task<IActionResult> GetMissingFurigana(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null)
    {
        var nameOnlyWordIds = await JmDictHelper.LoadNameOnlyWordIds(dbContext);

        var query = dbContext.WordForms.AsNoTracking()
            .Where(wf => wf.FormType == JmDictFormType.KanjiForm)
            .Where(wf => !wf.RubyText.Contains("["))
            .Where(wf => !nameOnlyWordIds.Contains(wf.WordId));

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(wf => wf.Text.Contains(search));

        var totalCount = await query.CountAsync();

        List<JmDictWordForm> forms;

        if (sortBy == "frequency")
        {
            var joined = query
                .GroupJoin(
                    dbContext.JmDictWordFrequencies.AsNoTracking(),
                    wf => wf.WordId,
                    freq => freq.WordId,
                    (wf, freqs) => new { wf, freqs })
                .SelectMany(
                    x => x.freqs.DefaultIfEmpty(),
                    (x, freq) => new { Form = x.wf, FrequencyRank = freq != null ? (int?)freq.FrequencyRank : null });

            forms = await joined
                .OrderBy(x => x.FrequencyRank == null).ThenBy(x => x.FrequencyRank).ThenBy(x => x.Form.WordId)
                .Skip(offset)
                .Take(limit)
                .Select(x => x.Form)
                .ToListAsync();
        }
        else
        {
            forms = await query
                .OrderBy(wf => wf.WordId).ThenBy(wf => wf.ReadingIndex)
                .Skip(offset)
                .Take(limit)
                .ToListAsync();
        }

        var wordIds = forms.Select(f => f.WordId).Distinct().ToList();

        var frequencyLookup = await dbContext.JmDictWordFrequencies.AsNoTracking()
            .Where(f => wordIds.Contains(f.WordId))
            .ToDictionaryAsync(f => f.WordId, f => f.FrequencyRank);

        var words = await dbContext.JMDictWords.AsNoTracking()
            .Where(w => wordIds.Contains(w.WordId))
            .Include(w => w.Forms.OrderBy(f => f.ReadingIndex))
            .ToDictionaryAsync(w => w.WordId);

        var items = forms.Select(f =>
        {
            words.TryGetValue(f.WordId, out var word);
            frequencyLookup.TryGetValue(f.WordId, out var freqRank);
            return new MissingFuriganaDto
            {
                WordId = f.WordId,
                ReadingIndex = f.ReadingIndex,
                Text = f.Text,
                RubyText = f.RubyText ?? "",
                FormType = f.FormType,
                PartsOfSpeech = word?.PartsOfSpeech ?? [],
                FrequencyRank = freqRank > 0 ? freqRank : null,
                AllForms = word?.Forms.Select(af => new WordFormSummary
                {
                    ReadingIndex = af.ReadingIndex,
                    Text = af.Text,
                    RubyText = af.RubyText ?? "",
                    FormType = af.FormType
                }).ToList() ?? []
            };
        }).ToList();

        return Ok(new MissingFuriganaPaginatedResponse { Items = items, TotalCount = totalCount });
    }

    [HttpGet("words/{wordId}/forms")]
    public async Task<IActionResult> GetWordForms(int wordId)
    {
        var word = await dbContext.JMDictWords.AsNoTracking()
            .Include(w => w.Forms.OrderBy(f => f.ReadingIndex))
            .FirstOrDefaultAsync(w => w.WordId == wordId);

        if (word == null)
            return NotFound(new { Message = $"Word {wordId} not found" });

        var forms = word.Forms.Select(f => new WordFormSummary
        {
            ReadingIndex = f.ReadingIndex,
            Text = f.Text,
            RubyText = f.RubyText ?? "",
            FormType = f.FormType
        }).ToList();

        return Ok(new { word.WordId, word.PartsOfSpeech, Forms = forms });
    }

    [HttpPut("words/{wordId}/forms/{readingIndex}/ruby-text")]
    public async Task<IActionResult> UpdateRubyText(
        int wordId,
        short readingIndex,
        [FromBody] UpdateRubyTextRequest request)
    {
        var form = await dbContext.WordForms
            .FirstOrDefaultAsync(wf => wf.WordId == wordId && wf.ReadingIndex == readingIndex);

        if (form == null)
            return NotFound(new { Message = $"Form {wordId}:{readingIndex} not found" });

        var oldRubyText = form.RubyText;
        form.RubyText = request.RubyText;
        await dbContext.SaveChangesAsync();

        try
        {
            var connection = await ConnectionMultiplexer.ConnectAsync(config.GetConnectionString("Redis")!);
            var redisDb = connection.GetDatabase();
            await redisDb.KeyDeleteAsync($"jmdict:word:{wordId}");
            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate Redis cache for word {WordId}", wordId);
        }

        logger.LogInformation(
            "Admin updated RubyText for {WordId}:{ReadingIndex}: '{OldRubyText}' -> '{NewRubyText}'",
            wordId, readingIndex, oldRubyText, request.RubyText);

        return Ok(new { Message = "RubyText updated", WordId = wordId, ReadingIndex = readingIndex });
    }

    [HttpPost("fetch-metadata/{deckId}")]
    public async Task<IActionResult> FetchMetadata(int deckId)
    {
        var deck = await dbContext.Decks.Include(d => d.Links).FirstOrDefaultAsync(d => d.DeckId == deckId);

        if (deck == null)
            return NotFound(new { Message = $"Deck {deckId} not found" });

        switch (deck.MediaType)
        {
            case MediaType.Anime or MediaType.Manga:
                if (deck.Links.Any(l => l.LinkType == LinkType.Anilist))
                    backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchAnilistMissingMetadata(deckId));
                else if (deck.Links.Any(l => l.LinkType == LinkType.Mal))
                    backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchJikanMissingMetadata(deckId));
                break;
            case MediaType.Drama or MediaType.Movie:
                backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchTmdbMissingMetadata(deckId));
                break;
            case MediaType.VisualNovel:
                backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchVndbMissingMetadata(deckId));
                break;
            case MediaType.VideoGame:
                backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchIgdbMissingMetadata(deckId));
                break;
            case MediaType.Novel or MediaType.NonFiction:
                if (deck.Links.Any(l => l.LinkType == LinkType.Anilist))
                    backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchAnilistMissingMetadata(deckId));
                else if (deck.Links.Any(l => l.LinkType == LinkType.Mal))
                    backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchJikanMissingMetadata(deckId));
                else
                    backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchGoogleBooksMissingMetadata(deckId));
                break;
            default:
                return NotFound("No fetch job for this media type.");
        }

        return Ok(new { Message = $"Fetching metadata for deck {deckId}" });
    }

    [HttpPost("fetch-all-missing-metadata")]
    public async Task<IActionResult> FetchAllMissingMetadata()
    {
        var decks = await dbContext
                          .Decks.Where(d => d.ParentDeck == null)
                          .Include(deck => deck.Links).ToListAsync();

        foreach (var deck in decks)
        {
            switch (deck.MediaType)
            {
                case MediaType.Anime or MediaType.Manga:
                    if (deck.Links.Any(l => l.LinkType == LinkType.Anilist))
                        backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchAnilistMissingMetadata(deck.DeckId));
                    else if (deck.Links.Any(l => l.LinkType == LinkType.Mal))
                        backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchJikanMissingMetadata(deck.DeckId));
                    break;
                case MediaType.Drama or MediaType.Movie:
                    backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchTmdbMissingMetadata(deck.DeckId));
                    break;
                case MediaType.VisualNovel:
                    backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchVndbMissingMetadata(deck.DeckId));
                    break;
                case MediaType.VideoGame:
                    backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchIgdbMissingMetadata(deck.DeckId));
                    break;
                case MediaType.Novel or MediaType.NonFiction:
                    if (deck.Links.Any(l => l.LinkType == LinkType.Anilist))
                    {
                        backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchAnilistMissingMetadata(deck.DeckId));
                    }
                    else if (deck.Links.Any(l => l.LinkType == LinkType.Mal))
                    {
                        backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchJikanMissingMetadata(deck.DeckId));
                    }
                    else
                    {
                        if (deck.ReleaseDate == default || string.IsNullOrEmpty(deck.Description))
                            backgroundJobs.Enqueue<FetchMetadataJob>(job => job.FetchGoogleBooksMissingMetadata(deck.DeckId));
                    }

                    break;
                default:
                    break;
            }
        }

        return Ok(new { Message = $"Fetching missing metadata for {decks.Count} decks", Count = decks.Count });
    }

    [HttpGet("get-jimaku/{id}")]
    public async Task<IActionResult> GetJimaku(int id)
    {
        var jimakuResult = new JimakuResultDto();

        var entry = await MetadataProviderHelper.JimakuGetEntryAsync(httpClient, config["JimakuApiKey"]!, id);
        if (entry == null)
        {
            return NotFound();
        }

        jimakuResult.Entry = entry;
        jimakuResult.Files = await MetadataProviderHelper.JimakuGetFilesAsync(httpClient, config["JimakuApiKey"]!, id);

        return Ok(jimakuResult);
    }

    [HttpPost("add-jimaku-deck")]
    public async Task<IActionResult> AddJimakuDeck([FromBody] AddJimakuDeckRequest model)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var entry = await MetadataProviderHelper.JimakuGetEntryAsync(httpClient, config["JimakuApiKey"]!, model.JimakuId);
            if (entry == null) return NotFound("Jimaku entry not found.");

            string path = Path.Join(config["StaticFilesPath"], "tmp", Guid.NewGuid().ToString());
            Directory.CreateDirectory(path);

            Metadata metadata;
            if (entry.Flags.Anime && entry.AnilistId.HasValue)
            {
                metadata = await MetadataProviderHelper.AnilistAnimeApi(entry.AnilistId.Value) ??
                           throw new Exception("Anilist API returned null.");
            }
            else if (entry.Flags.Movie && entry.TmdbId != null)
            {
                metadata = await MetadataProviderHelper.TmdbMovieApi(entry.TmdbId.Replace("movie:", ""), config["TmdbApiKey"]!);
                metadata.OriginalTitle = entry.JapaneseName;
                metadata.EnglishTitle = entry.EnglishName;
                metadata.RomajiTitle = entry.Name;
            }
            else if (entry.TmdbId != null)
            {
                metadata = await MetadataProviderHelper.TmdbTvApi(entry.TmdbId.Replace("tv:", ""), config["TmdbApiKey"]!);
                metadata.OriginalTitle = entry.JapaneseName;
                metadata.EnglishTitle = entry.EnglishName;
                metadata.RomajiTitle = entry.Name;
            }
            else
            {
                return BadRequest("No metadata provider found for this entry.");
            }

            if (!string.IsNullOrEmpty(metadata.Image))
            {
                var coverImagePath = Path.Join(path, "cover.jpg");
                var response = await httpClient.GetAsync(metadata.Image);
                response.EnsureSuccessStatusCode();

                await using var imageStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(coverImagePath, FileMode.Create);
                await imageStream.CopyToAsync(fileStream);
                metadata.Image = coverImagePath;
            }

            var downloadedFiles = new List<string>();
            foreach (var file in model.Files)
            {
                var filePath = Path.Join(path, file.Name);
                await MetadataProviderHelper.JimakuDownloadFileAsync(httpClient, file.Url, filePath);

                if (Path.GetExtension(filePath) is ".zip" or ".rar" or ".7z")
                {
                    using var archive = ArchiveFactory.Open(filePath);
                    foreach (var e in archive.Entries.Where(currentEntry => !currentEntry.IsDirectory &&
                                                                            SubtitleExtractor.SupportedExtensions
                                                                                .Contains(Path.GetExtension(currentEntry.Key))))
                    {
                        var entryPath = Path.Combine(path, Path.GetFileName(e.Key)!);
                        e.WriteToFile(entryPath, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                        downloadedFiles.Add(entryPath);
                    }
                }
                else
                {
                    downloadedFiles.Add(filePath);
                }
            }

            // Extract text from subtitle files
            var extractor = new SubtitleExtractor();
            var subtitleFiles = downloadedFiles
                                .Where(f => SubtitleExtractor.SupportedExtensions.Contains(Path.GetExtension(f)))
                                .ToList();

            List<string> extractedFiles = [];
            var subtitleStatsByFile = new Dictionary<string, SubtitleStats>();
            foreach (var file in subtitleFiles)
            {
                var text = await extractor.Extract(file);
                if (string.IsNullOrEmpty(text)) continue;

                var txtPath = Path.ChangeExtension(file, ".txt");
                await System.IO.File.WriteAllTextAsync(txtPath, text);
                extractedFiles.Add(txtPath);

                var items = await extractor.ExtractItems(file);
                if (items.Count > 0)
                {
                    var stats = await SubtitleMoraRateCalculator.ComputeAsync(items);
                    subtitleStatsByFile[txtPath] = stats;
                }
            }

            if (extractedFiles.Count > 1)
            {
                metadata.Children = new List<Metadata>();
                for (var i = 0; i < extractedFiles.Count; i++)
                {
                    var file = extractedFiles[i];
                    subtitleStatsByFile.TryGetValue(file, out var stats);
                    metadata.Children.Add(new Metadata
                    {
                        FilePath = file,
                        OriginalTitle = $"Episode {i + 1}",
                        SpeechDuration = stats.DurationMs,
                        SpeechMoraCount = stats.MoraCount
                    });
                }
            }
            else if (extractedFiles.Count == 1)
            {
                var file = extractedFiles.First();
                metadata.FilePath = file;
                if (subtitleStatsByFile.TryGetValue(file, out var stats))
                {
                    metadata.SpeechDuration = stats.DurationMs;
                    metadata.SpeechMoraCount = stats.MoraCount;
                }
            }
            else
            {
                return BadRequest("No valid subtitle files found.");
            }

            if (string.IsNullOrEmpty(metadata.OriginalTitle))
                metadata.OriginalTitle = metadata.EnglishTitle ?? metadata.RomajiTitle ?? entry.Name;

            var mediaType = entry.Flags.Anime ? MediaType.Anime : entry.Flags.Movie ? MediaType.Movie : MediaType.Drama;
            backgroundJobs.Enqueue<ParseJob>(job => job.Parse(metadata, mediaType, bool.Parse(config["StoreRawText"] ?? "false")));

            return Ok(new
                      {
                          Message = "Media added successfully.", Title = metadata.OriginalTitle, Path = path,
                          SubdeckCount = metadata.Children?.Count ?? 0
                      });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                              new { Message = "An unexpected error occurred while processing your request.", Details = ex.ToString() });
        }
    }

    [HttpPost("flush-redis-cache")]
    public async Task<IResult> FlushRedisCache()
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(config.GetConnectionString("Redis")!);
        var redisDb = connection.GetDatabase();
        redisDb.Execute("FLUSHALL");

        await connection.CloseAsync();

        logger.LogWarning("Admin flushed Redis cache");
        return Results.Ok();
    }

    [HttpPost("replace-word-reading")]
    public async Task<IActionResult> ReplaceWordReading(
        [FromBody] ReplaceWordReadingRequest request,
        [FromServices] WordReplacementService wordReplacementService)
    {
        if (request.OldWordId == request.NewWordId && request.OldReadingIndex == request.NewReadingIndex)
        {
            return BadRequest(new { Message = "Old and new word/reading are identical" });
        }

        try
        {
            var result = await wordReplacementService.ReplaceAsync(
                request.OldWordId, request.OldReadingIndex,
                request.NewWordId, request.NewReadingIndex,
                request.DryRun);

            if (request.DryRun)
            {
                logger.LogInformation(
                    "Admin dry-run word replacement: {OldWordId}:{OldReadingIndex} -> {NewWordId}:{NewReadingIndex}",
                    request.OldWordId, request.OldReadingIndex, request.NewWordId, request.NewReadingIndex);
            }
            else
            {
                logger.LogWarning(
                    "Admin executed word replacement: {OldWordId}:{OldReadingIndex} -> {NewWordId}:{NewReadingIndex}",
                    request.OldWordId, request.OldReadingIndex, request.NewWordId, request.NewReadingIndex);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Word replacement failed");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { Message = "Word replacement failed", Details = ex.Message });
        }
    }

    [HttpPost("split-word")]
    public async Task<IActionResult> SplitWord(
        [FromBody] SplitWordRequest request,
        [FromServices] WordReplacementService wordReplacementService)
    {
        if (request.NewWords.Count < 2)
        {
            return BadRequest(new { Message = "Split requires at least 2 new words" });
        }

        try
        {
            var result = await wordReplacementService.SplitAsync(
                request.OldWordId, request.OldReadingIndex,
                request.NewWords,
                request.DryRun);

            if (request.DryRun)
            {
                logger.LogInformation(
                    "Admin dry-run word split: {OldWordId}:{OldReadingIndex} -> {NewWordCount} words",
                    request.OldWordId, request.OldReadingIndex, request.NewWords.Count);
            }
            else
            {
                logger.LogWarning(
                    "Admin executed word split: {OldWordId}:{OldReadingIndex} -> {NewWordCount} words",
                    request.OldWordId, request.OldReadingIndex, request.NewWords.Count);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Word split failed");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { Message = "Word split failed", Details = ex.Message });
        }
    }

    [HttpPost("remove-word")]
    public async Task<IActionResult> RemoveWord(
        [FromBody] RemoveWordRequest request,
        [FromServices] WordReplacementService wordReplacementService)
    {
        try
        {
            var result = await wordReplacementService.RemoveAsync(
                request.WordId, request.ReadingIndex,
                request.DryRun);

            if (request.DryRun)
            {
                logger.LogInformation(
                    "Admin dry-run word removal: {WordId}:{ReadingIndex}",
                    request.WordId, request.ReadingIndex);
            }
            else
            {
                logger.LogWarning(
                    "Admin executed word removal: {WordId}:{ReadingIndex}",
                    request.WordId, request.ReadingIndex);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Word removal failed");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { Message = "Word removal failed", Details = ex.Message });
        }
    }

    /// <summary>
    /// Validates that a URL is safe for external requests (SSRF protection).
    /// Blocks IP addresses (decimal, hex, octal), localhost, and internal networks.
    /// </summary>
    private static bool IsValidExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host;

        // Block localhost variants
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1") ||
            host.Equals("[::1]") ||
            host.Equals("0.0.0.0"))
            return false;

        // Try to parse as IP address (catches decimal, hex, octal formats)
        if (IPAddress.TryParse(host, out var ip))
        {
            // Block all IP addresses - only allow domain names
            return false;
        }

        // Block hosts that look like IPs (e.g., "2130706433" decimal format)
        if (long.TryParse(host, out _))
            return false;

        // Block hex IP format (0x7f000001)
        if (host.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(host[2..], System.Globalization.NumberStyles.HexNumber, null, out _))
            return false;

        return true;
    }

    [HttpPost("difficulty-votes/recompute")]
    public IResult RecomputeDifficultyAdjustments()
    {
        backgroundJobs.Enqueue<DifficultyAdjustmentJob>(j => j.ComputeAllAdjustments());
        return Results.Ok(new { message = "Recompute job enqueued" });
    }

    [HttpPost("difficulty-votes/invalidate")]
    public async Task<IResult> InvalidateDifficultyVotes([FromBody] InvalidateVotesRequest request)
    {
        var query = dbContext.DifficultyVotes.Where(v => v.IsValid);

        if (request.VoteIds is { Length: > 0 })
            query = query.Where(v => request.VoteIds.Contains(v.Id));

        if (!string.IsNullOrEmpty(request.UserId))
            query = query.Where(v => v.UserId == request.UserId);

        if (request.VoteIds is not { Length: > 0 } && string.IsNullOrEmpty(request.UserId))
            return Results.BadRequest("Must specify voteIds or userId.");

        var invalidatedCount = await query.ExecuteUpdateAsync(s => s.SetProperty(v => v.IsValid, false));
        return Results.Ok(new { invalidatedCount });
    }

    public class InvalidateVotesRequest
    {
        public int[]? VoteIds { get; set; }
        public string? UserId { get; set; }
    }
}
