namespace Jiten.Api.Dtos;

public class FsrsParametersResponse
{
    public string Parameters { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public double DesiredRetention { get; set; }
    public int ReviewCount { get; set; }
    public int MinimumReviewsForOptimize { get; set; }
}
