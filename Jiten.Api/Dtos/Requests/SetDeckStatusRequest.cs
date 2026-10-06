using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class SetDeckStatusRequest
{
    public required DeckStatus Status { get; set; }

    /// <summary>Day the change took effect in the user's calendar; defaults to today (UTC).</summary>
    public DateOnly? Date { get; set; }

    /// <summary>Records the start or finish date as unknown instead of today.</summary>
    public bool DateUnknown { get; set; }

    /// <summary>Starts a new media list entry (reading it again) instead of reopening the current one.</summary>
    public bool NewEntry { get; set; }

    /// <summary>Dropping a Completed title: the completion never happened and becomes a dropped pass, instead of staying in the history.</summary>
    public bool UndoCompletion { get; set; }
}
