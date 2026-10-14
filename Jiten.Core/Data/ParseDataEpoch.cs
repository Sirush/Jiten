namespace Jiten.Core.Data;

/// <summary>Single row (Id 1) counting dictionary changes that invalidate client-saved parse results.</summary>
public class ParseDataEpoch
{
    public const int SingletonId = 1;

    public int Id { get; set; }
    public int Epoch { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
