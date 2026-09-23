namespace Jiten.Api.Dtos.Requests;

public class ReschedulePreviewRequest
{
    /// <summary>Retentions to project the due count at; the current one is always included.</summary>
    public double[] DesiredRetentions { get; set; } = [];
}

public class ApplyFsrsSettingsRequest
{
    /// <summary>Null keeps the stored parameters.</summary>
    public double[]? Parameters { get; set; }

    public double DesiredRetention { get; set; }
    public bool Reschedule { get; set; }
}
