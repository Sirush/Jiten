namespace Jiten.Api.Dtos.Requests;

public class UpdateFsrsParametersRequest
{
    public string? Parameters { get; set; }
    public double? DesiredRetention { get; set; }

    public bool? AutoOptimise { get; set; }
}
