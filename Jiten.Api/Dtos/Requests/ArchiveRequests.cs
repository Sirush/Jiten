using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Dtos.Requests;

public class ArchiveFormRef
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
}

public class RestoreArchivedCardsRequest
{
    public List<ArchiveFormRef> Forms { get; set; } = [];

    /// <summary>Restores every archived form instead of <see cref="Forms"/>, optionally narrowed by <see cref="Reason"/>.</summary>
    public bool All { get; set; }
    public CardArchiveReason? Reason { get; set; }
}

public class ForgetArchivedCardsRequest
{
    /// <summary>Empty means every archived form, optionally narrowed by <see cref="Reason"/>.</summary>
    public List<ArchiveFormRef> Forms { get; set; } = [];
    public CardArchiveReason? Reason { get; set; }
}
