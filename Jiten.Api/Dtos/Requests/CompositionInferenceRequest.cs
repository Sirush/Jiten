namespace Jiten.Api.Dtos.Requests;

public class CompositionInferenceRequest
{
    public required string Direction { get; set; }

    public int Offset { get; set; }
    public int Limit { get; set; } = 50;
}
