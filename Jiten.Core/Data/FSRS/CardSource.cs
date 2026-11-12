namespace Jiten.Core.Data.FSRS;

/// <summary>How a card came into the collection, written once at insert. Append-only, never renumber (persisted as byte).</summary>
public enum CardSource : byte
{
    /// <summary>Rows created before provenance was recorded.</summary>
    Unknown = 0,
    Review = 1,
    /// <summary>One word at a time: word page, review quick action, cards page.</summary>
    ManualSingle = 2,
    DeckLearn = 3,
    FrequencyImport = 4,
    AnkiImport = 5,
    JpdbImport = 6,
    WaniKaniImport = 7,
    WordListImport = 8,
    BackupRestore = 9,
    CompositionInference = 10,
    PlacementTest = 11,
    ImportExtrapolation = 12,
    KanaRedundancy = 13,
    MassAction = 14,
    WordSet = 15,
    ReadingCalibration = 16,
}
