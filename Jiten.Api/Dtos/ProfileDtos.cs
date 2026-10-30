using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class ProfileHeaderDto
{
    /// <summary>Null only on the owner's own profile before a display name was chosen.</summary>
    public string? DisplayName { get; set; }

    public bool IsOwner { get; set; }

    /// <summary>The owner is previewing the profile as this audience, a camelCase <see cref="ProfileViewer"/> name; <see cref="IsOwner"/> is false meanwhile.</summary>
    public string? PreviewAs { get; set; }

    public bool IsJitenPlus { get; set; }
    public string? Bio { get; set; }
    public List<string> Links { get; set; } = [];
    public string? AvatarKanji { get; set; }
    public string? AvatarColour { get; set; }

    /// <summary>One of <see cref="UserProfile.AvatarStyles"/>.</summary>
    public string AvatarStyle { get; set; } = UserProfile.DefaultAvatarStyle;

    /// <summary>KanjiVG paths of the avatar character for the stroke style; null when that character has none, which draws as filled.</summary>
    public List<string>? AvatarStrokes { get; set; }

    public string? HeaderColour { get; set; }
    public DateTime? MemberSince { get; set; }

    /// <summary>Sections this viewer may open, camelCase names of <see cref="ProfileSection"/>.</summary>
    public List<string> Sections { get; set; } = [];

    /// <summary>The Lists tab has something to show: public lists written, or favourited ones the viewer may see.</summary>
    public bool HasLists { get; set; }

    /// <summary>Owner only.</summary>
    public ProfilePrivacyNames? Privacy { get; set; }
}

public class ProfileOverviewDto
{
    public ProfileSnapshotDto Snapshot { get; set; } = new();
    public List<ProfileDeckDto>? CurrentlyReading { get; set; }
    public List<ProfileDeckDto>? Pinned { get; set; }
    public List<ProfileGenreShareDto>? Genres { get; set; }
    public List<ProfileFavouriteWordDto>? FavouriteWords { get; set; }
    public List<ProfileMilestoneDto>? Milestones { get; set; }

    /// <summary>The rolling year of study activity, as the study-heatmap endpoint returns it; null when study activity is hidden from this viewer.</summary>
    public StudyHeatmapResponse? Heatmap { get; set; }
}

/// <summary>Each value is null when hidden from this viewer.</summary>
public class ProfileSnapshotDto
{
    public int? WordsKnown { get; set; }
    public int? KanjiKnown { get; set; }
    public long? Characters { get; set; }
    public int? TitlesFinished { get; set; }
}

public class ProfileDeckDto
{
    public DeckCardDto Deck { get; set; } = new();
    public DeckStatus Status { get; set; }

    /// <summary>1.0 to 10.0; null when unscored or scores are hidden from this viewer.</summary>
    public decimal? Score { get; set; }

    /// <summary>Set on in-progress titles only.</summary>
    public Helpers.MediaListEntrySummary? ListEntry { get; set; }
}

public class ProfileGenreShareDto
{
    public Genre Genre { get; set; }
    public long Characters { get; set; }

    /// <summary>0 to 1, over the characters of the titles counted.</summary>
    public double Share { get; set; }
}

public class ProfileFavouriteWordDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Text { get; set; } = "";
    public string? Reading { get; set; }
    public string Meaning { get; set; } = "";
}

public class ProfileMilestoneDto
{
    /// <summary>characters, titles, words, kanji, reviews, streak (longest, in days), studyDays or votes (difficulty votes cast).</summary>
    public string Kind { get; set; } = "";

    public long Value { get; set; }

    /// <summary>ema, zeni, ichibu, koban, jade, crystal, lacquer or shimenawa; null before the first tier.</summary>
    public string? Tier { get; set; }

    public long? Threshold { get; set; }
    public string? NextTier { get; set; }
    public long? NextThreshold { get; set; }
}

public class ProfileStatsDto
{
    /// <summary>Lifetime finished titles, private and adult ones included, matching the overview; null when the owner hides the field.</summary>
    public int? TitlesFinished { get; set; }

    /// <summary>Finished titles left off the difficulty-over-time chart because no finish date is recorded.</summary>
    public int UndatedFinished { get; set; }

