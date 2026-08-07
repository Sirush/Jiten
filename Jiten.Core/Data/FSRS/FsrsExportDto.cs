namespace Jiten.Core.Data.FSRS;

public class FsrsExportDto
{
    public required DateTime ExportDate { get; set; }
    public required string UserId { get; set; }
    public required int TotalCards { get; set; }
    public required int TotalReviews { get; set; }
    public List<FsrsCardExportDto> Cards { get; set; } = [];

    /// <summary>Removed cards and their kept history. Absent in backups taken before the archive existed.</summary>
    public List<FsrsCardArchiveExportDto>? Archive { get; set; }

    /// <summary>
    /// Per-day activity counters behind the heatmap and streaks. Derived data, but carried so a restore
    /// reproduces the same heatmap rather than one recomputed under a different study timezone.
    /// </summary>
    public List<UserReviewDailyExportDto>? ReviewActivity { get; set; }
}
