namespace Jiten.Api.Dtos;

public class AdminWordSetMemberDto
{
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
    public int Position { get; set; }
    public string Text { get; set; } = string.Empty;
    public string RubyText { get; set; } = string.Empty;
    public List<string> Meanings { get; set; } = [];
    public List<string> PartsOfSpeech { get; set; } = [];
    public int FrequencyRank { get; set; }
    public bool IsManual { get; set; }
}
