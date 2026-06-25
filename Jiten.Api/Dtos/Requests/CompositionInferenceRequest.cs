namespace Jiten.Api.Dtos.Requests;

public class CompositionInferenceRequest
{
    public required string Direction { get; set; }

    public int Offset { get; set; }
    public int Limit { get; set; } = 50;

    public bool ShowNew { get; set; } = true;
    public bool ShowLearning { get; set; } = false;
    public bool ShowMature { get; set; } = false;
}
