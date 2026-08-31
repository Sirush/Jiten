using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public interface IExampleSentenceQueryService
{
    /// <summary>Random example sentences for a form, one per source deck. Sentences from priorityDeckIds are picked first.</summary>
    Task<List<ExampleSentenceDto>> GetRandomAsync(int wordId, int readingIndex, List<int> excludedDeckIds, MediaType? mediaType,
                                                  int take, int[]? priorityDeckIds = null);

    /// <summary>Example sentences for a form walking outward from a difficulty band. Within each band, priorityDeckIds win.</summary>
    Task<ExampleSentencesByDifficultyResponse> GetByDifficultyAsync(int wordId, int readingIndex, List<int> excludedDeckIds,
                                                                    MediaType? mediaType, float minDifficulty, float maxDifficulty,
                                                                    bool descending, int take, int[]? priorityDeckIds = null);
}

public class ExampleSentenceQueryService(JitenDbContext context) : IExampleSentenceQueryService
{
    private const float BandSize = 0.5f;

    private record PickedSentence(long SentenceId, string Text, float Difficulty, int DeckId, int? ParentDeckId, bool FromStudyDeck);

    public async Task<List<ExampleSentenceDto>> GetRandomAsync(int wordId, int readingIndex, List<int> excludedDeckIds,
                                                               MediaType? mediaType, int take, int[]? priorityDeckIds = null)
    {
        priorityDeckIds = await NarrowToDecksHoldingWord(wordId, readingIndex, priorityDeckIds);

        var picked = new List<PickedSentence>();

        if (priorityDeckIds is { Length: > 0 })
        {
            var sentenceIdSubquery = SentenceIdsFor(wordId, readingIndex);
            picked = await PickRandomSentences(
                context.ExampleSentences.AsNoTracking()
                       .Where(s => sentenceIdSubquery.Contains(s.SentenceId) && priorityDeckIds.Contains(s.DeckId)),
                excludedDeckIds, mediaType, take, fromStudyDeck: true);
        }

        if (picked.Count < take)
        {
            var remaining = take - picked.Count;
            var excluded = excludedDeckIds.Concat(picked.Select(p => p.DeckId)).Distinct().ToList();

            // Sample candidate ids first so ORDER BY random() never sorts the full sentence set of a common word
            const int sampleSize = 200;
            var candidateIds = await context.ExampleSentenceWords
                .Where(w => w.WordId == wordId && w.ReadingIndex == readingIndex)
                .OrderBy(_ => EF.Functions.Random())
                .Take(sampleSize)
                .Select(w => w.ExampleSentenceId)
                .ToListAsync();

            var topUp = await PickRandomSentences(
                context.ExampleSentences.AsNoTracking().Where(s => candidateIds.Contains(s.SentenceId)),
                excluded, mediaType, remaining, fromStudyDeck: false);

            // A truncated sample can miss all eligible sentences under heavy filtering; fall back to the full set
            if (topUp.Count < remaining && candidateIds.Count == sampleSize)
            {
                var sentenceIdSubquery = SentenceIdsFor(wordId, readingIndex);
                topUp = await PickRandomSentences(
                    context.ExampleSentences.AsNoTracking().Where(s => sentenceIdSubquery.Contains(s.SentenceId)),
                    excluded, mediaType, remaining, fromStudyDeck: false);
            }

            picked.AddRange(topUp);
        }

        if (picked.Count == 0) return [];

        return await BuildExampleSentenceDtos(picked, wordId, readingIndex);
    }

