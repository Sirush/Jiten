using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

/// <summary>
/// Only the fields a metadata patch can change. Deliberately not a <see cref="DeckDto"/>: the client
/// merges this into the deck it already holds, whose per-user fields the admin endpoint cannot produce.
/// </summary>
public sealed class DeckMetadataPatchResultDto
{
    public string OriginalTitle { get; set; } = "";
    public string RomajiTitle { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public string Description { get; set; } = "";
    public bool HideDialoguePercentage { get; set; }
    public bool HideAverageSentenceLength { get; set; }
    public List<Genre> Genres { get; set; } = new();
    public List<TagWithPercentageDto> Tags { get; set; } = new();
    public List<Link> Links { get; set; } = new();
    public List<DeckRelationshipDto> Relationships { get; set; } = new();
}
