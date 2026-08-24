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
}

/// <summary>Progress is the number of finished units on the source list; the server resolves which subdecks that covers.</summary>
public class MediaListImportEntry
{
    public int DeckId { get; set; }
    public DeckStatus Status { get; set; }
    public int? Progress { get; set; }
    public bool OverwriteSubdecks { get; set; }

    public bool IsFavourite { get; set; }
}

/// <summary>Exactly one operation per call: Status, IsFavourite, or Remove.</summary>
public class BulkDeckPreferencesRequest
{
    public List<int> DeckIds { get; set; } = new();
    public DeckStatus? Status { get; set; }
    public bool? IsFavourite { get; set; }
    public bool Remove { get; set; }
}
