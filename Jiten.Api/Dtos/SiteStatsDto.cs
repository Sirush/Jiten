using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

/// <summary>Public /stats snapshot. Sections below their privacy floor are empty lists or null, never zeros.</summary>
public class SiteStatsDto
{
    public DateOnly Date { get; set; }
    public DateTime ComputedAt { get; set; }
    public SiteStatsTotalsDto Totals { get; set; } = new();
    public SiteStatsRecentDto Recent { get; set; } = new();
    public List<SiteStatsMonthPointDto> LearnersByMonth { get; set; } = [];
    public SiteStatsHeatmapDto? Heatmap { get; set; }
    public SiteStatsKnownWordsDto? KnownWords { get; set; }
    public List<SiteStatsFrequencyBandDto> FrequencyBands { get; set; } = [];
    public SiteStatsWordsForCoverageDto? WordsForCoverage { get; set; }
    public List<SiteStatsReadingDto> Reading { get; set; } = [];
    public List<SiteStatsStudiedDeckDto> Studying { get; set; } = [];
    public List<SiteStatsForgottenWordDto> Forgotten { get; set; } = [];
    public SiteStatsLibraryDto Library { get; set; } = new();
}

public class SiteStatsTotalsDto
{
    public int Learners { get; set; }

    /// <summary>Only review days on or after the learner's signup; older days are imported Anki history.</summary>
    public long Reviews { get; set; }

    public long WordsKnown { get; set; }
    public long HoursReviewing { get; set; }
    public long TitlesFinished { get; set; }
    public long CharactersRead { get; set; }
}

public class SiteStatsRecentDto
{
    public int PeopleReviewing { get; set; }
    public long Reviews { get; set; }
    public long NewWords { get; set; }

    /// <summary>Median reviews over (learner, day) pairs with at least one review.</summary>
    public double MedianReviewsPerStudyDay { get; set; }
}

public class SiteStatsMonthPointDto
{
    /// <summary>First day of the month.</summary>
    public DateOnly Month { get; set; }

    public long Total { get; set; }
}

public class SiteStatsHeatmapDto
{
    /// <summary>[weekday][hour] review counts in each reviewer's own study timezone; weekday 0 = Monday.</summary>
    public long[][] Reviews { get; set; } = [];

    public int Learners { get; set; }
}

public class SiteStatsKnownWordsDto
{
    public int Learners { get; set; }
    public double Median { get; set; }
    public double P75 { get; set; }

    /// <summary>Share (0-100) of learners who know at least <see cref="SiteStatsAtLeastDto.Words"/> words.</summary>
    public List<SiteStatsAtLeastDto> AtLeast { get; set; } = [];
}

public class SiteStatsAtLeastDto
{
    public int Words { get; set; }
    public double Share { get; set; }
}

public class SiteStatsFrequencyBandDto
{
    public int From { get; set; }
    public int To { get; set; }

    /// <summary>Percent of the band known, over learners; each learner's blacklisted words leave their own band.</summary>
    public double P25 { get; set; }

    public double Median { get; set; }
    public double P75 { get; set; }
    public double P90 { get; set; }
}

public class SiteStatsWordsForCoverageDto
{
    public DateTime ComputedAt { get; set; }

    /// <summary>Coverage levels in percent, parallel to each media type's <see cref="SiteStatsWordsForCoverageTypeDto.Ranks"/>.</summary>
    public List<int> Levels { get; set; } = [];

    public List<SiteStatsWordsForCoverageTypeDto> MediaTypes { get; set; } = [];
}

public class SiteStatsWordsForCoverageTypeDto
{
    public MediaType MediaType { get; set; }
    public int Titles { get; set; }

    /// <summary>Median word frequency rank to know up to; null when the level is out of reach for the median title.</summary>
    public List<int?> Ranks { get; set; } = [];

    public double ExcludedPercent { get; set; }
}

public class SiteStatsReadingDto
{
    /// <summary>Null for the "Other" row that folds media types under the learner floor.</summary>
    public MediaType? MediaType { get; set; }

    public int Learners { get; set; }
    public long Characters { get; set; }
    public long TitlesFinished { get; set; }

    /// <summary>Episodes, volumes or videos finished.</summary>
    public long UnitsFinished { get; set; }
}

public class SiteStatsStudiedDeckDto
{
    public int DeckId { get; set; }
    public MediaType MediaType { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public string CoverName { get; set; } = "nocover.jpg";
    public int Learners { get; set; }
}

public class SiteStatsForgottenWordDto
{
    public int WordId { get; set; }
    public int ReadingIndex { get; set; }
    public string Text { get; set; } = "";
    public string ReadingFurigana { get; set; } = "";
    public string? Gloss { get; set; }

    /// <summary>Percent of Review-state reviews answered Again.</summary>
    public double MissedPercent { get; set; }
}

public class SiteStatsLibraryDto
{
    public int Titles { get; set; }
    public long CharactersParsed { get; set; }
    public int AddedThisMonth { get; set; }
    public int RequestsFulfilled { get; set; }
    public int DifficultyVotes { get; set; }

    /// <summary>Running total of top-level titles at the end of each month.</summary>
    public List<SiteStatsMonthPointDto> ByMonth { get; set; } = [];
}
