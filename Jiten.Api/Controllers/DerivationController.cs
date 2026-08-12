using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/derivations")]
public class DerivationController(
    IDerivationLinkCache derivationCache,
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IMemoryCache memoryCache) : ControllerBase
{
    [HttpGet("categories")]
    [SwaggerOperation(Summary = "List derivational redundancy categories",
                      Description = "Category metadata and live pair counts for the study settings checkboxes.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IResult GetCategories()
    {
        var counts = derivationCache.PairCounts;

        var categoriesByGroup = DerivationCategories.Shipped
                                                    .GroupBy(c => c.Group)
                                                    .ToDictionary(g => g.Key, g => g.ToList());

        var groups = DerivationCategories.Groups
                                         .Where(g => categoriesByGroup.ContainsKey(g.Key))
                                         .Select(g => new DerivationCategoryGroupDto
                                         {
                                             Key = g.Key,
                                             Label = g.Label,
                                             Explanation = g.Explanation,
                                             Categories = categoriesByGroup[g.Key]
                                                          .Select(c => new DerivationCategoryDto
                                                          {
                                                              Key = c.Key,
                                                              Label = c.Label,
                                                              ExampleBase = c.ExampleBase,
                                                              ExampleDerived = c.ExampleDerived,
                                                              Explanation = c.Explanation,
                                                              PairCount = counts.GetValueOrDefault(c.Category)
                                                          })
                                                          .ToList()
                                         })
                                         .ToList();

        foreach (var group in groups)
            group.PairCount = group.Categories.Sum(c => c.PairCount);

        return Results.Ok(groups.OrderByDescending(g => g.PairCount).ThenBy(g => g.Label).ToList());
    }

    [HttpGet("personal-summary")]
    [Authorize]
    [SwaggerOperation(Summary = "Per-group derivation coverage for the current user",
                      Description = "For each checkbox group, how many words the user's vocabulary covers (or " +
                                    "would cover) through it. Counts are marginal against the current selection.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> GetPersonalSummary()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var enabled = await DerivationSettingsHelper.GetEnabledCategories(memoryCache, userContext, userId);
        var conductors = await LoadDerivationConductors(userId);
        var knownWordIds = conductors.Select(c => c.WordId).ToHashSet();

        int CoveredWords(IReadOnlySet<DerivationCategory> categories)
        {
            if (categories.Count == 0 || conductors.Count == 0) return 0;

            var covered = new HashSet<long>();
            WordFormHelper.ExpandDerivationRedundancyKeys(derivationCache, categories, conductors, covered);

            var words = new HashSet<int>();
            foreach (var key in covered)
            {
                var wordId = (int)(key >> 8);
                if (!knownWordIds.Contains(wordId))
                    words.Add(wordId);
            }

            return words.Count;
        }

        var total = CoveredWords(enabled);
        var groups = new List<DerivationGroupPersonalDto>();

        foreach (var group in DerivationCategories.Groups)
        {
            var groupCategories = DerivationCategories.Shipped
                                                      .Where(c => c.Group == group.Key)
                                                      .Select(c => c.Category)
                                                      .ToHashSet();
            if (groupCategories.Count == 0) continue;

            var groupEnabled = groupCategories.All(enabled.Contains);
            var without = enabled.Except(groupCategories).ToHashSet();
            var with = enabled.Union(groupCategories).ToHashSet();

            groups.Add(new DerivationGroupPersonalDto
            {
                Key = group.Key,
                Enabled = groupEnabled,
                CoveredWords = groupEnabled ? total - CoveredWords(without) : CoveredWords(with) - total
            });
        }

        return Results.Ok(new DerivationPersonalSummaryDto { TotalCoveredWords = total, Groups = groups });
    }

    /// <summary>Same conductor set as new-card selection: every card plus Mastered/Blacklisted set members.</summary>
    private async Task<List<(int WordId, byte ReadingIndex)>> LoadDerivationConductors(string userId)
    {
        var cards = await userContext.FsrsCards
                                     .AsNoTracking()
                                     .Where(c => c.UserId == userId)
                                     .Select(c => new { c.WordId, c.ReadingIndex })
                                     .ToListAsync();
        var conductors = cards.Select(c => (c.WordId, c.ReadingIndex)).ToList();

        var conductingSetIds = await userContext.UserWordSetStates
                                                .AsNoTracking()
                                                .Where(s => s.UserId == userId &&
                                                            (s.State == WordSetStateType.Mastered ||
                                                             s.State == WordSetStateType.Blacklisted))
                                                .Select(s => s.SetId)
                                                .ToListAsync();

        if (conductingSetIds.Count > 0)
        {
            var members = await context.WordSetMembers
                                       .AsNoTracking()
                                       .Where(m => conductingSetIds.Contains(m.SetId))
                                       .Select(m => new { m.WordId, m.ReadingIndex })
                                       .ToListAsync();
            conductors.AddRange(members.Select(m => ((int)m.WordId, (byte)m.ReadingIndex)));
        }

        return conductors;
    }

    /// <summary>Companion to the shared, publicly-cached pair list: the per-user marking it must not carry.</summary>
    [HttpGet("pairs-personal")]
    [Authorize]
    [SwaggerOperation(Summary = "Per-user marking for one group's preview list",
                      Description = "Which of the group's derived entries the user's vocabulary already makes " +
                                    "redundant, and which entries in the group already count as known. Covered ids " +
                                    "are marginal against the current selection, matching the group's summary count.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> GetPersonalPairs(string group)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var groupCategories = DerivationCategories.Shipped
                                                  .Where(c => c.Group == group)
                                                  .Select(c => c.Category)
                                                  .ToHashSet();
        if (groupCategories.Count == 0) return Results.NotFound();

        var enabled = await DerivationSettingsHelper.GetEnabledCategories(memoryCache, userContext, userId);
        var conductors = await LoadDerivationConductors(userId);
        var knownWordIds = conductors.Select(c => c.WordId).ToHashSet();

        var conductorKeys = conductors.Select(c => WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex)).ToHashSet();

        // shadowByWord mirrors how the summary counts, where any known reading disqualifies the whole entry.
        // Status must not: the platform only lets a card on the exact form hide that form's cover.
        HashSet<long> Covered(IReadOnlySet<DerivationCategory> categories, bool shadowByWord)
        {
            var covered = new HashSet<long>();
            if (categories.Count == 0 || conductors.Count == 0) return covered;

            WordFormHelper.ExpandDerivationRedundancyKeys(derivationCache, categories, conductors, covered);
            if (shadowByWord) covered.RemoveWhere(k => knownWordIds.Contains((int)(k >> 8)));
            else covered.ExceptWith(conductorKeys);
            return covered;
        }

        var groupEnabled = groupCategories.All(enabled.Contains);

        // Status is what holds today, so a form another ticked group already earns still reads as redundant here.
        var redundantKeys = Covered(enabled, shadowByWord: false);

        // The group's own share, exactly as GetPersonalSummary counts it: transitivity means a form reachable
        // through another ticked group is not this group's to claim.
        var addedByGroup = Covered(groupEnabled ? enabled : enabled.Union(groupCategories).ToHashSet(), true);
        addedByGroup.ExceptWith(Covered(groupEnabled ? enabled.Except(groupCategories).ToHashSet() : enabled, true));

        var rows = await context.WordDerivations
                                .AsNoTracking()
                                .Where(d => groupCategories.Contains(d.Category))
                                .Select(d => new
                                {
                                    d.BaseWordId, d.BaseReadingIndex, d.DerivedWordId, d.DerivedReadingIndex, d.Category
                                })
                                .ToListAsync();

        // Same collapse and representative ordering as GetPairs: coverage is per form, but the list shows one row
        // per base→derived pair, so a mark on any collapsed form has to land on the form that row displays.
        var repRedundant = new HashSet<long>();
        var repAdded = new HashSet<long>();
        var repStudied = new HashSet<long>();

        foreach (var pair in rows.GroupBy(r => (r.BaseWordId, r.DerivedWordId, r.Category)))
        {
            var rep = pair.OrderBy(r => r.BaseReadingIndex).ThenBy(r => r.DerivedReadingIndex).First();
            var repBase = WordFormHelper.EncodeWordKey(rep.BaseWordId, rep.BaseReadingIndex);
            var repDerived = WordFormHelper.EncodeWordKey(rep.DerivedWordId, rep.DerivedReadingIndex);

            var derivedKeys = pair.Select(r => WordFormHelper.EncodeWordKey(r.DerivedWordId, r.DerivedReadingIndex))
                                  .ToList();

            if (derivedKeys.Any(redundantKeys.Contains)) repRedundant.Add(repDerived);
            if (derivedKeys.Any(addedByGroup.Contains)) repAdded.Add(repDerived);
            if (derivedKeys.Any(conductorKeys.Contains)) repStudied.Add(repDerived);
            if (pair.Any(r => conductorKeys.Contains(WordFormHelper.EncodeWordKey(r.BaseWordId, r.BaseReadingIndex))))
                repStudied.Add(repBase);
        }

        return Results.Ok(new DerivationPersonalPairsDto
        {
            RedundantKeys = repRedundant.ToList(),
            AddedByGroupKeys = repAdded.ToList(),
            StudiedKeys = repStudied.ToList()
        });
    }

    [HttpGet("pairs")]
    [ResponseCache(Duration = 3600, VaryByQueryKeys = ["group"])]
    [SwaggerOperation(Summary = "List every derivation pair in a checkbox group",
                      Description = "The full base→derived mapping list behind a settings checkbox, ordered by " +
                                    "the derived form's frequency rank.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> GetPairs(string group)
    {
        var categories = DerivationCategories.Shipped.Where(c => c.Group == group).ToList();
        if (categories.Count == 0)
            return Results.NotFound();

        var categoryValues = categories.Select(c => c.Category).ToList();
        var rows = await context.WordDerivations
                                .AsNoTracking()
                                .Where(d => categoryValues.Contains(d.Category))
                                .ToListAsync();

        // Form closure emits one row per matched form pair; the lowest indexes carry the primary display forms.
        var pairs = rows.GroupBy(r => (r.BaseWordId, r.DerivedWordId, r.Category))
                        .Select(g => g.OrderBy(r => r.BaseReadingIndex).ThenBy(r => r.DerivedReadingIndex).First())
                        .ToList();

        var wordIds = pairs.Select(p => p.BaseWordId).Concat(pairs.Select(p => p.DerivedWordId)).Distinct().ToList();
        var presentation = await WordFormHelper.LoadWordPresentation(context, wordIds);
        var labels = categories.ToDictionary(c => c.Category, c => c.Label);

        var result = pairs.Select(p => new DerivationPairDto
                          {
                              BaseWordId = p.BaseWordId,
                              BaseReadingIndex = p.BaseReadingIndex,
                              BaseText = presentation.FormText(p.BaseWordId, p.BaseReadingIndex),
                              BaseDefinition = presentation.Definition(p.BaseWordId),
                              DerivedWordId = p.DerivedWordId,
                              DerivedReadingIndex = p.DerivedReadingIndex,
                              DerivedText = presentation.FormText(p.DerivedWordId, p.DerivedReadingIndex),
                              DerivedDefinition = presentation.Definition(p.DerivedWordId),
                              FrequencyRank = presentation.FrequencyRank(p.DerivedWordId, p.DerivedReadingIndex),
                              CategoryLabel = labels.GetValueOrDefault(p.Category, ""),
                              Bidirectional = p.Direction == DerivationDirection.Bidirectional
                          })
                          .Where(dto => dto.BaseText.Length > 0 && dto.DerivedText.Length > 0)
                          .OrderBy(dto => dto.FrequencyRank == 0 ? int.MaxValue : dto.FrequencyRank)
                          .ThenBy(dto => dto.DerivedWordId)
                          .ToList();

        return Results.Ok(result);
    }
}