    /// <summary>Lifetime finished characters, rereads included, matching the overview; null when the owner hides the field.</summary>
    public long? Characters { get; set; }
    public decimal? MeanScore { get; set; }
    public int ScoredCount { get; set; }
    public List<ProfileLadderPointDto> Ladder { get; set; } = [];
    public List<ProfileMonthDto> CharactersByMonth { get; set; } = [];
    public List<ProfileCountDto> MediaTypes { get; set; } = [];
    public List<ProfileGenreShareDto>? Genres { get; set; }
    public List<ProfileCountDto> Difficulty { get; set; } = [];
    public List<ProfileCountDto> CoverageAtFinish { get; set; } = [];
    public List<ProfileCountDto> Scores { get; set; } = [];
    public List<ProfileCountDto> Lengths { get; set; } = [];
    public List<ProfileCountDto> ReleaseYears { get; set; } = [];

    /// <summary>Null when hidden from this viewer or before the owner's coverage was first computed.</summary>
    public ProfileReadableDto? Readable { get; set; }
}

/// <summary>How much of the catalogue the owner's known words cover, counted over main entries.</summary>
public class ProfileReadableDto
{
    public DateTime ComputedAt { get; set; }

    /// <summary>Coverage levels, in percent, that <see cref="ProfileReadableTypeDto.Mature"/> and <see cref="ProfileReadableTypeDto.WithYoung"/> count against.</summary>
    public List<int> Levels { get; set; } = [];

    /// <summary>The whole catalogue first, then each media type.</summary>
    public List<ProfileReadableTypeDto> MediaTypes { get; set; } = [];
}

public class ProfileReadableTypeDto
{
    /// <summary>Null for the whole catalogue.</summary>
    public MediaType? MediaType { get; set; }

    public int Titles { get; set; }

    /// <summary>Titles at each level or above, mature words only.</summary>
    public List<int> Mature { get; set; } = [];

    /// <summary>The same with young cards counted as known.</summary>
    public List<int> WithYoung { get; set; } = [];
}

public class ProfileLadderPointDto
{
    /// <summary>Null, like the titles, when the Library section is hidden from this viewer.</summary>
    public int? DeckId { get; set; }

    public string? Title { get; set; }
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public DateOnly FinishedOn { get; set; }
    public float Difficulty { get; set; }
}

public class ProfileMonthDto
{
    /// <summary>yyyy-MM.</summary>
    public string Month { get; set; } = "";

    public long Characters { get; set; }
}

public class ProfileCountDto
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
    public long Characters { get; set; }
}

public class UpdateProfileRequest
{
    /// <summary>Null leaves it unchanged; an empty string clears it. Same for the other string fields.</summary>
    public string? Bio { get; set; }

    public List<string>? Links { get; set; }
    public string? AvatarKanji { get; set; }
    public string? AvatarColour { get; set; }

    /// <summary>One of <see cref="UserProfile.AvatarStyles"/>; null leaves it unchanged.</summary>
    public string? AvatarStyle { get; set; }

    public string? HeaderColour { get; set; }
    public ProfilePrivacyNames? Privacy { get; set; }
}

public class SetPinnedDecksRequest
{
    public List<int> DeckIds { get; set; } = [];
}

public class SetFavouriteWordsRequest
{
    public List<FavouriteWordKey> Words { get; set; } = [];
}

public record FavouriteWordKey(int WordId, byte ReadingIndex);

public class UpdateDeckDetailsRequest
{
    /// <summary>10 to 100; ignored unless <see cref="SetScore"/> so null can clear it.</summary>
    public short? Score { get; set; }

    public bool SetScore { get; set; }

    /// <summary>Null leaves it unchanged; an empty string clears it.</summary>
    public string? Note { get; set; }

    public bool? IsPrivate { get; set; }
}

public class DeckDetailsDto
{
    public int DeckId { get; set; }
    public decimal? Score { get; set; }
    public string? Note { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsPinned { get; set; }

    /// <summary><paramref name="deckId"/> is the title's main entry, where details are stored.</summary>
    public static DeckDetailsDto From(int deckId, UserDeckPreference? preference, bool isPinned) => new()
    {
        DeckId = deckId, Score = UserDeckPreference.ToDisplayScore(preference?.Score), Note = preference?.Note,
        IsPrivate = preference?.IsPrivate ?? false, IsPinned = isPinned
    };
}
