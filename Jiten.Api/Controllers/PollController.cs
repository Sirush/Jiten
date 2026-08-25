using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/polls")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("fixed")]
public class PollController(
    JitenDbContext context,
    ICurrentUserService currentUserService,
    ILogger<PollController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetPolls([FromQuery] int offset = 0, [FromQuery] int limit = 10)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(offset, 0);

        var now = DateTime.UtcNow;
        var query = context.Polls.AsNoTracking().Where(p => p.PublishedAt != null);

        var totalCount = await query.CountAsync();

        var polls = await query
                          .OrderBy(p => p.ClosedAt != null || (p.ClosesAt != null && p.ClosesAt <= now) ? 1 : 0)
                          .ThenByDescending(p => p.PublishedAt)
                          .Skip(offset)
                          .Take(limit)
                          .Include(p => p.Options)
                          .ToListAsync();

        var dtos = await MapPolls(polls, userId, now);
        return Results.Ok(new PaginatedResponse<List<PollDto>>(dtos, totalCount, limit, offset));
    }

    [HttpGet("{id:int}")]
    public async Task<IResult> GetPoll(int id)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var poll = await context.Polls.AsNoTracking()
                                .Include(p => p.Options)
                                .FirstOrDefaultAsync(p => p.Id == id && p.PublishedAt != null);

        if (poll == null)
            return Results.NotFound();

        var now = DateTime.UtcNow;
        var dtos = await MapPolls([poll], userId, now);
        return Results.Ok(dtos[0]);
    }

    [HttpGet("home")]
    public async Task<IResult> GetHomePoll([FromQuery] int[]? exclude = null)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var active = await context.Polls.AsNoTracking()
                                  .Where(p => p.PublishedAt != null && p.ClosedAt == null && (p.ClosesAt == null || p.ClosesAt > now))
                                  .Include(p => p.Options)
                                  .ToListAsync();

        if (active.Count == 0)
            return Results.NoContent();

        var activeIds = active.Select(p => p.Id).ToList();
        var votedIds = await context.PollVotes.AsNoTracking()
                                    .Where(v => activeIds.Contains(v.PollId) && v.UserId == userId)
                                    .Select(v => v.PollId)
                                    .Distinct()
                                    .ToListAsync();

        var excluded = exclude ?? [];
        var unvoted = active.Where(p => !votedIds.Contains(p.Id) && !excluded.Contains(p.Id)).ToList();
        Poll pick;
        if (unvoted.Count > 0)
        {
            pick = unvoted[Random.Shared.Next(unvoted.Count)];
        }
        else
        {
            var voted = active.Where(p => votedIds.Contains(p.Id)).OrderByDescending(p => p.PublishedAt).FirstOrDefault();
            if (voted == null)
                return Results.NoContent();
            pick = voted;
        }

        var dtos = await MapPolls([pick], userId, now);
        return Results.Ok(dtos[0]);
    }

    [HttpPut("{id:int}/vote")]
    public async Task<IResult> Vote(int id, [FromBody] SubmitPollVoteRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var poll = await context.Polls.AsNoTracking()
                                .Include(p => p.Options)
                                .FirstOrDefaultAsync(p => p.Id == id && p.PublishedAt != null);

        if (poll == null)
            return Results.NotFound();

        var now = DateTime.UtcNow;
        if (poll.IsClosed(now))
            return Results.Conflict(new { Message = "Poll is closed" });

        var optionIds = request.OptionIds ?? [];
        if (optionIds.Count == 0 || optionIds.Distinct().Count() != optionIds.Count)
            return Results.BadRequest(new { Message = "Pick at least one option, without repeats" });

        if (optionIds.Any(optionId => poll.Options.All(o => o.Id != optionId)))
            return Results.BadRequest(new { Message = "An option does not belong to this poll" });

        if (optionIds.Count > poll.MaxSelections)
            return Results.BadRequest(new { Message = $"This poll allows at most {poll.MaxSelections} option(s)" });

        await using var transaction = await context.Database.BeginTransactionAsync();

        var existing = await context.PollVotes.Where(v => v.PollId == id && v.UserId == userId).ToListAsync();
        context.PollVotes.RemoveRange(existing);
        await context.SaveChangesAsync();

        context.PollVotes.AddRange(optionIds.Select(optionId => new PollVote
        {
            PollId = id,
            OptionId = optionId,
            UserId = userId,
            CreatedAt = now
        }));
        await context.SaveChangesAsync();

        await transaction.CommitAsync();

        logger.LogInformation("Poll vote recorded: PollId={PollId}, UserId={UserId}", id, userId);

        var dtos = await MapPolls([poll], userId, now);
        return Results.Ok(dtos[0]);
    }

    private async Task<List<PollDto>> MapPolls(List<Poll> polls, string userId, DateTime now)
    {
        var pollIds = polls.Select(p => p.Id).ToList();

        var optionCounts = await context.PollVotes.AsNoTracking()
                                        .Where(v => pollIds.Contains(v.PollId))
                                        .GroupBy(v => v.OptionId)
                                        .Select(g => new { OptionId = g.Key, Count = g.Count() })
                                        .ToDictionaryAsync(x => x.OptionId, x => x.Count);

        var voterCounts = await context.PollVotes.AsNoTracking()
                                       .Where(v => pollIds.Contains(v.PollId))
                                       .GroupBy(v => v.PollId)
                                       .Select(g => new { PollId = g.Key, Count = g.Select(v => v.UserId).Distinct().Count() })
                                       .ToDictionaryAsync(x => x.PollId, x => x.Count);

        var mine = await context.PollVotes.AsNoTracking()
                                .Where(v => pollIds.Contains(v.PollId) && v.UserId == userId)
                                .Select(v => new { v.PollId, v.OptionId })
                                .ToListAsync();

        var myOptionsByPoll = mine.GroupBy(v => v.PollId).ToDictionary(g => g.Key, g => g.Select(v => v.OptionId).ToList());

        return polls.Select(poll =>
        {
            var isClosed = poll.IsClosed(now);
            var myOptionIds = myOptionsByPoll.GetValueOrDefault(poll.Id, []);
            var resultsVisible = isClosed || myOptionIds.Count > 0;

            return new PollDto
            {
                Id = poll.Id,
                Question = poll.Question,
                DescriptionMarkdown = poll.DescriptionMarkdown,
                MaxSelections = poll.MaxSelections,
                PublishedAt = poll.PublishedAt,
                ClosesAt = poll.ClosesAt,
                IsClosed = isClosed,
                MyOptionIds = myOptionIds,
                ResultsVisible = resultsVisible,
                TotalVoters = resultsVisible ? voterCounts.GetValueOrDefault(poll.Id) : null,
                Options = poll.Options
                              .OrderBy(o => o.SortOrder)
                              .Select(o => new PollOptionDto
                              {
                                  Id = o.Id,
                                  Text = o.Text,
                                  SortOrder = o.SortOrder,
                                  VoteCount = resultsVisible ? optionCounts.GetValueOrDefault(o.Id) : null
                              })
                              .ToList()
            };
        }).ToList();
    }
}
