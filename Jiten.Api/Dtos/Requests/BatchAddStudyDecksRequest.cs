using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class BatchAddStudyDecksRequest
{
    /// <summary>Media deck ids in the order they should be studied; the resulting SortOrder follows this order.</summary>
    public List<int> DeckIds { get; set; } = new();

    [Range(1, 6)]
    public int DownloadType { get; set; } = (int)Dtos.DeckDownloadType.OccurrenceCount;

    [Range(1, int.MaxValue)]
    public int MinOccurrences { get; set; } = 1;

    /// <summary>Turns every study deck outside the plan inactive, so the plan alone feeds new cards. Plan decks already in the list stay active.</summary>
    public bool DeactivateOthers { get; set; }

    /// <summary>Moves the plan's decks (added and already present) to the top of the study list, in plan order.</summary>
    public bool AddToTop { get; set; }
}
