using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/difficulty-votes")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("fixed")]
public class DifficultyVoteController(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    ILogger<DifficultyVoteController> logger) : ControllerBase
{
    private const int VotesPerMinuteLimit = 35;
    [HttpPost]
    public async Task<IResult> SubmitVote([FromBody] SubmitVoteRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!Enum.IsDefined(typeof(ComparisonOutcome), request.Outcome))
            return Results.BadRequest("Invalid outcome. Must be -2, -1, 0, 1, or 2.");

        var oneMinuteAgo = DateTimeOffset.UtcNow.AddMinutes(-1);
        var recentTimestamps = await context.DifficultyVotes
            .Where(v => v.UserId == userId && v.Source == DifficultyVoteSource.Manual)
            .OrderByDescending(v => v.Id)
            .Take(VotesPerMinuteLimit)
            .Select(v => v.CreatedAt)
            .ToListAsync();
        if (recentTimestamps.Count(t => t > oneMinuteAgo) >= VotesPerMinuteLimit)
            return Results.Problem("Rate limit exceeded. Maximum 35 votes per minute.", statusCode: 429);

        if (request.DeckAId == request.DeckBId)
            return Results.BadRequest("Cannot compare a deck with itself.");

        var decks = await context.Decks.AsNoTracking()
            .Where(d => d.DeckId == request.DeckAId || d.DeckId == request.DeckBId)
            .Select(d => new { d.DeckId, d.MediaType, d.ParentDeckId })
            .ToListAsync();
        var deckA = decks.FirstOrDefault(d => d.DeckId == request.DeckAId);
        var deckB = decks.FirstOrDefault(d => d.DeckId == request.DeckBId);

        if (deckA == null || deckB == null)
            return Results.NotFound("One or both decks not found.");

        if (deckA.ParentDeckId != null || deckB.ParentDeckId != null)
            return Results.BadRequest("Difficulty comparisons are only available for parent decks, not subdecks.");

        var completedBothDecks = (await MediaListEntryHelper.CompletedDeckIdsAsync(userContext, userId, [request.DeckAId, request.DeckBId])).Count == 2;

        if (!completedBothDecks)
            return Results.Problem("You must have completed both decks to vote.", statusCode: 403);

        if (!MediaTypeGroups.AreComparable(deckA.MediaType, deckB.MediaType))
            return Results.BadRequest("These media types cannot be compared.");

        var deckLowId = Math.Min(request.DeckAId, request.DeckBId);
        var deckHighId = Math.Max(request.DeckAId, request.DeckBId);

        var outcome = request.Outcome;
        if (request.DeckAId > request.DeckBId)
            outcome = (ComparisonOutcome)(-(int)outcome);

        var existing = await context.DifficultyVotes
            .FirstOrDefaultAsync(v => v.UserId == userId && v.DeckLowId == deckLowId && v.DeckHighId == deckHighId && v.IsValid);

        if (existing != null)
        {
            existing.Outcome = outcome;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.Source = DifficultyVoteSource.Manual;

            var existingSkipForUpdate = await context.SkippedComparisons
                .FirstOrDefaultAsync(s => s.UserId == userId && s.DeckLowId == deckLowId && s.DeckHighId == deckHighId);
            if (existingSkipForUpdate != null)
                context.SkippedComparisons.Remove(existingSkipForUpdate);

            await context.SaveChangesAsync();
            return Results.Ok(new { id = existing.Id, isUpdate = true });
        }

        var vote = new DifficultyVote
        {
            UserId = userId,
            DeckLowId = deckLowId,
            DeckHighId = deckHighId,
            Outcome = outcome,
            Source = DifficultyVoteSource.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
            IsValid = true
        };

        context.DifficultyVotes.Add(vote);

        var existingSkip = await context.SkippedComparisons
            .FirstOrDefaultAsync(s => s.UserId == userId && s.DeckLowId == deckLowId && s.DeckHighId == deckHighId);
        if (existingSkip != null)
            context.SkippedComparisons.Remove(existingSkip);

