namespace Jiten.Cli.NGrams;

public class NgramProcessingQueue
{
    public int QueueId { get; set; }
    public int NgramId { get; set; }
    public short Priority { get; set; } = 1;
    public ProcessingStatus Status { get; set; } = ProcessingStatus.Pending;
    public short RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    // Navigation
    public PrecomputedNgram Ngram { get; set; } = null!;
}

public enum ProcessingStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}