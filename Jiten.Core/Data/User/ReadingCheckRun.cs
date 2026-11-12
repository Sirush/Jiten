namespace Jiten.Core.Data.User;

/// <summary>One confirmed reading check with every answer, kept to refit the check's slopes and tune its levels from real readers.</summary>
public class ReadingCheckRun
{
    public long Id { get; set; }
    public string UserId { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public int Units { get; set; }
    public int Candidates { get; set; }
    public int Level { get; set; }
    public int Seeded { get; set; }

    /// <summary>Fitted intercept, then the occurrence and rank slopes in force, of P(known) = σ(a + b·ln(occ/20) + c·ln(rank/20000)).</summary>
    public double A { get; set; }
    public double B { get; set; }
    public double C { get; set; }

    public Guid? BatchId { get; set; }

    /// <summary>JSON array of [wordId, readingIndex, occurrences, rank, works, answer, final check 0/1] per answered word; answer 0 knew, 1 partly, 2 didn't know.</summary>
    public string AnswersJson { get; set; } = "[]";
}
