using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Difficulty;

namespace Jiten.Tests;

/// <summary>
/// Pure-function tests for the difficulty adjustment model, encoding the four design invariants:
/// (A) ordinal/scale-aware magnitude, (B) bracketing confidence, (C) ridge prior + clamp, (D) robust IRLS.
/// </summary>
public class DifficultyAdjustmentCalculatorTests
{
    private int _voteId = 1;

    private DifficultyVoteInput V(string user, int low, int high, ComparisonOutcome outcome,
        DifficultyVoteSource source = DifficultyVoteSource.Manual)
        => new(_voteId++, user, low, high, outcome, source);

    private static DeckDifficultyInput D(int id, decimal ml) => new(id, MediaType.Novel, ml);

    private static UserDifficultyInput U(string id) => new(id, DateTime.UtcNow.AddDays(-90));

    /// <summary>
    /// Runs the calculator with breadth-padding so every user reaches full media weight (gating realistic).
    /// </summary>
    private Dictionary<int, DifficultyAdjustmentResult> Run(
        (int id, decimal ml)[] decks, string[] users, IEnumerable<DifficultyVoteInput> votes)
    {
        var fillerDecks = Enumerable.Range(900, 16).Select(i => D(i, 3.0m));
        var allDecks = decks.Select(d => D(d.id, d.ml)).Concat(fillerDecks).ToList();

        var allVotes = votes.ToList();
        foreach (var u in users)
            for (var i = 0; i < 15; i++)
                allVotes.Add(V(u, 900 + i, 900 + i + 1, ComparisonOutcome.Same));

        var results = DifficultyAdjustmentCalculator.Compute(
            allDecks, allVotes, Array.Empty<DifficultyRatingInput>(), users.Select(U).ToList(), DateTime.UtcNow);
        return results.ToDictionary(r => r.DeckId);
    }

    [Fact]
    public void MuchHarderThanFarEasierDeck_IsInert_CloseComparisonMovesMore()
    {
        // deck 1: "much harder" than 0.5-decks (already true → uninformative)
        // deck 2: "much harder" than 2.9-decks (a real claim → should push up)
        var users = new[] { "u1", "u2", "u3", "u4" };
        var votes = users.SelectMany(u => new[]
        {
            V(u, 1, 10, ComparisonOutcome.MuchHarder),
            V(u, 1, 11, ComparisonOutcome.MuchHarder),
            V(u, 2, 12, ComparisonOutcome.MuchHarder),
            V(u, 2, 13, ComparisonOutcome.MuchHarder)
        });

        var r = Run(new[] { (1, 3.0m), (2, 3.0m), (10, 0.5m), (11, 0.5m), (12, 2.9m), (13, 2.9m) }, users, votes);

        r[1].Adjustment.Should().BeLessThan(0.05m,
            "being 'much harder' than a far-easier deck is already satisfied and should not move it");
        r[2].Adjustment.Should().BeGreaterThan(r[1].Adjustment + 0.05m,
            "the close 'much harder' comparison is informative and should push the deck up");
    }

    [Fact]
    public void GenuinelyHardOneSidedDeck_StaysNearMl()
    {
        // A real 4.3 deck, voted harder than everything it's compared to (no upper anchor).
        // It must neither inflate further nor get shrunk toward the mean.
        var users = new[] { "u1", "u2", "u3", "u4" };
        var votes = users.SelectMany(u => new[]
        {
            V(u, 1, 2, ComparisonOutcome.MuchHarder),
            V(u, 1, 3, ComparisonOutcome.MuchHarder),
            V(u, 1, 4, ComparisonOutcome.Harder)
        });

        var r = Run(new[] { (1, 4.3m), (2, 2.6m), (3, 2.8m), (4, 3.0m) }, users, votes);

        var final = 4.3m + r[1].Adjustment;
        final.Should().BeInRange(4.0m, 4.7m,
            "a genuinely hard deck whose votes merely confirm its high ML should stay put");
        Math.Abs(r[1].Adjustment).Should().BeLessThan(0.4m);
    }

