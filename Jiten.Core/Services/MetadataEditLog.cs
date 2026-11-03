using System.Text.Json;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;

namespace Jiten.Core.Services;

public static class MetadataEditLog
{
    /// <summary>History row for changes already written; a person's row counts as reviewed by them, a provider row is never reviewed.</summary>
    public static MetadataEdit Applied(int deckId, IReadOnlyCollection<MetadataFieldChange> changes, string? userId, LinkType? provider = null,
                                       DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var byPerson = provider == null;
        return new MetadataEdit
        {
            DeckId = deckId,
            Actor = byPerson ? MetadataEditActor.User : MetadataEditActor.Provider,
            Provider = provider,
            UserId = userId,
            SubmissionId = Guid.NewGuid(),
            Status = MetadataEditStatus.Approved,
            Fields = MetadataFields.Of(changes),
            ChangesJson = JsonSerializer.Serialize(changes, DeckMetadataSnapshot.Json),
            CreatedAt = at,
            AppliedAt = at,
            ReviewedAt = byPerson ? at : null,
            ReviewedByUserId = byPerson ? userId : null
        };
    }

    /// <summary>History row for a provider refetch, or null when nothing community-editable changed; provider rows never lock fields.</summary>
    public static MetadataEdit? ProviderEdit(int deckId, LinkType provider, DeckMetadataSnapshot before, DeckMetadataSnapshot after, DateTime? now = null)
    {
        var changes = DeckMetadataSnapshot.Diff(before, after);
        return changes.Count == 0 ? null : Applied(deckId, changes, null, provider, now);
    }
}
