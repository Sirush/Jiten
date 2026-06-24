namespace Jiten.Api.Dtos.Requests;

public class CompositionInferenceExecuteRequest
{
    public required string Direction { get; set; }
    public required string TargetState { get; set; }
    public List<WordKey>? WordKeys { get; set; }

    public bool ShowNew { get; set; } = true;
    public bool ShowLearning { get; set; } = false;
    public bool ShowMature { get; set; } = false;

    public class WordKey
    {
        public int WordId { get; set; }
        public byte ReadingIndex { get; set; }
    }
}
