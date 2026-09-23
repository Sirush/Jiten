namespace Jiten.Api.Dtos;

public class FsrsParametersResponse
{
    public string Parameters { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public double DesiredRetention { get; set; }
    public int ReviewCount { get; set; }
    public int MinimumReviewsForOptimize { get; set; }

    /// <summary>FSRS major version the parameters belong to (6 or 7); the parameter count follows from it.</summary>
    public int Version { get; set; }

    public double[] DefaultParameters { get; set; } = [];
}
