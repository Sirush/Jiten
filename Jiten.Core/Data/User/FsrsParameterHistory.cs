namespace Jiten.Core.Data.User;

/// <summary>One stored parameter set per change, newest last; a restore stamps the rows it undoes.</summary>
public class FsrsParameterHistory
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "[]";
    public int Version { get; set; }
    public FsrsParametersSource Source { get; set; }

    /// <summary>Log loss of an automatic refit on the reviews it was fitted to; null for manual changes.</summary>
    public double? Loss { get; set; }

    /// <summary>Log loss of the set it replaced, scored on the same reviews as <see cref="Loss"/>.</summary>
    public double? PreviousLoss { get; set; }

    public int ReviewCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevertedAtUtc { get; set; }
}
