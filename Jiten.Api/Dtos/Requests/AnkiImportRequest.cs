using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Dtos.Requests;

public class AnkiImportRequest
{
    public List<AnkiCardWrapper> Cards { get; set; } = new();
    public bool Overwrite { get; set; } = false;
    public bool ParseWords { get; set; } = false;
}

public class AnkiCardWrapper
{
    public required AnkiCardImport Card { get; set; }
    public List<AnkiReviewLogImport> ReviewLogs { get; set; } = new();
}

public class AnkiCardImport
{
    public required string Word { get; set; }

    /// <summary>
    /// Optional reading (full kana) used to disambiguate when several dictionary words share the
    /// same surface but have different readings. Already furigana-stripped by the client.
    /// </summary>
    public string? Reading { get; set; }
    public double? Stability { get; set; }
    public double? Difficulty { get; set; }
    public int Reps { get; set; } = 0;
    public int Lapses { get; set; } = 0;
    public DateTime Due { get; set; } = DateTime.UtcNow;
    public FsrsState State { get; set; } = FsrsState.Learning;
    public DateTime? LastReview { get; set; }
}

public class AnkiReviewLogImport
{
    public FsrsRating Rating { get; set; }
    public DateTime ReviewDateTime { get; set; }
    public int? ReviewDuration { get; set; }
}
