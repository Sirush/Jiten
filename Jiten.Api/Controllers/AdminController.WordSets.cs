using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    [HttpGet("word-sets")]
    public async Task<IActionResult> GetWordSets()
    {
        var sets = await dbContext.WordSets
            .AsNoTracking()
            .OrderBy(ws => ws.SetId)
            .Select(ws => new WordSetDto
            {
                SetId = ws.SetId,
                Slug = ws.Slug,
                Name = ws.Name,
                Description = ws.Description,
                WordCount = ws.WordCount,
                FormCount = ws.Members.Count
            })
            .ToListAsync();

        return Ok(sets);
    }

    [HttpPost("word-sets")]
    public async Task<IActionResult> CreateWordSet([FromBody] CreateWordSetRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var slugExists = await dbContext.WordSets.AnyAsync(ws => ws.Slug == request.Slug);
        if (slugExists)
            return BadRequest(new { Message = $"A word set with slug '{request.Slug}' already exists" });

        var wordSet = new WordSet
        {
            Slug = request.Slug,
            Name = request.Name,
            Description = request.Description,
            WordCount = 0,
            CreatedAt = DateTime.UtcNow
        };

        dbContext.WordSets.Add(wordSet);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin created word set: SetId={SetId}, Slug={Slug}", wordSet.SetId, wordSet.Slug);

        return Ok(new WordSetDto
        {
            SetId = wordSet.SetId,
            Slug = wordSet.Slug,
            Name = wordSet.Name,
            Description = wordSet.Description,
            WordCount = 0,
            FormCount = 0
        });
    }

    [HttpPut("word-sets/{setId:int}")]
    public async Task<IActionResult> UpdateWordSet(int setId, [FromBody] UpdateWordSetRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var wordSet = await dbContext.WordSets.FirstOrDefaultAsync(ws => ws.SetId == setId);
        if (wordSet == null)
            return NotFound(new { Message = "Word set not found" });

        var slugExists = await dbContext.WordSets.AnyAsync(ws => ws.Slug == request.Slug && ws.SetId != setId);
        if (slugExists)
            return BadRequest(new { Message = $"A word set with slug '{request.Slug}' already exists" });

        wordSet.Slug = request.Slug;
        wordSet.Name = request.Name;
        wordSet.Description = request.Description;

        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin updated word set: SetId={SetId}, Slug={Slug}", setId, request.Slug);

        return Ok(new WordSetDto
        {
            SetId = wordSet.SetId,
            Slug = wordSet.Slug,
            Name = wordSet.Name,
            Description = wordSet.Description,
            WordCount = wordSet.WordCount,
            FormCount = await dbContext.WordSetMembers.CountAsync(m => m.SetId == setId)
        });
    }

    [HttpDelete("word-sets/{setId:int}")]
    public async Task<IActionResult> DeleteWordSet(int setId, [FromServices] IWordSetMemberCache wordSetCache)
    {
        var wordSet = await dbContext.WordSets.FirstOrDefaultAsync(ws => ws.SetId == setId);
        if (wordSet == null)
            return NotFound(new { Message = "Word set not found" });

        // Clean up UserWordSetState entries from UserDbContext first
        await userContext.UserWordSetStates
            .Where(uwss => uwss.SetId == setId)
            .ExecuteDeleteAsync();

        // Delete the word set (cascade handles members)
        dbContext.WordSets.Remove(wordSet);
        await dbContext.SaveChangesAsync();
        wordSetCache.Invalidate(setId);

        logger.LogWarning("Admin deleted word set: SetId={SetId}, Slug={Slug}", setId, wordSet.Slug);

        return Ok(new { Message = $"Word set '{wordSet.Name}' deleted" });
    }

    [HttpGet("word-sets/{setId:int}/member-keys")]
    public async Task<IActionResult> GetMemberKeys(int setId)
    {
        var keys = await dbContext.WordSetMembers
            .AsNoTracking()
            .Where(m => m.SetId == setId)
            .Select(m => new { m.WordId, m.ReadingIndex })
            .ToListAsync();

        return Ok(keys);
    }

    [HttpGet("word-sets/{setId:int}/members")]
    public async Task<IActionResult> GetMembers(
        int setId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string sortBy = "position",
        [FromQuery] string sortOrder = "asc")
    {
        limit = Math.Clamp(limit, 1, 100);

        var setExists = await dbContext.WordSets.AnyAsync(ws => ws.SetId == setId);
        if (!setExists)
            return NotFound(new { Message = "Word set not found" });

        var baseQuery = dbContext.WordSetMembers
            .AsNoTracking()
            .Where(m => m.SetId == setId);

        var totalCount = await baseQuery.CountAsync();

        IOrderedQueryable<WordSetMember> sorted;
        if (sortBy == "globalFreq")
        {
            sorted = sortOrder == "desc"
                ? baseQuery.OrderByDescending(m => dbContext.WordFormFrequencies
                    .Where(wff => wff.WordId == m.WordId && wff.ReadingIndex == m.ReadingIndex)
                    .Select(wff => wff.FrequencyRank)
                    .FirstOrDefault()).ThenBy(m => m.Position)
                : baseQuery.OrderBy(m => dbContext.WordFormFrequencies
                    .Where(wff => wff.WordId == m.WordId && wff.ReadingIndex == m.ReadingIndex)
                    .Select(wff => wff.FrequencyRank)
                    .FirstOrDefault()).ThenBy(m => m.Position);
        }
        else
        {
            sorted = sortOrder == "desc"
                ? baseQuery.OrderByDescending(m => m.Position)
                : baseQuery.OrderBy(m => m.Position);
        }

        var pagedItems = await sorted.Skip(offset).Take(limit).ToListAsync();
        var pagedWordIds = pagedItems.Select(p => p.WordId).Distinct().ToList();

        var formDict = await WordFormHelper.LoadWordForms(dbContext, pagedWordIds);
        var freqDict = await WordFormHelper.LoadWordFormFrequencies(dbContext, pagedWordIds);

        var words = await dbContext.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .Where(w => pagedWordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);

        var members = pagedItems.Select(p =>
        {
            var form = formDict.GetValueOrDefault((p.WordId, p.ReadingIndex));
            var freq = freqDict.GetValueOrDefault((p.WordId, p.ReadingIndex));
            words.TryGetValue(p.WordId, out var word);

            var meanings = word?.Definitions
                .OrderBy(d => d.DefinitionId)
                .FirstOrDefault(d => d.EnglishMeanings.Count > 0)?
                .EnglishMeanings ?? [];

            return new AdminWordSetMemberDto
            {
                WordId = p.WordId,
                ReadingIndex = p.ReadingIndex,
                Position = p.Position,
                Text = form?.Text ?? "",
                RubyText = form?.RubyText ?? "",
                Meanings = meanings,
                PartsOfSpeech = word?.PartsOfSpeech ?? [],
                FrequencyRank = freq?.FrequencyRank ?? 0
            };
        }).ToList();

        return Ok(new PaginatedResponse<List<AdminWordSetMemberDto>>(members, totalCount, limit, offset));
    }

    [HttpPost("word-sets/{setId:int}/members")]
    public async Task<IActionResult> AddMembers(int setId, [FromBody] AddWordSetMembersRequest request,
                                                [FromServices] IWordSetMemberCache wordSetCache)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var wordSet = await dbContext.WordSets.FirstOrDefaultAsync(ws => ws.SetId == setId);
        if (wordSet == null)
            return NotFound(new { Message = "Word set not found" });

        // Validate that the words exist
        var requestedWordIds = request.Members.Select(m => m.WordId).Distinct().ToList();
        var existingWordIds = await dbContext.JMDictWords
            .AsNoTracking()
            .Where(w => requestedWordIds.Contains(w.WordId))
            .Select(w => w.WordId)
            .ToListAsync();
        var existingWordIdSet = existingWordIds.ToHashSet();

        // Get existing members to skip duplicates (only check the requested WordIds)
        var existingMembers = await dbContext.WordSetMembers
            .AsNoTracking()
            .Where(m => m.SetId == setId && requestedWordIds.Contains(m.WordId))
            .Select(m => new { m.WordId, m.ReadingIndex })
            .ToListAsync();
        var existingMemberSet = existingMembers.Select(m => (m.WordId, m.ReadingIndex)).ToHashSet();

        var maxPosition = await dbContext.WordSetMembers
            .Where(m => m.SetId == setId)
            .Select(m => (int?)m.Position)
            .MaxAsync() ?? 0;

        int added = 0;
        int skipped = 0;

        foreach (var member in request.Members)
        {
            if (!existingWordIdSet.Contains(member.WordId))
            {
                skipped++;
                continue;
            }

            if (existingMemberSet.Contains((member.WordId, member.ReadingIndex)))
            {
                skipped++;
                continue;
            }

            maxPosition++;
            dbContext.WordSetMembers.Add(new WordSetMember
            {
                SetId = setId,
                WordId = member.WordId,
                ReadingIndex = member.ReadingIndex,
                Position = maxPosition
            });
            existingMemberSet.Add((member.WordId, member.ReadingIndex));
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync();
            wordSetCache.Invalidate(setId);

            wordSet.WordCount = await dbContext.WordSetMembers
                .Where(m => m.SetId == setId)
                .Select(m => m.WordId)
                .Distinct()
                .CountAsync();
            await dbContext.SaveChangesAsync();
        }

        logger.LogInformation("Admin added members to word set: SetId={SetId}, Added={Added}, Skipped={Skipped}",
            setId, added, skipped);

        return Ok(new { added, skipped });
    }

    [HttpPost("word-sets/{setId:int}/members/remove")]
    public async Task<IActionResult> RemoveMembers(int setId, [FromBody] RemoveWordSetMembersRequest request,
                                                   [FromServices] IWordSetMemberCache wordSetCache)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var wordSet = await dbContext.WordSets.FirstOrDefaultAsync(ws => ws.SetId == setId);
        if (wordSet == null)
            return NotFound(new { Message = "Word set not found" });

        int removed = 0;
        foreach (var member in request.Members)
        {
            removed += await dbContext.WordSetMembers
                .Where(m => m.SetId == setId && m.WordId == member.WordId && m.ReadingIndex == member.ReadingIndex)
                .ExecuteDeleteAsync();
        }

        if (removed > 0)
        {
            wordSetCache.Invalidate(setId);
            wordSet.WordCount = await dbContext.WordSetMembers
                .Where(m => m.SetId == setId)
                .Select(m => m.WordId)
                .Distinct()
                .CountAsync();
            await dbContext.SaveChangesAsync();
        }

        logger.LogInformation("Admin removed members from word set: SetId={SetId}, Removed={Removed}", setId, removed);

        return Ok(new { removed });
    }

    [HttpGet("word-sets/{setId:int}/word-forms/{wordId:int}")]
    public async Task<IActionResult> GetWordSetWordForms(int setId, int wordId)
    {
        var setExists = await dbContext.WordSets.AnyAsync(ws => ws.SetId == setId);
        if (!setExists)
            return NotFound(new { Message = "Word set not found" });

        var word = await dbContext.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .FirstOrDefaultAsync(w => w.WordId == wordId);

        if (word == null)
            return NotFound(new { Message = $"Word {wordId} not found" });

        var forms = await WordFormHelper.LoadWordFormsForWord(dbContext, wordId);
        var freqs = await dbContext.WordFormFrequencies
            .AsNoTracking()
            .Where(wff => wff.WordId == wordId)
            .ToDictionaryAsync(wff => wff.ReadingIndex);

        var existingMembers = await dbContext.WordSetMembers
            .AsNoTracking()
            .Where(m => m.SetId == setId && m.WordId == wordId)
            .Select(m => m.ReadingIndex)
            .ToListAsync();
        var existingSet = existingMembers.ToHashSet();

        var meanings = word.Definitions
            .OrderBy(d => d.DefinitionId)
            .FirstOrDefault(d => d.EnglishMeanings.Count > 0)?
            .EnglishMeanings ?? [];

        var formDtos = forms.Select(f =>
        {
            freqs.TryGetValue(f.ReadingIndex, out var freq);
            return new
            {
                f.ReadingIndex,
                f.Text,
                RubyText = f.RubyText ?? "",
                FormType = (int)f.FormType,
                FrequencyRank = freq?.FrequencyRank ?? 0,
                AlreadyInSet = existingSet.Contains(f.ReadingIndex)
            };
        }).ToList();

        return Ok(new
        {
            word.WordId,
            word.PartsOfSpeech,
            Meanings = meanings,
            Forms = formDtos
        });
    }
}
