namespace Jiten.Api.Dtos.Requests;

public class CompositionInferenceExecuteRequest
{
    public required string Direction { get; set; }
    public required string TargetState { get; set; }
    public List<WordKey>? WordKeys { get; set; }

    public class WordKey
    {
        public int WordId { get; set; }
        public byte ReadingIndex { get; set; }
    }
}
