using System.Text.Json.Serialization;
using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class AddMediaListEntryRequest
{
    /// <summary>InProgress starts a new pass through the title; Completed or Dropped records a past one.</summary>
    public MediaListEntryState State { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? FinishedOn { get; set; }
    public int? CharactersRead { get; set; }
}

/// <summary>Only the fields present in the body change; an explicit null clears one.</summary>
public class UpdateMediaListEntryRequest
{
    private DateOnly? _startedOn;
    private DateOnly? _finishedOn;
    private int? _charactersRead;

    public DateOnly? StartedOn
    {
        get => _startedOn;
        set { _startedOn = value; HasStartedOn = true; }
    }

    public DateOnly? FinishedOn
    {
        get => _finishedOn;
        set { _finishedOn = value; HasFinishedOn = true; }
    }

    public int? CharactersRead
    {
        get => _charactersRead;
        set { _charactersRead = value; HasCharactersRead = true; }
    }

    [JsonIgnore] public bool HasStartedOn { get; private set; }
    [JsonIgnore] public bool HasFinishedOn { get; private set; }
    [JsonIgnore] public bool HasCharactersRead { get; private set; }
}

public class SetCompletedUnitsRequest
{
    /// <summary>How many volumes or episodes of the series are completed.</summary>
    public int Completed { get; set; }

    /// <summary>Day in the user's calendar for the units it completes or reopens; defaults to today (UTC).</summary>
    public DateOnly? Date { get; set; }
}

/// <summary>A title's status and full history as the caller saw it; the mutating endpoints return one taken before the change, for Undo.</summary>
public class MediaListSnapshot
{
    public int DeckId { get; set; }
    public DeckStatus Status { get; set; }
    public List<MediaListSnapshotEntry> Entries { get; set; } = [];
}

public class MediaListSnapshotEntry
{
    /// <summary>Null, or an id that no longer exists, recreates the entry.</summary>
    public long? Id { get; set; }

    public MediaListEntryState State { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? FinishedOn { get; set; }
    public int? CharactersRead { get; set; }
    public bool IsCurrent { get; set; }

    /// <summary>On a volume, the series read-through this read belongs to; null when it isn't part of one.</summary>
    public long? SeriesEntryId { get; set; }
}

public class RestoreMediaListRequest
{
    public List<MediaListSnapshot> Decks { get; set; } = [];
}