    [Fact]
    public void LoneOutlierVoter_BarelyChangesResult()
    {
        // Four calibrated voters place deck 1 (~2.5); one outlier calls it much harder than everything.
        var calibrated = new[] { "u1", "u2", "u3", "u4" };
        var decks = new[] { (1, 2.5m), (2, 2.5m), (3, 3.0m), (4, 3.5m) };

        List<DifficultyVoteInput> CalibratedVotes() => calibrated.SelectMany(u => new[]
        {
            V(u, 1, 2, ComparisonOutcome.Same),
            V(u, 1, 4, ComparisonOutcome.Easier)
        }).ToList();

        var without = Run(decks, calibrated, CalibratedVotes());

        _voteId = 1;
        var withOutlier = Run(decks, calibrated.Append("outlier").ToArray(),
            CalibratedVotes().Concat(new[]
            {
                V("outlier", 1, 2, ComparisonOutcome.MuchHarder),
                V("outlier", 1, 3, ComparisonOutcome.MuchHarder),
                V("outlier", 1, 4, ComparisonOutcome.MuchHarder)
            }));

        var finalWithout = 2.5m + without[1].Adjustment;
        var finalWith = 2.5m + withOutlier[1].Adjustment;

        // The outlier demanded "much harder" (a +0.6 margin); robustness + the same-vote anchor should
        // hold the shift to less than half of that.
        (finalWith - finalWithout).Should().BeLessThan(0.3m,
            "a single consensus-violating voter should be down-weighted by the robust pass");
        finalWith.Should().BeLessThan(2.9m, "the outlier must not drag the deck near its claimed level (3.1+)");
    }

    [Fact]
    public void FullOrdering_IsPreserved()
    {
        // Everyone agrees deck1 > deck2 > deck3 > deck4; the final difficulties must respect that order.
        var users = new[] { "u1", "u2", "u3", "u4" };
        var votes = users.SelectMany(u => new[]
        {
            V(u, 1, 2, ComparisonOutcome.Harder),
            V(u, 1, 3, ComparisonOutcome.MuchHarder),
            V(u, 1, 4, ComparisonOutcome.MuchHarder),
            V(u, 2, 3, ComparisonOutcome.Harder),
            V(u, 2, 4, ComparisonOutcome.MuchHarder),
            V(u, 3, 4, ComparisonOutcome.Harder)
        });

        var r = Run(new[] { (1, 3.0m), (2, 3.0m), (3, 3.0m), (4, 3.0m) }, users, votes);

        var f1 = 3.0m + r[1].Adjustment;
        var f2 = 3.0m + r[2].Adjustment;
        var f3 = 3.0m + r[3].Adjustment;
        var f4 = 3.0m + r[4].Adjustment;

        f1.Should().BeGreaterThan(f2);
        f2.Should().BeGreaterThan(f3);
        f3.Should().BeGreaterThan(f4);
    }

    [Fact]
    public void OneSidedConfirmingMl_StaysFlat_WhileFightingMlMoves()
    {
        // deck 1 (ML 1.5) voted easier than 2.5-3.0 decks → already true → no move.
        // deck 2 (ML 3.0) voted easier than 2.4-decks → contradicts ML (rated easier than easier decks) → moves down.
        var users = new[] { "u1", "u2", "u3", "u4" };
        var votes = users.SelectMany(u => new[]
        {
            V(u, 1, 20, ComparisonOutcome.Easier),
            V(u, 1, 21, ComparisonOutcome.Easier),
            V(u, 2, 22, ComparisonOutcome.Easier),
            V(u, 2, 23, ComparisonOutcome.Easier)
        });

        var r = Run(new[] { (1, 1.5m), (2, 3.0m), (20, 2.6m), (21, 2.8m), (22, 2.4m), (23, 2.4m) }, users, votes);

        // Soft threshold means confirming votes nudge slightly; the invariant is that the contradicting
        // deck moves substantially more than the confirming one.
        r[2].Adjustment.Should().BeLessThan(-0.15m,
            "a deck rated easier than already-easier decks should be pushed down hard");
        Math.Abs(r[1].Adjustment).Should().BeLessThan(Math.Abs(r[2].Adjustment) - 0.1m,
            "votes that merely confirm the ML ordering should move the deck far less than contradicting ones");
    }
}
