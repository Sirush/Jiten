using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Services.ReadingCheck;

public sealed class ReadingCheckWriter(ICardBatchService cardBatches)
{
    /// <summary>Marks <paramref name="known"/> as known under one undoable batch; Seeded counts the words added.</summary>
    public async Task<(int Seeded, Guid? BatchId)> SeedAsync(string userId, IReadOnlyCollection<(int WordId, byte ReadingIndex)> known, int units)
    {
        var now = DateTime.UtcNow;
        var cards = known.Select(k => new FsrsCard(userId, k.WordId, k.ReadingIndex, state: FsrsState.Mastered, due: now, lastReview: now)).ToList();

        var result = await cardBatches.SeedAsync(userId, cards, new CardBatchDetails { Kind = CardBatchKind.ReadingCheck, Units = units });
        return (result.InsertedWords, result.BatchId);
    }
}
