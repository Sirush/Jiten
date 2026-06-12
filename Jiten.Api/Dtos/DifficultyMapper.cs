using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

/// <summary>
/// Single source of truth for converting a deck's raw difficulty into the 0-5 integer band
/// surfaced to clients. The thresholds are model-specific and subject to change with a different
/// training, so they live in exactly one place.
/// </summary>
public static class DifficultyMapper
{
    /// <summary>
    /// Raw difficulty including the community vote adjustment (<see cref="DeckDifficulty.UserAdjustment"/>).
    /// </summary>
    public static float GetAdjustedDifficulty(Deck deck)
    {
        var baseDifficulty = deck.GetDifficulty();
        var adjustment = deck.DeckDifficulty?.UserAdjustment ?? 0;
        return baseDifficulty + (float)adjustment;
    }

    /// <summary>
    /// Remap the raw difficulty to a 0-5 int while taking into account the biases of the model.
    /// This is subject to change with a different training.
    /// </summary>
    public static int MapDifficulty(float difficulty)
    {
        if (difficulty < 1.01)
            return 0;

        if (difficulty < 2.01)
            return 1;

        if (difficulty < 3.01)
            return 2;

        if (difficulty < 4.01)
            return 3;

        if (difficulty < 4.95)
            return 4;

        return 5;
    }

    /// <summary>
    /// Convenience: adjusted-then-mapped 0-5 band for a deck.
    /// </summary>
    public static int MapDeck(Deck deck) => MapDifficulty(GetAdjustedDifficulty(deck));
}
