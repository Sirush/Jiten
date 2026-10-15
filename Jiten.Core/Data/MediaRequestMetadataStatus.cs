namespace Jiten.Core.Data;

public enum MediaRequestMetadataStatus
{
    /// <summary>The request has no external link.</summary>
    None = 0,
    Pending = 1,
    Fetched = 2,
    Failed = 3,
    /// <summary>The link points at a site no metadata provider reads.</summary>
    Unsupported = 4
}
