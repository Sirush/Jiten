namespace Jiten.Api.Dtos;

public class ReschedulePreviewResponse
{
    public int CurrentDue { get; set; }

    /// <summary>Cards not due now that come due within <see cref="UpcomingDays"/> days after today's cutoff.</summary>
    public int CurrentUpcoming { get; set; }

    public int UpcomingDays { get; set; }
    public List<ReschedulePreviewOption> Options { get; set; } = [];
}

public class ReschedulePreviewOption
{
    public double DesiredRetention { get; set; }
    public int Due { get; set; }
    public int Upcoming { get; set; }
}

public class OptimizePreviewResponse
{
    public string Parameters { get; set; } = string.Empty;

    /// <summary>Full-precision values to send back to apply; <see cref="Parameters"/> is rounded for display.</summary>
    public double[] ParameterValues { get; set; } = [];

    public double Loss { get; set; }
    public int ReviewCount { get; set; }
    public int Version { get; set; }
    public double DesiredRetention { get; set; }
    public ReschedulePreviewResponse Preview { get; set; } = new();
}
