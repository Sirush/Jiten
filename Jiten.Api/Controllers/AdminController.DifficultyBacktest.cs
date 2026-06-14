using Jiten.Api.Jobs;
using Jiten.Core.Difficulty;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    /// <summary>
    /// Read-only dry run of the difficulty adjustment model. Recomputes every deck's adjustment with the
    /// current calculator, diffs it against the stored value, and returns the biggest movers, the decks that
    /// were saturated against the old dynamic cap, and any spotlighted decks. Writes nothing.
    /// </summary>
    [HttpGet("difficulty-backtest")]
    public async Task<IResult> DifficultyBacktest(
        [FromQuery] int top = 30,
        [FromQuery] string? spotlight = null)
    {
        top = Math.Clamp(top, 1, 200);

        var (decks, votes, ratings, users) = await DifficultyAdjustmentJob.LoadInputsAsync(dbContext, userContext);
        var results = DifficultyAdjustmentCalculator.Compute(decks, votes, ratings, users, DateTime.UtcNow);

        var deckIds = results.Select(r => r.DeckId).ToHashSet();

        var stored = await dbContext.DeckDifficulties.AsNoTracking()
            .Where(dd => deckIds.Contains(dd.DeckId))
            .Select(dd => new { dd.DeckId, dd.Difficulty, dd.UserAdjustment, dd.NEffective })
            .ToDictionaryAsync(x => x.DeckId);

        var titles = await dbContext.Decks.AsNoTracking()
            .Where(d => deckIds.Contains(d.DeckId))
            .Select(d => new { d.DeckId, d.OriginalTitle, d.EnglishTitle, d.MediaType })
            .ToDictionaryAsync(x => x.DeckId);

        var rows = results.Select(r =>
        {
            var s = stored.GetValueOrDefault(r.DeckId);
            var ml = s?.Difficulty ?? 0m;
            var oldAdj = s?.UserAdjustment ?? 0m;
            var oldNeff = s?.NEffective ?? 0m;
            var t = titles.GetValueOrDefault(r.DeckId);
            var oldCap = OldDynamicCap(oldNeff);
            return new BacktestRow
            {
                DeckId = r.DeckId,
                Title = t?.EnglishTitle ?? t?.OriginalTitle ?? $"#{r.DeckId}",
                Ml = ml,
                OldAdj = oldAdj,
                NewAdj = r.Adjustment,
                Delta = Math.Round(r.Adjustment - oldAdj, 2),
                OldFinal = Math.Round(ml + oldAdj, 2),
                NewFinal = Math.Round(ml + r.Adjustment, 2),
                Balance = r.Balance,
                Confidence = r.Confidence,
                RawAdj = r.RawAdjustment,
                NewNeff = r.Neff,
                OldNeff = oldNeff,
                Easier = r.EasierVoteCount,
                Harder = r.HarderVoteCount,
                Voters = r.DistinctVoterCount,
                OldSaturation = oldCap > 0 ? Math.Round(Math.Abs(oldAdj) / oldCap, 2) : 0m
            };
        }).ToList();

        var spotlightIds = (spotlight ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var v) ? v : (int?)null)
            .Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();

        var changed = rows.Where(r => Math.Abs(r.Delta) >= 0.05m).ToList();

        var summary = new
        {
            TotalDecks = rows.Count,
            Changed = changed.Count,
            MeanAbsDelta = rows.Count > 0 ? Math.Round(rows.Average(r => Math.Abs(r.Delta)), 3) : 0m,
            MaxIncrease = rows.Count > 0 ? rows.Max(r => r.Delta) : 0m,
            MaxDecrease = rows.Count > 0 ? rows.Min(r => r.Delta) : 0m,
            WasSaturated = rows.Count(r => r.OldSaturation >= 0.8m),
            SaturatedNowRelaxed = rows.Count(r => r.OldSaturation >= 0.8m && Math.Abs(r.RawAdj) < 0.8m * 2.0m)
        };

        return Results.Ok(new
        {
            summary,
            topMoversDown = rows.OrderBy(r => r.Delta).Take(top),
            topMoversUp = rows.OrderByDescending(r => r.Delta).Take(top),
            wasSaturated = rows.Where(r => r.OldSaturation >= 0.8m).OrderBy(r => r.Delta).Take(top),
            spotlight = rows.Where(r => spotlightIds.Contains(r.DeckId)).OrderBy(r => r.DeckId)
        });
    }

    private class BacktestRow
    {
        public int DeckId { get; init; }
        public string Title { get; init; } = "";
        public decimal Ml { get; init; }
        public decimal OldAdj { get; init; }
        public decimal NewAdj { get; init; }
        public decimal Delta { get; init; }
        public decimal OldFinal { get; init; }
        public decimal NewFinal { get; init; }
        public decimal Balance { get; init; }
        public decimal Confidence { get; init; }
        public decimal RawAdj { get; init; }
        public decimal NewNeff { get; init; }
        public decimal OldNeff { get; init; }
        public int Easier { get; init; }
        public int Harder { get; init; }
        public int Voters { get; init; }
        public decimal OldSaturation { get; init; }
    }

    // Replica of the retired dynamic cap, kept only to flag which decks were cap-saturated under the old model.
    private static decimal OldDynamicCap(decimal neff)
    {
        const decimal t1 = 15m, t2 = 30m, t3 = 50m, baseCap = 1.0m;
        if (neff < t1) return baseCap;
        if (neff < t2) return baseCap + 0.5m * (neff - t1) / (t2 - t1);
        if (neff < t3) return 1.0m + 0.5m * (neff - t2) / (t3 - t2);
        return 1.5m + 0.5m * Math.Min(1m, (neff - t3) / 50m);
    }
}
