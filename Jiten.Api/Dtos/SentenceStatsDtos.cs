using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class DeckSentenceStatsDto
{
    /// <summary>False until the deck (or any of its parts) has a sentence profile.</summary>
    public bool HasData { get; set; }

    public int Total { get; set; }
    public int Readable { get; set; }
    public int OneUnknown { get; set; }
    public int TwoUnknown { get; set; }
    public int ThreeOrMoreUnknown { get; set; }

    /// <summary>Distinct unknown content words across the work's sentences.</summary>
    public int UnknownWords { get; set; }

    /// <summary>Parts with a profile out of all parts; below TotalParts while a backfill is still running.</summary>
    public int ProfiledParts { get; set; }

    public int TotalParts { get; set; }

    /// <summary>True when the segments are the deck's children (episodes, volumes); false when they are slices of one text.</summary>
    public bool SegmentsArePart { get; set; }

    public List<SentenceSegmentDto> Segments { get; set; } = [];
    public List<SentenceLearnStepDto> LearnNext { get; set; } = [];
    public List<SentenceProjectionPoint> Projection { get; set; } = [];
    public List<SentenceMilestone> Milestones { get; set; } = [];
}

public class SentenceSegmentDto
{
    public int Index { get; set; }

    /// <summary>1-based positions of the first and last part in the segment; both 0 for slices of one text.</summary>
    public int FirstPart { get; set; }

    public int LastPart { get; set; }

    /// <summary>Title of the part when the segment holds exactly one.</summary>
    public string? Title { get; set; }

    public int Total { get; set; }
    public int Readable { get; set; }
    public int OneUnknown { get; set; }
    public int TwoUnknown { get; set; }
}

public class SentenceLearnStepDto
{
    public required WordSummaryDto Word { get; set; }
    public int Unlocked { get; set; }
    public int ReadableAfter { get; set; }
}

public class DeckIPlusOneSentenceDto
{
    public required ExampleSentenceDto Sentence { get; set; }

    /// <summary>The one word in the sentence the user doesn't know yet.</summary>
    public required WordSummaryDto Word { get; set; }

    /// <summary>How many of the title's i+1 sentences hinge on this same word.</summary>
    public int WordSentences { get; set; }
}

public class DeckIPlusOneSentencesResponse
{
    public List<DeckIPlusOneSentenceDto> Sentences { get; set; } = [];
    public int Total { get; set; }

    /// <summary>Example sentences checked; the deck's full text holds more sentences than these.</summary>
    public int Checked { get; set; }
}
