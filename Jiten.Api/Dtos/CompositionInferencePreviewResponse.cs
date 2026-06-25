namespace Jiten.Api.Dtos;

public class CompositionInferencePreviewResponse(
    List<MassActionCardDto> data, int totalItems, int pageSize, int currentOffset,
    int newCount, int learningCount, int matureCount)
    : PaginatedResponse<List<MassActionCardDto>>(data, totalItems, pageSize, currentOffset)
{
    /// <summary>Total never-studied words available for this direction (ignores the active filter).</summary>
    public int NewCount { get; set; } = newCount;

    /// <summary>Total in-progress words (Learning/Relearning/young Review/Suspended) available for this direction.</summary>
    public int LearningCount { get; set; } = learningCount;

    /// <summary>Total mature words (Review with interval ≥ 21 days) available for this direction.</summary>
    public int MatureCount { get; set; } = matureCount;
}
