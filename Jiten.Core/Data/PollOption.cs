namespace Jiten.Core.Data;

public class PollOption
{
    public int Id { get; set; }
    public int PollId { get; set; }
    public required string Text { get; set; }
    public int SortOrder { get; set; }

    public Poll? Poll { get; set; }
}
