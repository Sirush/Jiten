using System.Text;
using Jiten.Api.Services.ExternalMediaList;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public partial class UserController
{
    private const int MaxImportFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Reads back a media list export (CSV or JSON) and matches it against the catalogue
    /// </summary>
    [HttpPost("media-list/import/file-preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxImportFileBytes + 4096)]
    [SwaggerOperation(Summary = "Preview a media list import from an exported Jiten file")]
    public async Task<IResult> PreviewMediaListFileImport(IFormFile? file)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (file == null || file.Length == 0)
            return Results.BadRequest(new { message = "Choose a file to import." });
        if (file.Length > MaxImportFileBytes)
            return Results.BadRequest(new { message = $"File exceeds the {MaxImportFileBytes / (1024 * 1024)} MB limit." });

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            content = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        var parsed = JitenExportParser.Parse(content);
        if (parsed.Error != null)
            return Results.BadRequest(new { message = parsed.Error });
        if (parsed.Entries.Count > MaxImportApplyEntries)
            return Results.BadRequest(new { message = $"File has more than {MaxImportApplyEntries:N0} entries." });

        var byDeck = new Dictionary<int, JitenExportEntry>();
        foreach (var entry in parsed.Entries)
        {
            if (!byDeck.TryGetValue(entry.DeckId, out var existing))
            {
                byDeck[entry.DeckId] = entry;
                continue;
            }

            var strongest = StatusRank(entry.MappedStatus) > StatusRank(existing.MappedStatus) ? entry : existing;
            byDeck[entry.DeckId] = strongest with
                                   {
                                       IsFavourite = existing.IsFavourite || entry.IsFavourite,
                                       Progress = (existing.Progress ?? 0) > (entry.Progress ?? 0) ? existing.Progress : entry.Progress,
                                   };
        }

        var fileDeckIds = byDeck.Keys.ToList();

        var decks = await jitenContext.Decks
                                      .AsNoTracking()
                                      .Where(d => fileDeckIds.Contains(d.DeckId) && d.ParentDeckId == null)
                                      .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.MediaType })
                                      .ToDictionaryAsync(d => d.DeckId);

        var matchedDeckIds = decks.Keys.ToList();

        var preferences = await userContext.UserDeckPreferences
                                           .AsNoTracking()
                                           .Where(p => p.UserId == userId && matchedDeckIds.Contains(p.DeckId))
                                           .Select(p => new { p.DeckId, p.Status, p.IsIgnored, p.IsFavourite })
                                           .ToDictionaryAsync(p => p.DeckId);

        var subdeckCounts = await jitenContext.Decks
                                              .AsNoTracking()
                                              .Where(d => d.ParentDeckId != null && matchedDeckIds.Contains(d.ParentDeckId.Value))
                                              .GroupBy(d => d.ParentDeckId!.Value)
                                              .Select(g => new { ParentDeckId = g.Key, Count = g.Count() })
                                              .ToDictionaryAsync(g => g.ParentDeckId, g => g.Count);

        var matched = byDeck
                      .Where(kv => decks.ContainsKey(kv.Key))
                      .Select(kv =>
                      {
                          var deck = decks[kv.Key];
                          var pref = preferences.GetValueOrDefault(kv.Key);
                          var currentStatus = pref != null && pref.Status != DeckStatus.None ? pref.Status : (DeckStatus?)null;
                          return new
                                 {
                                     deckId = deck.DeckId,
                                     originalTitle = deck.OriginalTitle,
                                     romajiTitle = deck.RomajiTitle,
                                     englishTitle = deck.EnglishTitle,
                                     coverName = deck.CoverName,
                                     mediaType = deck.MediaType,
                                     externalStatus = kv.Value.SourceStatus,
                                     mappedStatus = kv.Value.MappedStatus,
                                     finishedAt = (DateOnly?)null,
                                     progress = kv.Value.Progress,
                                     subdeckCount = subdeckCounts.TryGetValue(deck.DeckId, out var subdecks) ? subdecks : (int?)null,
                                     currentStatus,
                                     isIgnored = pref?.IsIgnored ?? false,
                                     isFavourite = kv.Value.IsFavourite,
                                     currentFavourite = pref?.IsFavourite ?? false,
                                 };
                      })
                      .OrderBy(m => m.originalTitle)
                      .ToList();

        var unmatched = byDeck
                        .Where(kv => !decks.ContainsKey(kv.Key))
                        .Select(kv => new
                                      {
                                          title = kv.Value.Title.Length > 0 ? kv.Value.Title : $"Deck {kv.Key}",
                                          url = (string?)null,
                                          externalStatus = kv.Value.SourceStatus,
                                          mappedStatus = kv.Value.MappedStatus,
                                      })
                        .OrderBy(u => u.title)
                        .ToList();

        var conflicts = matched.Count(m => !m.isIgnored && m.currentStatus != null && m.currentStatus != m.mappedStatus);

        logger.LogInformation("Media list file import preview: UserId={UserId}, Parsed={Parsed}, Matched={Matched}",
                              userId, parsed.Entries.Count, matched.Count);

        return Results.Ok(new
                          {
                              fileName = FileLabel(file.FileName),
                              matched,
                              unmatched,
                              counts = new { total = parsed.Entries.Count, matched = matched.Count, unmatched = unmatched.Count, conflicts },
                          });
    }

    private static string FileLabel(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        return name.Length > 80 ? name[..80] : name;
    }
}