        await context.SaveChangesAsync();
        return Results.Created($"/api/difficulty-votes/{vote.Id}", new { id = vote.Id, isUpdate = false });
    }

    [HttpPost("rating")]
    public async Task<IResult> SubmitRating([FromBody] SubmitRatingRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (request.Rating is < 0 or > 4)
            return Results.BadRequest("Rating must be between 0 and 4.");

        var deckExists = await context.Decks.AnyAsync(d => d.DeckId == request.DeckId && d.ParentDeckId == null);
        if (!deckExists)
            return Results.NotFound("Deck not found or is a subdeck.");

        var hasCompleted = await MediaListEntryHelper.CompletedDeckIds(userContext, userId, [request.DeckId]).AnyAsync();
        if (!hasCompleted)
            return Results.Problem("You must have completed this deck to rate it.", statusCode: 403);

        var existing = await context.DifficultyRatings
            .FirstOrDefaultAsync(r => r.UserId == userId && r.DeckId == request.DeckId);

        if (existing != null)
        {
            existing.Rating = request.Rating;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
            return Results.Ok(new { id = existing.Id });
        }

        var rating = new DifficultyRating
        {
            UserId = userId,
            DeckId = request.DeckId,
            Rating = request.Rating,
            CreatedAt = DateTimeOffset.UtcNow
        };

        context.DifficultyRatings.Add(rating);
        await context.SaveChangesAsync();
        return Results.Ok(new { id = rating.Id });
    }

    [HttpGet("rating/{deckId:int}")]
    public async Task<IResult> GetRating(int deckId)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var rating = await context.DifficultyRatings
            .FirstOrDefaultAsync(r => r.UserId == userId && r.DeckId == deckId);
        if (rating == null)
            return Results.NotFound();

        return Results.Ok(new { rating = rating.Rating });
    }

    [HttpDelete("rating/{deckId:int}")]
    public async Task<IResult> DeleteRating(int deckId)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var rating = await context.DifficultyRatings
            .FirstOrDefaultAsync(r => r.UserId == userId && r.DeckId == deckId);
        if (rating == null)
            return Results.NotFound();

        context.DifficultyRatings.Remove(rating);
        await context.SaveChangesAsync();
        return Results.NoContent();
    }

    [HttpGet("suggestions")]
    public async Task<IResult> GetSuggestions([FromQuery] int? deckId = null, [FromQuery] int count = 10)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        count = Math.Clamp(count, 1, 10);

        var completedDeckIds = await MediaListEntryHelper.CompletedDeckIds(userContext, userId).ToListAsync();

        var parentDeckIds = await context.Decks.AsNoTracking()
            .Where(d => completedDeckIds.Contains(d.DeckId) && d.ParentDeckId == null)
            .Select(d => d.DeckId)
            .ToListAsync();
        completedDeckIds = parentDeckIds;

        if (completedDeckIds.Count < 2)
            return Results.Ok(Array.Empty<ComparisonSuggestionDto>());

        var blacklistedDeckIds = await context.BlacklistedComparisonDecks.AsNoTracking()
            .Where(b => b.UserId == userId)
            .Select(b => b.DeckId)
            .ToListAsync();
        var blacklistedSet = blacklistedDeckIds.ToHashSet();

        var decks = await context.Decks.AsNoTracking()
            .Where(d => completedDeckIds.Contains(d.DeckId))
            .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType })
            .ToListAsync();

        var deckDifficulties = await context.DeckDifficulties.AsNoTracking()
            .Where(dd => completedDeckIds.Contains(dd.DeckId))
            .ToDictionaryAsync(dd => dd.DeckId, dd => dd);

        var userVotedPairs = await context.DifficultyVotes.AsNoTracking()
            .Where(v => v.UserId == userId && v.IsValid)
            .Select(v => new { v.DeckLowId, v.DeckHighId })
            .ToListAsync();
        var votedPairSet = userVotedPairs.Select(v => (v.DeckLowId, v.DeckHighId)).ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var skippedPairs = await context.SkippedComparisons.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToListAsync();
        var skippedPairSet = skippedPairs
            .Where(s => s.Permanent || (now - s.CreatedAt).TotalDays < 7)
            .Select(s => (s.DeckLowId, s.DeckHighId))
            .ToHashSet();

        var allVoteCounts = await context.DifficultyVotes.AsNoTracking()
            .Where(v => v.IsValid
                && completedDeckIds.Contains(v.DeckLowId)
                && completedDeckIds.Contains(v.DeckHighId))
            .GroupBy(v => new { v.DeckLowId, v.DeckHighId })
            .Select(g => new { g.Key.DeckLowId, g.Key.DeckHighId, Count = g.Count() })
            .ToListAsync();
        var voteCountMap = allVoteCounts.ToDictionary(v => (v.DeckLowId, v.DeckHighId), v => v.Count);

        var deckMap = decks.ToDictionary(d => d.DeckId);
        var candidates = new List<(int deckAId, int deckBId, double score)>();

        for (int i = 0; i < decks.Count; i++)
        {
            for (int j = i + 1; j < decks.Count; j++)
            {
                var a = decks[i];
                var b = decks[j];

                var low = Math.Min(a.DeckId, b.DeckId);
                var high = Math.Max(a.DeckId, b.DeckId);

                if (blacklistedSet.Contains(a.DeckId) || blacklistedSet.Contains(b.DeckId)) continue;
                if (votedPairSet.Contains((low, high))) continue;
                if (skippedPairSet.Contains((low, high))) continue;
                if (!MediaTypeGroups.AreComparable(a.MediaType, b.MediaType)) continue;

                if (deckId.HasValue && a.DeckId != deckId.Value && b.DeckId != deckId.Value) continue;

                var mlA = deckDifficulties.TryGetValue(a.DeckId, out var ddA) ? (double)ddA.Difficulty : a.Difficulty;
                var mlB = deckDifficulties.TryGetValue(b.DeckId, out var ddB) ? (double)ddB.Difficulty : b.Difficulty;

                var closeness = 1.0 / (1.0 + Math.Abs(mlA - mlB) / 0.75);

                voteCountMap.TryGetValue((low, high), out var totalVotes);
                var coverage = 1.0 / (1.0 + Math.Sqrt(totalVotes));

                var nEffA = deckDifficulties.TryGetValue(a.DeckId, out var daA) ? (double)daA.NEffective : 0;
                var nEffB = deckDifficulties.TryGetValue(b.DeckId, out var daB) ? (double)daB.NEffective : 0;
                var uncertainty = 1.0 / (1.0 + Math.Sqrt(Math.Min(nEffA, nEffB)));

                var sameType = a.MediaType == b.MediaType;
                var typeBonus = sameType ? 1.0 : 0.7;

                var score = (0.4 * closeness + 0.3 * coverage + 0.3 * uncertainty) * typeBonus;
                candidates.Add((a.DeckId, b.DeckId, score));
            }
        }

        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        List<(int deckAId, int deckBId, double score)> topCandidates;

        if (deckId.HasValue)
        {
            var anchorType = deckMap[deckId.Value].MediaType;
            var sameType = candidates.Where(c => deckMap[c.deckAId].MediaType == anchorType && deckMap[c.deckBId].MediaType == anchorType).Take(10).ToList();
            var sameGroup = candidates.Where(c => !sameType.Contains(c)).Take(10).ToList();

            if (sameType.Count >= count)
                topCandidates = sameType;
            else
                topCandidates = sameType.Concat(sameGroup).Take(10).ToList();
        }
        else
        {
            topCandidates = candidates.Take(10).ToList();
        }

        if (topCandidates.Count == 0)
            return Results.Ok(Array.Empty<ComparisonSuggestionDto>());

        var rng = Random.Shared;
        var selected = new List<ComparisonSuggestionDto>();
        var remaining = new List<(int deckIdA, int deckIdB, double score)>(topCandidates);

        for (int pick = 0; pick < Math.Min(count, topCandidates.Count); pick++)
        {
            var totalScore = remaining.Sum(c => c.score);
            var roll = rng.NextDouble() * totalScore;
            var cumulative = 0.0;
            var chosenIdx = remaining.Count - 1;

            for (int k = 0; k < remaining.Count; k++)
            {
                cumulative += remaining[k].score;
                if (cumulative >= roll)
                {
                    chosenIdx = k;
                    break;
                }
            }

            var (aId, bId, _) = remaining[chosenIdx];
            remaining.RemoveAt(chosenIdx);

            var dA = deckMap[aId];
            var dB = deckMap[bId];
            selected.Add(new ComparisonSuggestionDto
            {
                DeckA = MapDeckSummary(dA.DeckId, dA.OriginalTitle, dA.RomajiTitle, dA.EnglishTitle, dA.CoverName, dA.Difficulty, dA.MediaType),
                DeckB = MapDeckSummary(dB.DeckId, dB.OriginalTitle, dB.RomajiTitle, dB.EnglishTitle, dB.CoverName, dB.Difficulty, dB.MediaType)
            });
        }

        return Results.Ok(selected);
    }

    [HttpGet("mine")]
    public async Task<IResult> GetMyVotes(
        [FromQuery] string type = "comparisons",
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 20)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);

        switch (type)
        {
            case "ratings":
            {
                var ratingsQuery = context.DifficultyRatings.AsNoTracking()
                    .Where(r => r.UserId == userId)
                    .OrderByDescending(r => r.Id);

                var totalItems = await ratingsQuery.CountAsync();
                var ratings = await ratingsQuery
                    .Skip(offset)
                    .Take(limit)
                    .Include(r => r.Deck)
                    .ToListAsync();

                var data = ratings.Select(r => new DifficultyRatingDto
                {
                    Id = r.Id,
                    DeckId = r.DeckId,
                    DeckTitle = r.Deck.OriginalTitle,
                    RomajiTitle = r.Deck.RomajiTitle,
                    EnglishTitle = r.Deck.EnglishTitle,
                    CoverUrl = r.Deck.CoverName,
                    MediaType = r.Deck.MediaType,
                    Rating = r.Rating,
                    CreatedAt = r.CreatedAt
                }).ToList();

                return Results.Ok(new { data, totalItems });
            }

            case "skipped":
            {
                var skippedQuery = context.SkippedComparisons.AsNoTracking()
                    .Where(s => s.UserId == userId)
                    .OrderByDescending(s => s.Id);

                var totalItems = await skippedQuery.CountAsync();
                var skipped = await skippedQuery
                    .Skip(offset)
                    .Take(limit)
                    .ToListAsync();

                var deckIds = skipped.SelectMany(s => new[] { s.DeckLowId, s.DeckHighId }).Distinct().ToList();
                var decks = await context.Decks.AsNoTracking()
                    .Where(d => deckIds.Contains(d.DeckId))
                    .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType })
                    .ToDictionaryAsync(d => d.DeckId);

                var data = skipped.Select(s => new DifficultyVoteDto
                {
                    Id = s.Id,
                    DeckA = decks.TryGetValue(s.DeckLowId, out var dLow) ? MapDeckSummary(dLow.DeckId, dLow.OriginalTitle, dLow.RomajiTitle, dLow.EnglishTitle, dLow.CoverName, dLow.Difficulty, dLow.MediaType) : null!,
                    DeckB = decks.TryGetValue(s.DeckHighId, out var dHigh) ? MapDeckSummary(dHigh.DeckId, dHigh.OriginalTitle, dHigh.RomajiTitle, dHigh.EnglishTitle, dHigh.CoverName, dHigh.Difficulty, dHigh.MediaType) : null!,
                    Outcome = ComparisonOutcome.Same,
                    CreatedAt = s.CreatedAt
                }).ToList();

                return Results.Ok(new { data, totalItems });
            }

            default: // "comparisons"
            {
                var votesQuery = context.DifficultyVotes.AsNoTracking()
                    .Where(v => v.UserId == userId && v.IsValid && v.Source == DifficultyVoteSource.Manual)
                    .OrderByDescending(v => v.Id);

                var totalItems = await votesQuery.CountAsync();
                var votes = await votesQuery
                    .Skip(offset)
                    .Take(limit)
                    .Include(v => v.DeckLow)
                    .Include(v => v.DeckHigh)
                    .ToListAsync();

                var data = votes.Select(v => new DifficultyVoteDto
                {
                    Id = v.Id,
                    DeckA = MapDeckSummary(v.DeckLow),
                    DeckB = MapDeckSummary(v.DeckHigh),
                    Outcome = v.Outcome,
                    CreatedAt = v.CreatedAt
                }).ToList();

                return Results.Ok(new { data, totalItems });
            }
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IResult> DeleteVote(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var vote = await context.DifficultyVotes.FindAsync(id);
        if (vote == null || vote.UserId != userId)
            return Results.NotFound();

        var wasManual = vote.Source == DifficultyVoteSource.Manual;
        var deckLowId = vote.DeckLowId;
        var deckHighId = vote.DeckHighId;

        context.DifficultyVotes.Remove(vote);
        await context.SaveChangesAsync();

        if (wasManual)
        {
            var decks = await context.Decks.AsNoTracking()
                .Where(d => d.DeckId == deckLowId || d.DeckId == deckHighId)
                .Select(d => new { d.DeckId, d.MediaType })
                .ToListAsync();

            if (decks.Count == 2)
            {
                var deckA = decks.First(d => d.DeckId == deckLowId);
                var deckB = decks.First(d => d.DeckId == deckHighId);
                var groupA = MediaTypeGroups.GetGroup(deckA.MediaType);
                var groupB = MediaTypeGroups.GetGroup(deckB.MediaType);
                if (groupA == groupB)
                    await DifficultyRankingSync.SyncDerivedVotes(context, userContext, userId, groupA);
            }
        }

        return Results.NoContent();
    }

    [HttpDelete("skip/{id:int}")]
    public async Task<IResult> DeleteSkip(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var skip = await context.SkippedComparisons.FindAsync(id);
        if (skip == null || skip.UserId != userId)
            return Results.NotFound();

        context.SkippedComparisons.Remove(skip);
        await context.SaveChangesAsync();
        return Results.NoContent();
    }

    [HttpPost("skip")]
    public async Task<IResult> SkipPair([FromBody] SkipPairRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (request.DeckAId == request.DeckBId)
            return Results.BadRequest("Cannot skip a pair with the same deck.");

        var hasChildDeck = await context.Decks.AsNoTracking()
            .Where(d => d.DeckId == request.DeckAId || d.DeckId == request.DeckBId)
            .AnyAsync(d => d.ParentDeckId != null);
        if (hasChildDeck)
            return Results.BadRequest("Difficulty comparisons are only available for parent decks, not subdecks.");

        var deckLowId = Math.Min(request.DeckAId, request.DeckBId);
        var deckHighId = Math.Max(request.DeckAId, request.DeckBId);

        var existing = await context.SkippedComparisons
            .FirstOrDefaultAsync(s => s.UserId == userId && s.DeckLowId == deckLowId && s.DeckHighId == deckHighId);

        if (existing != null)
        {
            existing.Permanent = request.Permanent;
            existing.CreatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
            return Results.Ok(new { id = existing.Id });
        }

        var skip = new SkippedComparison
        {
            UserId = userId,
            DeckLowId = deckLowId,
            DeckHighId = deckHighId,
            Permanent = request.Permanent,
            CreatedAt = DateTimeOffset.UtcNow
        };

        context.SkippedComparisons.Add(skip);
        await context.SaveChangesAsync();
        return Results.Ok(new { id = skip.Id });
    }

    [HttpGet("unrated-decks")]
    public async Task<IResult> GetUnratedDecks()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var completedDeckIds = await MediaListEntryHelper.CompletedDeckIds(userContext, userId).ToListAsync();

        var ratedDeckIds = await context.DifficultyRatings.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => r.DeckId)
            .ToListAsync();

        var unratedIds = completedDeckIds.Except(ratedDeckIds).ToList();

        if (unratedIds.Count == 0)
            return Results.Ok(Array.Empty<DeckSummaryDto>());

        var decks = await context.Decks.AsNoTracking()
            .Where(d => unratedIds.Contains(d.DeckId) && d.ParentDeckId == null)
            .OrderBy(d => d.OriginalTitle)
            .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType })
            .ToListAsync();

        return Results.Ok(decks.Select(d => MapDeckSummary(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType)));
    }

    [HttpGet("stats")]
    public async Task<IResult> GetMyStats()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var totalComparisons = await context.DifficultyVotes
            .CountAsync(v => v.UserId == userId && v.IsValid && v.Source == DifficultyVoteSource.Manual);

        var totalRatings = await context.DifficultyRatings
            .CountAsync(r => r.UserId == userId);

        int? percentile = null;
        if (totalComparisons > 0)
        {
            var userCounts = context.DifficultyVotes
                .Where(v => v.IsValid && v.Source == DifficultyVoteSource.Manual)
                .GroupBy(v => v.UserId)
                .Select(g => new { Count = g.Count() });

            var totalUsers = await userCounts.CountAsync();
            var belowCount = await userCounts.CountAsync(u => u.Count < totalComparisons);
            percentile = (int)Math.Round(100.0 * belowCount / totalUsers);
        }

        return Results.Ok(new VotingStatsDto
        {
            TotalComparisons = totalComparisons,
            TotalRatings = totalRatings,
            Percentile = percentile
        });
    }

    [HttpGet("completed-decks")]
    public async Task<IResult> GetCompletedDecks()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var completedDeckIds = await MediaListEntryHelper.CompletedDeckIds(userContext, userId).ToListAsync();

        if (completedDeckIds.Count == 0)
            return Results.Ok(new CompletedDecksResponse());

        var blacklistedDeckIds = await context.BlacklistedComparisonDecks.AsNoTracking()
            .Where(b => b.UserId == userId)
            .Select(b => b.DeckId)
            .ToListAsync();

        var decks = await context.Decks.AsNoTracking()
            .Where(d => completedDeckIds.Contains(d.DeckId) && !blacklistedDeckIds.Contains(d.DeckId) && d.ParentDeckId == null)
            .OrderBy(d => d.OriginalTitle)
            .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType })
            .ToListAsync();

        var votedPairs = await context.DifficultyVotes.AsNoTracking()
            .Where(v => v.UserId == userId && v.IsValid
                && !blacklistedDeckIds.Contains(v.DeckLowId)
                && !blacklistedDeckIds.Contains(v.DeckHighId))
            .Select(v => new[] { v.DeckLowId, v.DeckHighId })
            .ToArrayAsync();

        return Results.Ok(new CompletedDecksResponse
        {
            Decks = decks.Select(d => MapDeckSummary(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType)).ToArray(),
            VotedPairs = votedPairs
        });
    }

    [HttpPost("blacklist/{deckId:int}")]
    public async Task<IResult> BlockDeck(int deckId)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var deckExists = await context.Decks.AnyAsync(d => d.DeckId == deckId);
        if (!deckExists)
            return Results.NotFound("Deck not found.");

        var existing = await context.BlacklistedComparisonDecks
            .AnyAsync(b => b.UserId == userId && b.DeckId == deckId);
        if (existing)
            return Results.Ok();

        context.BlacklistedComparisonDecks.Add(new BlacklistedComparisonDeck
        {
            UserId = userId,
            DeckId = deckId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        return Results.Ok();
    }

    [HttpDelete("blacklist/{deckId:int}")]
    public async Task<IResult> UnblockDeck(int deckId)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var entry = await context.BlacklistedComparisonDecks
            .FirstOrDefaultAsync(b => b.UserId == userId && b.DeckId == deckId);
        if (entry == null)
            return Results.NotFound();

        context.BlacklistedComparisonDecks.Remove(entry);
        await context.SaveChangesAsync();
        return Results.NoContent();
    }

    [HttpGet("blacklist")]
    public async Task<IResult> GetBlockedDecks([FromQuery] int offset = 0, [FromQuery] int limit = 20)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);

        var query = context.BlacklistedComparisonDecks.AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderByDescending(b => b.Id);

        var totalItems = await query.CountAsync();
        var blocked = await query
            .Skip(offset)
            .Take(limit)
            .Include(b => b.Deck)
            .ToListAsync();

        var data = blocked.Select(b => new BlacklistedDeckDto
        {
            DeckId = b.DeckId,
            Title = b.Deck.OriginalTitle,
            RomajiTitle = b.Deck.RomajiTitle,
            EnglishTitle = b.Deck.EnglishTitle,
            CoverUrl = b.Deck.CoverName,
            MediaType = b.Deck.MediaType,
            CreatedAt = b.CreatedAt
        }).ToList();

        return Results.Ok(new { data, totalItems });
    }

    private static DeckSummaryDto MapDeckSummary(Deck deck) => new()
    {
        Id = deck.DeckId,
        Title = deck.OriginalTitle,
        RomajiTitle = deck.RomajiTitle,
        EnglishTitle = deck.EnglishTitle,
        CoverUrl = deck.CoverName,
        Difficulty = deck.Difficulty,
        MediaType = deck.MediaType
    };

    private static DeckSummaryDto MapDeckSummary(int deckId, string title, string? romajiTitle, string? englishTitle, string coverName, float difficulty, MediaType mediaType) => new()
    {
        Id = deckId,
        Title = title,
        RomajiTitle = romajiTitle,
        EnglishTitle = englishTitle,
        CoverUrl = coverName,
        Difficulty = difficulty,
        MediaType = mediaType
    };
}
