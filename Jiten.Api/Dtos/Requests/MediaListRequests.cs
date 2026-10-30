using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class MediaListImportPreviewRequest
{
    public string Provider { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
}

public class MediaListImportApplyRequest
{
    public List<MediaListImportEntry> Entries { get; set; } = new();
    public bool OverwriteExisting { get; set; }

    /// <summary>The cutoff the source list was filtered by; limits completions added to titles already on the list to passes after it.</summary>
    public DateOnly? CompletionsSince { get; set; }
}

public class MediaListDatesImportRequest
{
    public List<MediaListDatesImportEntry> Entries { get; set; } = new();
}

public class MediaListDatesImportEntry
{
    public int DeckId { get; set; }
    public DeckStatus Status { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? FinishedOn { get; set; }
}

/// <summary>Progress is the number of finished units on the source list; the server resolves which subdecks that covers.</summary>
public class MediaListImportEntry
{
    public int DeckId { get; set; }
    public DeckStatus Status { get; set; }
    public int? Progress { get; set; }
    public bool OverwriteSubdecks { get; set; }

    public bool IsFavourite { get; set; }

    /// <summary>10 to 100 from the source; only fills a title that has no score yet.</summary>
    public short? Score { get; set; }

    /// <summary>Private note from a Jiten export; only fills a title that has no note yet.</summary>
    public string? Note { get; set; }

    /// <summary>Reading history from the source; a title that already had history only gets its missing completions and an empty character count filled.</summary>
    public DateOnly? StartedOn { get; set; }
    public DateOnly? FinishedOn { get; set; }
    public int? CharactersRead { get; set; }

    /// <summary>Completions before the entry the status refers to, from the source's repeat count.</summary>
    public int? RepeatCount { get; set; }

    /// <summary>Full history from a Jiten JSON export; replaces the single-entry fields when present.</summary>
    public List<ImportedMediaListEntry>? History { get; set; }

    /// <summary>Volumes from a Jiten JSON export with their own status and history; replaces Progress when present.</summary>
    public List<ImportedVolume>? Volumes { get; set; }
}

public class ImportedVolume
{
    public int DeckId { get; set; }
    public DeckStatus Status { get; set; }
    public List<ImportedMediaListEntry>? History { get; set; }
}

public class ImportedMediaListEntry
{
    public MediaListEntryState State { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? FinishedOn { get; set; }
    public int? CharactersRead { get; set; }

    /// <summary>The entry the exported status referred to.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>On a volume row, the position in its series' history of the series entry it was read under.</summary>
    public int? SeriesEntry { get; set; }
}

/// <summary>Exactly one operation per call: Status, IsFavourite, or Remove.</summary>
public class BulkDeckPreferencesRequest
{
    public List<int> DeckIds { get; set; } = new();
    public DeckStatus? Status { get; set; }
    public bool? IsFavourite { get; set; }

    public bool? IsPrivate { get; set; }

    public bool Remove { get; set; }
}
