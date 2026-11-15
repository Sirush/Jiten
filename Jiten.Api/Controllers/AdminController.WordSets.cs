using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Services;
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
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
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
        wordSet.Name = request.Name.Trim();
        wordSet.Description = request.Description?.Trim();

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
        [FromQuery] string sortOrder = "asc",
        [FromQuery] bool manualOnly = false)
    {
        limit = Math.Clamp(limit, 1, 100);

        var setExists = await dbContext.WordSets.AnyAsync(ws => ws.SetId == setId);
        if (!setExists)
            return NotFound(new { Message = "Word set not found" });

        var baseQuery = dbContext.WordSetMembers
            .AsNoTracking()
            .Where(m => m.SetId == setId);
        if (manualOnly)
            baseQuery = baseQuery.Where(m => m.IsManual);

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
        var members = await HydrateMembers(pagedItems);

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
            .Where(m => m.SetId == setId && requestedWordIds.Contains(m.WordId))
            .ToListAsync();
        var existingMemberSet = existingMembers.Select(m => (m.WordId, m.ReadingIndex)).ToHashSet();
        var requestedKeys = request.Members.Select(m => (m.WordId, m.ReadingIndex)).ToHashSet();

        foreach (var existing in existingMembers.Where(m => requestedKeys.Contains((m.WordId, m.ReadingIndex))))
            existing.IsManual = true;

        var exclusions = await dbContext.WordSetExclusions
            .Where(e => e.SetId == setId && requestedWordIds.Contains(e.WordId))
            .ToListAsync();
        dbContext.WordSetExclusions.RemoveRange(exclusions.Where(e => requestedKeys.Contains((e.WordId, e.ReadingIndex))));

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
                Position = maxPosition,
                IsManual = true
            });
            existingMemberSet.Add((member.WordId, member.ReadingIndex));
            added++;
        }

        await dbContext.SaveChangesAsync();

        if (added > 0)
        {
            wordSetCache.Invalidate(setId);
            await CoverageDirtyHelper.MarkWordSetSubscribersDirty(userContext, setId);

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

        var requestedWordIds = request.Members.Select(m => m.WordId).Distinct().ToList();
        var excluded = (await dbContext.WordSetExclusions
                .AsNoTracking()
                .Where(e => e.SetId == setId && requestedWordIds.Contains(e.WordId))
                .Select(e => new { e.WordId, e.ReadingIndex })
                .ToListAsync())
            .Select(e => (e.WordId, e.ReadingIndex))
            .ToHashSet();

        int removed = 0;
        foreach (var member in request.Members)
        {
            var deleted = await dbContext.WordSetMembers
                .Where(m => m.SetId == setId && m.WordId == member.WordId && m.ReadingIndex == member.ReadingIndex)
                .ExecuteDeleteAsync();
            removed += deleted;

            if (deleted > 0 && wordSet.RuleJson != null && excluded.Add((member.WordId, member.ReadingIndex)))
            {
                dbContext.WordSetExclusions.Add(new WordSetExclusion
                {
                    SetId = setId, WordId = member.WordId, ReadingIndex = member.ReadingIndex
                });
            }
        }

        if (removed > 0)
        {
            wordSetCache.Invalidate(setId);
            await CoverageDirtyHelper.MarkWordSetSubscribersDirty(userContext, setId);
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

    [HttpGet("word-sets/{setId:int}/rule")]
    public async Task<IActionResult> GetWordSetRule(int setId)
    {
        var wordSet = await dbContext.WordSets.AsNoTracking().FirstOrDefaultAsync(ws => ws.SetId == setId);
        if (wordSet == null)
            return NotFound(new { Message = "Word set not found" });

        return Ok(new
        {
            wordSet.Rule,
            wordSet.RegeneratedAt,
            ManualCount = await dbContext.WordSetMembers.CountAsync(m => m.SetId == setId && m.IsManual),
            ExclusionCount = await dbContext.WordSetExclusions.CountAsync(e => e.SetId == setId)
        });
    }

    [HttpPost("word-sets/{setId:int}/rule/preview")]
    public async Task<IActionResult> PreviewWordSetRule(int setId, [FromBody] PreviewWordSetRuleRequest request, CancellationToken ct)
    {
        if (!await dbContext.WordSets.AnyAsync(ws => ws.SetId == setId, ct))
            return NotFound(new { Message = "Word set not found" });

        var error = NormaliseRule(request.Rule);
        if (error != null)
            return BadRequest(new { Message = error });

        var diff = await WordSetRuleResolver.DiffAsync(dbContext, setId, request.Rule, ct);

        return Ok(new
        {
            ToAddCount = diff.ToAdd.Count,
            UnmatchedCount = diff.Unmatched.Count,
            diff.Unchanged,
            diff.Manual,
            diff.Excluded,
            diff.UnknownPos,
            ToAdd = await HydrateSample(diff.ToAdd, ct),
            Unmatched = await HydrateSample(diff.Unmatched, ct)
        });
    }

    [HttpPost("word-sets/{setId:int}/rule/apply")]
    public async Task<IActionResult> ApplyWordSetRule(int setId, [FromBody] ApplyWordSetRuleRequest request,
                                                      [FromServices] IWordSetMemberCache wordSetCache, CancellationToken ct)
    {
        if (!await dbContext.WordSets.AnyAsync(ws => ws.SetId == setId, ct))
            return NotFound(new { Message = "Word set not found" });

        var error = NormaliseRule(request.Rule);
        if (error != null)
            return BadRequest(new { Message = error });
        if (!Enum.IsDefined(request.Unmatched))
            return BadRequest(new { Message = "Unknown action for members that no longer match" });

        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

        var diff = await WordSetRuleResolver.DiffAsync(dbContext, setId, request.Rule, ct);

        int removed = 0, markedManual = 0;
        if (request.Unmatched != UnmatchedMemberAction.Keep)
        {
            foreach (var chunk in diff.Unmatched.Chunk(MemberWriteBatchSize))
            {
                var keys = chunk.ToHashSet();
                var wordIds = chunk.Select(k => k.WordId).Distinct().ToList();
                var rows = await dbContext.WordSetMembers
                    .Where(m => m.SetId == setId && wordIds.Contains(m.WordId))
                    .ToListAsync(ct);

                foreach (var row in rows.Where(r => keys.Contains((r.WordId, r.ReadingIndex))))
                {
                    if (request.Unmatched == UnmatchedMemberAction.Remove)
                    {
                        dbContext.WordSetMembers.Remove(row);
                        removed++;
                    }
                    else
                    {
                        row.IsManual = true;
                        markedManual++;
                    }
                }

                await dbContext.SaveChangesAsync(ct);
                dbContext.ChangeTracker.Clear();
            }
        }

        var position = await dbContext.WordSetMembers
            .Where(m => m.SetId == setId)
            .Select(m => (int?)m.Position)
            .MaxAsync(ct) ?? 0;

        foreach (var chunk in diff.ToAdd.Chunk(MemberWriteBatchSize))
        {
            dbContext.WordSetMembers.AddRange(chunk.Select(k => new WordSetMember
            {
                SetId = setId, WordId = k.WordId, ReadingIndex = k.ReadingIndex, Position = ++position
            }));
            await dbContext.SaveChangesAsync(ct);
            dbContext.ChangeTracker.Clear();
        }

        var wordSet = await dbContext.WordSets.FirstAsync(ws => ws.SetId == setId, ct);
        wordSet.Rule = request.Rule;
        wordSet.RegeneratedAt = DateTime.UtcNow;
        wordSet.WordCount = await dbContext.WordSetMembers
            .Where(m => m.SetId == setId)
            .Select(m => m.WordId)
            .Distinct()
            .CountAsync(ct);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (diff.ToAdd.Count > 0 || removed > 0)
        {
            wordSetCache.Invalidate(setId);
            foreach (var userId in await CoverageDirtyHelper.MarkWordSetSubscribersDirty(userContext, setId))
                backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));
        }

        logger.LogInformation(
            "Admin regenerated word set: SetId={SetId}, Added={Added}, Removed={Removed}, MarkedManual={MarkedManual}",
            setId, diff.ToAdd.Count, removed, markedManual);

        return Ok(new
        {
            Added = diff.ToAdd.Count,
            Removed = removed,
            MarkedManual = markedManual,
            wordSet.WordCount,
            FormCount = await dbContext.WordSetMembers.CountAsync(m => m.SetId == setId, ct)
        });
    }

    private const int MemberWriteBatchSize = 5000;
    private const int RuleSampleSize = 200;
    private const int MaxRuleListLength = 50;

    private static string? NormaliseRule(WordSetRule rule)
    {
        rule.IncludePos = NormaliseTags(rule.IncludePos);
        rule.ExcludePos = NormaliseTags(rule.ExcludePos);
        rule.SourceLanguages = NormaliseTags(rule.SourceLanguages);
        rule.Origins = (rule.Origins ?? []).Distinct().ToList();

        if (rule.IncludePos.Count > MaxRuleListLength || rule.ExcludePos.Count > MaxRuleListLength ||
            rule.SourceLanguages.Count > MaxRuleListLength)
            return $"A rule takes at most {MaxRuleListLength} values per field";
        if (!Enum.IsDefined(rule.Script) || !Enum.IsDefined(rule.Dictionary) || !rule.Origins.TrueForAll(Enum.IsDefined))
            return "The rule holds an unknown option";
        if (rule.MaxFrequencyRank is <= 0)
            return "The frequency rank limit must be above 0";
        if (!rule.HasCriteria)
            return "The rule needs at least one criterion besides the dictionary and excluded parts of speech";

        return null;
    }

    private static List<string> NormaliseTags(List<string>? tags)
        => (tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();

    private async Task<List<AdminWordSetMemberDto>> HydrateSample(List<(int WordId, short ReadingIndex)> forms, CancellationToken ct)
    {
        var sample = forms;
        if (forms.Count > RuleSampleSize)
        {
            var remaining = forms.ToHashSet();
            sample = new List<(int, short)>(RuleSampleSize);
            await foreach (var f in dbContext.WordFormFrequencies
                               .AsNoTracking()
                               .Where(f => f.FrequencyRank > 0)
                               .OrderBy(f => f.FrequencyRank)
                               .Select(f => new { f.WordId, f.ReadingIndex })
                               .AsAsyncEnumerable().WithCancellation(ct))
            {
                if (!remaining.Remove((f.WordId, f.ReadingIndex))) continue;

                sample.Add((f.WordId, f.ReadingIndex));
                if (sample.Count == RuleSampleSize) break;
            }

            sample.AddRange(forms.Where(remaining.Contains).Take(RuleSampleSize - sample.Count));
        }

        return await HydrateMembers(sample.Select(f => new WordSetMember { WordId = f.WordId, ReadingIndex = f.ReadingIndex }).ToList());
    }

    private async Task<List<AdminWordSetMemberDto>> HydrateMembers(List<WordSetMember> items)
    {
        var wordIds = items.Select(p => p.WordId).Distinct().ToList();

        var formDict = await WordFormHelper.LoadWordForms(dbContext, wordIds);
        var freqDict = await WordFormHelper.LoadWordFormFrequencies(dbContext, wordIds);

        var words = await dbContext.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .Where(w => wordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);

        return items.Select(p =>
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
                FrequencyRank = freq?.FrequencyRank ?? 0,
                IsManual = p.IsManual
            };
        }).ToList();
    }
}
