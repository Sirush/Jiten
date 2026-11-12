using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Core.Data.FSRS;

/// <summary>One bulk write of cards, the unit an import is undone by. Cards point back through <see cref="FsrsCard.SourceBatchId"/>.</summary>
public class CardBatch
{
    public Guid BatchId { get; set; }
    public string UserId { get; set; } = default!;
    public CardSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Cards the batch inserted; cards it skipped because the user already had them are not counted.</summary>
    public int CardCount { get; set; }

    public CardBatchDetails Details { get; set; } = new();

    /// <summary>The state every card was written with, or null when the batch wrote mixed states. Undo keeps cards no longer in it.</summary>
    public FsrsState? CardState { get; set; }

    public bool Undone { get; set; }

    public void Stamp(IEnumerable<FsrsCard> cards)
    {
        foreach (var card in cards)
        {
            card.Source = Source;
            card.SourceBatchId = BatchId;
        }
    }
}

/// <summary>What a batch added, kept as data so the client words it and localises deck titles.</summary>
public sealed record CardBatchDetails
{
    public const int MaxNameLength = 200;

    public CardBatchKind Kind { get; init; }
    public int? DeckId { get; init; }

    /// <summary>Text the user supplied: a file name, an Anki or study deck name, or a profile name.</summary>
    public string? Name { get; init; }

    public int? MinRank { get; init; }
    public int? MaxRank { get; init; }

    /// <summary>Volumes and works a reading check covered.</summary>
    public int? Units { get; init; }

    public DeckStatus? ListStatus { get; init; }

    public static CardBatchDetails Of(CardBatchKind kind, string? name = null)
        => new() { Kind = kind, Name = NormaliseName(name) };

    /// <summary>Drops values a backup file may carry that the client cannot render.</summary>
    public CardBatchDetails Sanitised()
        => this with
           {
               Kind = Enum.IsDefined(Kind) ? Kind : CardBatchKind.Other,
               Name = NormaliseName(Name),
               ListStatus = ListStatus is { } status && Enum.IsDefined(status) ? status : null
           };

    private static string? NormaliseName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }
}

/// <summary>Append-only, never renumber (persisted inside <see cref="CardBatch.Details"/>).</summary>
public enum CardBatchKind : byte
{
    Other = 0,
    JpdbKnownWords = 1,
    JpdbReviewHistory = 2,
    WordList = 3,
    WordIds = 4,
    AnkiFile = 5,
    AnkiConnect = 6,
    CompoundsOfKnown = 7,
    PartsOfKnown = 8,
    FrequencyRange = 9,
    MediaDeck = 10,
    StudyDeck = 11,
    MediaList = 12,
    BackupRestore = 13,
    WordCheck = 14,
    ReadingCheck = 15,
}

public static class CardBatchKinds
{
    public static CardSource Source(this CardBatchKind kind) => kind switch
    {
        CardBatchKind.JpdbKnownWords or CardBatchKind.JpdbReviewHistory => CardSource.JpdbImport,
        CardBatchKind.WordList => CardSource.WordListImport,
        CardBatchKind.AnkiFile or CardBatchKind.AnkiConnect => CardSource.AnkiImport,
        CardBatchKind.CompoundsOfKnown or CardBatchKind.PartsOfKnown => CardSource.CompositionInference,
        CardBatchKind.FrequencyRange => CardSource.FrequencyImport,
        CardBatchKind.MediaDeck or CardBatchKind.StudyDeck or CardBatchKind.MediaList => CardSource.DeckLearn,
        CardBatchKind.BackupRestore => CardSource.BackupRestore,
        CardBatchKind.WordCheck => CardSource.PlacementTest,
        CardBatchKind.ReadingCheck => CardSource.ReadingCalibration,
        _ => CardSource.Unknown
    };
}

public static class CardBatchDetailsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Write(CardBatchDetails details) => JsonSerializer.Serialize(details, Options);

    public static CardBatchDetails Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CardBatchDetails>(json, Options) ?? new CardBatchDetails();
        }
        catch (JsonException)
        {
            return new CardBatchDetails();
        }
    }
}
