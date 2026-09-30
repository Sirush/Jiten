using System.Text.Json.Serialization;
using Jiten.Core.Data;
using Jiten.Core.Data.User;

namespace Jiten.Api.Dtos;

public class ExampleSentenceDto
{
    public long SentenceId { get; set; }
    public required string Text { get; set; }
    public int WordPosition { get; set; }
    public int WordLength { get; set; }
    public float Difficulty { get; set; }
    public StudyExampleSourceDto? SourceDeckParent { get; set; }
    public StudyExampleSourceDto? SourceDeck { get; set; }

    /// <summary>Set only by the authenticated study endpoint: the sentence comes from one of the caller's study decks.</summary>
    public bool FromStudyDeck { get; set; }

    /// <summary>Set only by the authenticated study endpoint, and only for cards served in a study batch: every other content word is known.</summary>
    public bool IsIPlusOne { get; set; }

    /// <summary>Unknown content words besides the target; set only by the Jiten+ readable-sentence search.</summary>
    public int? UnknownCount { get; set; }

    /// <summary>Where those unknown words sit in Text; set only by the Jiten+ readable-sentence search.</summary>
    public List<SentenceSpanDto>? UnknownSpans { get; set; }

    /// <summary>Null for sentences parsed before token spans existed.</summary>
    public List<SentenceFuriganaDto>? Furigana { get; set; }
}

public class SentenceSpanDto
{
    public int Position { get; set; }
    public int Length { get; set; }
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ReadableSentenceSort>))]
public enum ReadableSentenceSort
{
    Random,
    EasiestFirst,
    HardestFirst
}

public class ReadableSentencesRequest
{
    /// <summary>Exact number of unknown words besides the target: 0 is i+1, 2 is i+3.</summary>
    public int Unknown { get; set; }

    /// <summary>Empty searches every media type.</summary>
    public List<MediaType> MediaTypes { get; set; } = [];

    /// <summary>Only media the caller marked with one of these statuses, with their subdecks; empty searches all media.</summary>
    public List<DeckStatus> Statuses { get; set; } = [];

    public ReadableSentenceSort Sort { get; set; }

    /// <summary>Picks which slice of the word's sentences a search walks first; sent unchanged with every page of one search.</summary>
    public int Seed { get; set; }

    public ReadableSentencesCursor? Cursor { get; set; }
}

public class ReadableSentencesCursor
{
    public int Bucket { get; set; }
    public int Skip { get; set; }
}

public class ReadableSentencesResponse
{
    public List<ExampleSentenceDto> Sentences { get; set; } = [];

    /// <summary>Null once every sentence holding the word has been checked.</summary>
    public ReadableSentencesCursor? Next { get; set; }
}

/// <summary>One ruby group over the sentence Text. Known is the caller's own state for the word and false when signed out.</summary>
public class SentenceFuriganaDto
{
    public int Position { get; set; }
    public int Length { get; set; }
    public string Reading { get; set; } = "";
    public int WordId { get; set; }
    public bool Known { get; set; }

    /// <summary>The caller's states for the word; null when signed out.</summary>
    public List<KnownState>? States { get; set; }
}

public class ExampleSentencesByDifficultyResponse
{
    public float MinDifficulty { get; set; }
    public float MaxDifficulty { get; set; }
    public float SearchedBandMin { get; set; }
    public float SearchedBandMax { get; set; }
    public List<ExampleSentenceDto> Sentences { get; set; } = [];
} 