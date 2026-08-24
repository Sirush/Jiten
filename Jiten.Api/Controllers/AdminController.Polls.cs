using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    [HttpGet("polls")]
    public async Task<IActionResult> GetPolls()
    {
        var polls = await dbContext.Polls.AsNoTracking()
                                   .Include(p => p.Options)
                                   .OrderByDescending(p => p.CreatedAt)
                                   .ToListAsync();

        var pollIds = polls.Select(p => p.Id).ToList();
        var (optionCounts, voterCounts) = await LoadPollTallies(pollIds);

        return Ok(polls.Select(p => MapAdminPoll(p, optionCounts, voterCounts)).ToList());
    }

    [HttpGet("polls/{id:int}")]
    public async Task<IActionResult> GetPoll(int id)
    {
        var poll = await dbContext.Polls.AsNoTracking()
                                  .Include(p => p.Options)
                                  .FirstOrDefaultAsync(p => p.Id == id);

        if (poll == null)
            return NotFound(new { Message = "Poll not found" });

        var (optionCounts, voterCounts) = await LoadPollTallies([id]);
        return Ok(MapAdminPoll(poll, optionCounts, voterCounts));
    }

    [HttpPost("polls")]
    public async Task<IActionResult> CreatePoll([FromBody] SavePollRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var poll = new Poll
        {
            Question = request.Question.Trim(),
            DescriptionMarkdown = string.IsNullOrWhiteSpace(request.DescriptionMarkdown) ? null : request.DescriptionMarkdown.Trim(),
            MaxSelections = request.MaxSelections,
            ClosesAt = request.ClosesAt,
            CreatedAt = DateTime.UtcNow,
            Options = request.Options.Select((o, index) => new PollOption { Text = o.Text.Trim(), SortOrder = index }).ToList()
        };

        dbContext.Polls.Add(poll);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin created poll: Id={Id}", poll.Id);
        return Ok(new { poll.Id });
    }

    [HttpPut("polls/{id:int}")]
    public async Task<IActionResult> UpdatePoll(int id, [FromBody] SavePollRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var poll = await dbContext.Polls.Include(p => p.Options).FirstOrDefaultAsync(p => p.Id == id);
        if (poll == null)
            return NotFound(new { Message = "Poll not found" });

        var isPublished = poll.PublishedAt != null;

        if (isPublished && request.MaxSelections != poll.MaxSelections)
            return Conflict(new { Message = "The number of allowed selections cannot change after publishing" });

        var keptIds = request.Options.Where(o => o.Id.HasValue).Select(o => o.Id!.Value).ToHashSet();
        if (keptIds.Any(optionId => poll.Options.All(o => o.Id != optionId)))
            return BadRequest(new { Message = "An option does not belong to this poll" });

        var removed = poll.Options.Where(o => !keptIds.Contains(o.Id)).ToList();
        if (removed.Count > 0)
        {
            var removedIds = removed.Select(o => o.Id).ToList();
            var votedOption = await dbContext.PollVotes.AsNoTracking().AnyAsync(v => removedIds.Contains(v.OptionId));
            if (votedOption)
                return Conflict(new { Message = "An option with votes cannot be removed" });

            dbContext.PollOptions.RemoveRange(removed);
        }

        poll.Question = request.Question.Trim();
        poll.DescriptionMarkdown = string.IsNullOrWhiteSpace(request.DescriptionMarkdown) ? null : request.DescriptionMarkdown.Trim();
        poll.MaxSelections = request.MaxSelections;
        poll.ClosesAt = request.ClosesAt;
        poll.UpdatedAt = DateTime.UtcNow;

        for (var index = 0; index < request.Options.Count; index++)
        {
            var incoming = request.Options[index];
            if (incoming.Id.HasValue)
            {
                var existing = poll.Options.First(o => o.Id == incoming.Id.Value);
                existing.Text = incoming.Text.Trim();
                existing.SortOrder = index;
            }
            else
            {
                poll.Options.Add(new PollOption { PollId = poll.Id, Text = incoming.Text.Trim(), SortOrder = index });
            }
        }

        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin edited poll: Id={Id}", id);
        return Ok(new { Message = "Poll saved" });
    }

    [HttpPost("polls/{id:int}/publish")]
    public async Task<IActionResult> PublishPoll(int id)
    {
        var poll = await dbContext.Polls.Include(p => p.Options).FirstOrDefaultAsync(p => p.Id == id);
        if (poll == null)
            return NotFound(new { Message = "Poll not found" });

        if (poll.Options.Count < 2)
            return BadRequest(new { Message = "A poll needs at least two options before it can be published" });

        poll.PublishedAt ??= DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin published poll: Id={Id}", id);
        return Ok(new { Message = "Poll published" });
    }

    [HttpPost("polls/{id:int}/close")]
    public async Task<IActionResult> ClosePoll(int id)
    {
        var poll = await dbContext.Polls.FirstOrDefaultAsync(p => p.Id == id);
        if (poll == null)
            return NotFound(new { Message = "Poll not found" });

        poll.ClosedAt ??= DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin closed poll: Id={Id}", id);
        return Ok(new { Message = "Poll closed" });
    }

    [HttpPost("polls/{id:int}/reopen")]
    public async Task<IActionResult> ReopenPoll(int id)
    {
        var poll = await dbContext.Polls.FirstOrDefaultAsync(p => p.Id == id);
        if (poll == null)
            return NotFound(new { Message = "Poll not found" });

        poll.ClosedAt = null;
        // A ClosesAt already in the past would keep the poll computed-closed
        if (poll.ClosesAt != null && poll.ClosesAt <= DateTime.UtcNow)
            poll.ClosesAt = null;
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin reopened poll: Id={Id}", id);
        return Ok(new { Message = "Poll reopened" });
    }

    [HttpDelete("polls/{id:int}")]
    public async Task<IActionResult> DeletePoll(int id)
    {
        var poll = await dbContext.Polls.FirstOrDefaultAsync(p => p.Id == id);
        if (poll == null)
            return NotFound(new { Message = "Poll not found" });

        dbContext.Polls.Remove(poll);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin deleted poll: Id={Id}", id);
        return Ok(new { Message = "Poll deleted" });
    }

    private async Task<(Dictionary<int, int> OptionCounts, Dictionary<int, int> VoterCounts)> LoadPollTallies(List<int> pollIds)
    {
        if (pollIds.Count == 0)
            return (new Dictionary<int, int>(), new Dictionary<int, int>());

        var optionCounts = await dbContext.PollVotes.AsNoTracking()
                                          .Where(v => pollIds.Contains(v.PollId))
                                          .GroupBy(v => v.OptionId)
                                          .Select(g => new { OptionId = g.Key, Count = g.Count() })
                                          .ToDictionaryAsync(x => x.OptionId, x => x.Count);

        var voterCounts = await dbContext.PollVotes.AsNoTracking()
                                         .Where(v => pollIds.Contains(v.PollId))
                                         .GroupBy(v => v.PollId)
                                         .Select(g => new { PollId = g.Key, Count = g.Select(v => v.UserId).Distinct().Count() })
                                         .ToDictionaryAsync(x => x.PollId, x => x.Count);

        return (optionCounts, voterCounts);
    }

    private static AdminPollDto MapAdminPoll(Poll poll, Dictionary<int, int> optionCounts, Dictionary<int, int> voterCounts) => new()
    {
        Id = poll.Id,
        Question = poll.Question,
        DescriptionMarkdown = poll.DescriptionMarkdown,
        MaxSelections = poll.MaxSelections,
        CreatedAt = poll.CreatedAt,
        UpdatedAt = poll.UpdatedAt,
        PublishedAt = poll.PublishedAt,
        ClosesAt = poll.ClosesAt,
        ClosedAt = poll.ClosedAt,
        IsClosed = poll.IsClosed(DateTime.UtcNow),
        TotalVoters = voterCounts.GetValueOrDefault(poll.Id),
        Options = poll.Options
                      .OrderBy(o => o.SortOrder)
                      .Select(o => new AdminPollOptionDto
                      {
                          Id = o.Id,
                          Text = o.Text,
                          SortOrder = o.SortOrder,
                          VoteCount = optionCounts.GetValueOrDefault(o.Id)
                      })
                      .ToList()
    };
}
