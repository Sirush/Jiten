using Jiten.Api.Dtos.Requests;
using System.Security.Claims;
using Jiten.Api.Services;
using Jiten.Api.Services.MetadataEdits;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    private static DeckMetadataPatch BuildMetadataPatch(int deckId, UpdateMediaRequest model) => new()
    {
        OriginalTitle = model.OriginalTitle,
        MediaType = model.MediaType,
        ReleaseDate = DeckMetadataSnapshot.FormatDate(model.ReleaseDate) ?? "",
        Aliases = model.Aliases.Any() ? model.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).ToList() : null,
        CharacterNames = model.DictionaryEntries.Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
        RomajiTitle = model.RomajiTitle ?? "",
        EnglishTitle = model.EnglishTitle ?? "",
        Description = model.Description ?? "",
        HideDialoguePercentage = model.HideDialoguePercentage,
        HideAverageSentenceLength = model.HideAverageSentenceLength,
        Genres = model.Genres,
        Tags = model.Tags
                    .Select(t => new DeckMetadataTagPatch { TagId = t.TagId, Percentage = t.Percentage })
                    .ToList(),
        Links = model.Links
                     .Select(l => new DeckMetadataLinkPatch { LinkType = l.LinkType, Url = l.Url })
                     .ToList(),
        Relationships = model.Relationships
                             .Where(r => DeckRelationship.IsPrimaryRelationship(r.RelationshipType))
                             .Select(r => new DeckMetadataRelationshipPatch
                                          {
                                              // A zero source means a legacy payload; the edited deck is the source.
                                              SourceDeckId = r.SourceDeckId == 0 ? deckId : r.SourceDeckId,
                                              TargetDeckId = r.TargetDeckId,
                                              RelationshipType = r.RelationshipType
                                          })
                             .Where(r => (r.SourceDeckId == deckId || r.TargetDeckId == deckId) &&
                                         r.SourceDeckId != r.TargetDeckId)
                             .ToList()
    };

    [HttpPatch("deck/{id:int}/metadata")]
    public async Task<IActionResult> PatchDeckMetadata(int id, [FromBody] DeckMetadataPatch patch,
                                                       [FromServices] FranchiseSyncRunner franchiseSync,
                                                       [FromServices] MetadataEditService metadataEdits, CancellationToken ct)
    {
        var deck = await deckMetadata.LoadForPatchAsync(id, ct);
        if (deck == null)
            return NotFound(new { Message = $"No deck found with ID {id}." });

        var before = (await deckMetadata.LoadSnapshotAsync(id, ct))!;
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var error = await deckMetadata.ApplyAsync(deck, patch, ct, adminId);
        if (error != null)
            return BadRequest(new { Message = error });

        var relationshipsChanged = RelationshipsChanged();
        await dbContext.SaveChangesAsync(ct);
        if (relationshipsChanged)
            await franchiseSync.RunAsync(ct);
        await metadataEdits.LogDirectEditAsync(id, before, adminId, ct);

        logger.LogInformation("Admin patched deck metadata: DeckId={DeckId}", id);

        return Ok(await deckMetadata.BuildResultAsync(id, ct));
    }

    private bool RelationshipsChanged() =>
        dbContext.ChangeTracker.Entries<DeckRelationship>().Any(e => e.State is EntityState.Added or EntityState.Deleted);
}
