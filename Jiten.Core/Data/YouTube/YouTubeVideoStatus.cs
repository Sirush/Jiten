namespace Jiten.Core.Data.YouTube;

public enum YouTubeVideoStatus
{
    /// <summary>Seen in a listing or feed, metadata and subtitles not fetched yet</summary>
    Pending = 1,
    Imported = 2,
    /// <summary>Only auto-generated or no Japanese track; rechecked while the video is under 90 days old</summary>
    NoManualSubs = 3,
    /// <summary>Failed a title filter or the density guard</summary>
    FilteredOut = 4,
    /// <summary>Manually blacklisted by an admin</summary>
    Excluded = 5,
    /// <summary>Removed or privated at the source; an imported child deck is kept</summary>
    Dead = 6,
    /// <summary>Subtitles fetched and the child deck row created with raw text; the parse job has not run yet</summary>
    Fetched = 7
}