    public async Task<ExampleSentencesByDifficultyResponse> GetByDifficultyAsync(int wordId, int readingIndex, List<int> excludedDeckIds,
                                                                                 MediaType? mediaType, float minDifficulty,
                                                                                 float maxDifficulty, bool descending, int take,
                                                                                 int[]? priorityDeckIds = null)
    {
        take = Math.Clamp(take, 1, 20);
        const int maxIterations = 40;

        priorityDeckIds = await NarrowToDecksHoldingWord(wordId, readingIndex, priorityDeckIds);

        var sentenceIdSubquery = SentenceIdsFor(wordId, readingIndex);

        var baseSentences = context.ExampleSentences
                                   .AsNoTracking()
                                   .Where(s => sentenceIdSubquery.Contains(s.SentenceId));

        var difficultyStats = await baseSentences
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(s => s.Difficulty), Max = g.Max(s => s.Difficulty) })
            .FirstOrDefaultAsync();
        var globalMin = difficultyStats?.Min ?? 0f;
        var globalMax = difficultyStats?.Max ?? 0f;

        var collected = new List<PickedSentence>();
        var bandMin = minDifficulty;
        var bandMax = maxDifficulty;

        if (descending && bandMin > globalMax)
        {
            bandMax = (float)(Math.Ceiling(globalMax / BandSize) * BandSize + BandSize);
            bandMin = bandMax - BandSize;
        }

        if (!descending && bandMax <= globalMin)
        {
            bandMin = Math.Max(bandMin, (float)(Math.Floor(globalMin / BandSize) * BandSize));
            bandMax = bandMin + BandSize;
        }

        for (var i = 0; i < maxIterations && collected.Count < take; i++)
        {
            if (descending ? bandMax < globalMin : bandMin > globalMax + BandSize)
                break;

            var remaining = take - collected.Count;
            var excludeIds = excludedDeckIds.Concat(collected.Select(c => c.DeckId)).Distinct().ToList();
            var band = baseSentences.Where(s => s.Difficulty >= bandMin && s.Difficulty < bandMax);

            var batch = new List<PickedSentence>();
            if (priorityDeckIds is { Length: > 0 })
            {
                batch = await PickRandomSentences(band.Where(s => priorityDeckIds.Contains(s.DeckId)),
                                                  excludeIds, mediaType, remaining, fromStudyDeck: true);
            }

            if (batch.Count < remaining)
            {
                var topUpExcluded = excludeIds.Concat(batch.Select(b => b.DeckId)).Distinct().ToList();
                batch.AddRange(await PickRandomSentences(band, topUpExcluded, mediaType, remaining - batch.Count, fromStudyDeck: false));
            }

            collected.AddRange(batch);

            if (batch.Count == 0)
            {
                // Empty band: jump straight to the band containing the nearest sentence instead of stepping through the gap
                var next = descending
                    ? await baseSentences.Where(s => s.Difficulty < bandMin).Select(s => (float?)s.Difficulty).MaxAsync()
                    : await baseSentences.Where(s => s.Difficulty >= bandMax).Select(s => (float?)s.Difficulty).MinAsync();

                if (next == null)
                {
                    // Range exhausted; leave the band cursor past the global bounds so the client stops paging
                    if (descending)
                    {
                        bandMax = globalMin - BandSize;
                        bandMin = bandMax - BandSize;
                    }
                    else
                    {
                        bandMin = globalMax + BandSize * 2;
                        bandMax = bandMin + BandSize;
                    }

                    break;
                }

                var bandStart = (float)(Math.Floor(next.Value / BandSize) * BandSize);
                if (descending)
                {
                    bandMax = Math.Min(bandStart + BandSize, bandMin);
                    bandMin = bandMax - BandSize;
                }
                else
                {
                    bandMin = Math.Max(bandStart, bandMax);
                    bandMax = bandMin + BandSize;
                }

                continue;
            }

            if (descending)
            {
                bandMax = bandMin;
                bandMin -= BandSize;
            }
            else
            {
                bandMin = bandMax;
                bandMax += BandSize;
            }
        }

        return new ExampleSentencesByDifficultyResponse
        {
            MinDifficulty = globalMin,
            MaxDifficulty = globalMax,
            SearchedBandMin = minDifficulty,
            SearchedBandMax = descending ? bandMax + BandSize : bandMin,
            Sentences = collected.Count == 0 ? [] : await BuildExampleSentenceDtos(collected, wordId, readingIndex)
        };
    }

    /// <summary>
    /// Drops priority decks that do not contain the word at all. The DeckWords lookup is one index-only
    /// descent per deck; the sentence query it guards probes ExampleSentences once per candidate sentence,
    /// so for the common "none of my decks has this word" case this turns milliseconds into microseconds.
    /// </summary>
    private async Task<int[]> NarrowToDecksHoldingWord(int wordId, int readingIndex, int[]? priorityDeckIds)
    {
        if (priorityDeckIds is not { Length: > 0 }) return [];

        var ri = (byte)readingIndex;
        return await context.DeckWords
                            .AsNoTracking()
                            .Where(dw => dw.WordId == wordId && dw.ReadingIndex == ri && priorityDeckIds.Contains(dw.DeckId))
                            .Select(dw => dw.DeckId)
                            .Distinct()
                            .ToArrayAsync();
    }

    private IQueryable<long> SentenceIdsFor(int wordId, int readingIndex)
        => context.ExampleSentenceWords
                  .Where(w => w.WordId == wordId && w.ReadingIndex == readingIndex)
                  .Select(w => w.ExampleSentenceId)
                  .Distinct();

    private Task<List<PickedSentence>> PickRandomSentences(IQueryable<ExampleSentence> sentences, List<int> excludedDeckIds,
                                                           MediaType? mediaType, int take, bool fromStudyDeck)
    {
        if (take <= 0) return Task.FromResult(new List<PickedSentence>());

        return sentences
               .Join(context.Decks.AsNoTracking(),
                     s => s.DeckId, d => d.DeckId,
                     (s, d) => new { Sentence = s, Deck = d })
               .Where(j => !mediaType.HasValue || j.Deck.MediaType == mediaType.Value)
               .Where(j => !excludedDeckIds.Contains(j.Deck.DeckId)
                           && (!j.Deck.ParentDeckId.HasValue || !excludedDeckIds.Contains(j.Deck.ParentDeckId.Value)))
               .OrderBy(_ => EF.Functions.Random())
               .Take(take)
               .Select(j => new PickedSentence(
                           j.Sentence.SentenceId, j.Sentence.Text, j.Sentence.Difficulty,
                           j.Deck.DeckId, j.Deck.ParentDeckId, fromStudyDeck))
               .ToListAsync();
    }

    private async Task<List<ExampleSentenceDto>> BuildExampleSentenceDtos(List<PickedSentence> picked, int wordId, int readingIndex)
    {
        var selectedIds = picked.Select(p => p.SentenceId).ToList();
        var positionMap = (await context.ExampleSentenceWords
                              .AsNoTracking()
                              .Where(w => w.WordId == wordId && w.ReadingIndex == readingIndex
                                          && selectedIds.Contains(w.ExampleSentenceId))
                              .Select(w => new { w.ExampleSentenceId, w.Position, w.Length })
                              .ToListAsync())
            .DistinctBy(w => w.ExampleSentenceId)
            .ToDictionary(w => w.ExampleSentenceId);

        var childDeckIds = picked.Select(p => p.DeckId).Distinct().ToList();
        var childDecks = await context.Decks.AsNoTracking()
                                      .Where(d => childDeckIds.Contains(d.DeckId))
                                      .ToDictionaryAsync(d => d.DeckId, d => d);

        var parentIds = picked.Where(p => p.ParentDeckId.HasValue).Select(p => p.ParentDeckId!.Value).Distinct().ToList();
        var parentDecks = parentIds.Count > 0
            ? await context.Decks.AsNoTracking()
                          .Where(d => parentIds.Contains(d.DeckId))
                          .ToDictionaryAsync(d => d.DeckId, d => d)
            : new Dictionary<int, Deck>();

        return picked.Select(p =>
        {
            positionMap.TryGetValue(p.SentenceId, out var pos);
            childDecks.TryGetValue(p.DeckId, out var sourceDeck);
            Deck? parentDeck = null;
            if (p.ParentDeckId.HasValue)
                parentDecks.TryGetValue(p.ParentDeckId.Value, out parentDeck);

            return new ExampleSentenceDto
            {
                SentenceId = p.SentenceId,
                Text = p.Text,
                Difficulty = p.Difficulty,
                WordPosition = pos?.Position ?? 0,
                WordLength = pos?.Length ?? 0,
                SourceDeck = sourceDeck!,
                SourceDeckParent = parentDeck,
                FromStudyDeck = p.FromStudyDeck
            };
        }).ToList();
    }
}
