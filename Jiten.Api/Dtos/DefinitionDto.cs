namespace Jiten.Api.Dtos;

public class DefinitionDto
{
    public int Index { get; set; }
    public List<string> Meanings { get; set; } = new();
    public List<string> PartsOfSpeech { get; set; } = new();
}