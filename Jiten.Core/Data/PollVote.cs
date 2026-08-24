namespace Jiten.Core.Data;

public class PollVote
{
    /// <summary>Denormalised from the option so per-poll queries and the one-ballot key stay single-table.</summary>
    public int PollId { get; set; }

    public int OptionId { get; set; }
    public required string UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
