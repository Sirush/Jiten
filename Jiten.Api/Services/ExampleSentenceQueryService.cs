using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public interface IExampleSentenceQueryService
{
    /// <summary>Random example sentences for a form, one per title (subdecks collapse to their parent). Sentences from priorityDeckIds are picked first.</summary>
    Task<List<ExampleSentenceDto>> GetRandomAsync(int wordId, int readingIndex, List<int> excludedDeckIds, MediaType? mediaType,
                                                  int take, int[]? priorityDeckIds = null);

    /// <summary>Example sentences for a form walking outward from a difficulty band. Within each band, priorityDeckIds win.</summary>
    Task<ExampleSentencesByDifficultyResponse> GetByDifficultyAsync(int wordId, int readingIndex, List<int> excludedDeckIds,
                                                                    MediaType? mediaType, float minDifficulty, float maxDifficulty,
                                                                    bool descending, int take, int[]? priorityDeckIds = null);
}

public class ExampleSentenceQueryService(JitenDbContext context, ISentenceTokenService sentenceTokens) : IExampleSentenceQueryService
{
    private const float BandSize = 0.5f;

    /// <summary>Candidate pool standing in for a word's full sentence set, which for a common word is too large to sort.</summary>
    private const int WideSampleSize = 500;

    private record PickedSentence(long SentenceId, string Text, float Difficulty, int DeckId, int? ParentDeckId, bool FromStudyDeck,
                                  byte[] Tokens);

    public async Task<List<ExampleSentenceDto>> GetRandomAsync(int wordId, int readingIndex, List<int> excludedDeckIds,
                                                               MediaType? mediaType, int take, int[]? priorityDeckIds = null)
    {
        priorityDeckIds = await NarrowToDecksHoldingWord(wordId, readingIndex, priorityDeckIds, excludedDeckIds);

        var picked = new List<PickedSentence>();

        if (priorityDeckIds is { Length: > 0 })
        {
            picked = await PickRandomSentences(
                SentencesWithForm(wordId, readingIndex).Where(s => priorityDeckIds.Contains(s.DeckId)),
                excludedDeckIds, mediaType, take, fromStudyDeck: true);
        }

        if (picked.Count < take)
        {
            var remaining = take - picked.Count;
            var excluded = excludedDeckIds.Concat(picked.Select(p => p.ParentDeckId ?? p.DeckId)).Distinct().ToList();

            // Sample candidate ids first so ORDER BY random() never sorts the full sentence set of a common word
            const int sampleSize = 200;
            var candidateIds = await SampleSentenceIds(wordId, readingIndex, sampleSize);

            var topUp = await PickRandomSentences(
                context.ExampleSentences.AsNoTracking().Where(s => candidateIds.Contains(s.SentenceId)),
                excluded, mediaType, remaining, fromStudyDeck: false);

            // A truncated sample can miss every eligible sentence under heavy filtering; retry on a wider one
            if (topUp.Count < remaining && candidateIds.Count == sampleSize)
            {
                topUp = await PickRandomSentences(SentencesById(await SampleSentenceIds(wordId, readingIndex, WideSampleSize)),
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

        priorityDeckIds = await NarrowToDecksHoldingWord(wordId, readingIndex, priorityDeckIds, excludedDeckIds);

        // A common word's full set is millions of rows, so bands walk a wide sample; the deck filter keeps study decks exact.
        var baseSentences = SentencesById(await SampleSentenceIds(wordId, readingIndex, WideSampleSize));
        var priorityPool = SentencesWithForm(wordId, readingIndex);

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
            var excludeIds = excludedDeckIds.Concat(collected.Select(c => c.ParentDeckId ?? c.DeckId)).Distinct().ToList();
            var band = baseSentences.Where(s => s.Difficulty >= bandMin && s.Difficulty < bandMax);

            var batch = new List<PickedSentence>();
            if (priorityDeckIds is { Length: > 0 })
            {
                batch = await PickRandomSentences(
                    priorityPool.Where(s => s.Difficulty >= bandMin && s.Difficulty < bandMax && priorityDeckIds.Contains(s.DeckId)),
                    excludeIds, mediaType, remaining, fromStudyDeck: true);
            }

            if (batch.Count < remaining)
            {
                var topUpExcluded = excludeIds.Concat(batch.Select(b => b.ParentDeckId ?? b.DeckId)).Distinct().ToList();
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
            SearchedBandMin = descending ? bandMax : minDifficulty,
            SearchedBandMax = descending ? maxDifficulty : bandMin,
            Sentences = collected.Count == 0 ? [] : await BuildExampleSentenceDtos(collected, wordId, readingIndex)
        };
    }

    /// <summary>
    /// Drops priority decks that do not contain the word at all. The DeckWords lookup is one index-only
    /// descent per deck; the sentence query it guards probes ExampleSentences once per candidate sentence,
    /// so for the common "none of my decks has this word" case this turns milliseconds into microseconds.
    /// </summary>
    private async Task<int[]> NarrowToDecksHoldingWord(int wordId, int readingIndex, int[]? priorityDeckIds, List<int> excludedDeckIds)
    {
        if (priorityDeckIds is not { Length: > 0 }) return [];

        // Later pages already exclude the decks shown earlier; once every priority deck is spent the query is skipped
        var candidates = priorityDeckIds.Except(excludedDeckIds).ToArray();
        if (candidates.Length == 0) return [];

        var ri = (byte)readingIndex;
        return await context.DeckWords
                            .AsNoTracking()
                            .Where(dw => dw.WordId == wordId && dw.ReadingIndex == ri && candidates.Contains(dw.DeckId))
                            .Select(dw => dw.DeckId)
                            .Distinct()
                            .ToArrayAsync();
    }

    private IQueryable<ExampleSentence> SentencesWithForm(int wordId, int readingIndex)
    {
        var key = ExampleSentenceTokens.WordKey(wordId, (byte)readingIndex);
        return context.ExampleSentences.AsNoTracking().Where(s => s.WordKeys.Contains(key));
    }

    private IQueryable<ExampleSentence> SentencesById(List<long> ids) =>
        context.ExampleSentences.AsNoTracking().Where(s => ids.Contains(s.SentenceId));

    private async Task<List<long>> SampleSentenceIds(int wordId, int readingIndex, int size)
    {
        var key = ExampleSentenceTokens.WordKey(wordId, (byte)readingIndex);
        return (await SentenceSampler.SampleAsync(context, [key], size)).GetValueOrDefault(key, []);
    }

    /// <summary>Random candidates drawn per requested slot; enough to fill a page after collapsing same-title picks.</summary>
    private const int OversampleFactor = 4;

    private async Task<List<PickedSentence>> PickRandomSentences(IQueryable<ExampleSentence> sentences, List<int> excludedDeckIds,
                                                                 MediaType? mediaType, int take, bool fromStudyDeck)
    {
        if (take <= 0) return [];

        var picked = await sentences
               .Join(context.Decks.AsNoTracking(),
                     s => s.DeckId, d => d.DeckId,
                     (s, d) => new { Sentence = s, Deck = d })
               .Where(j => !mediaType.HasValue || j.Deck.MediaType == mediaType.Value)
               .Where(j => !excludedDeckIds.Contains(j.Deck.DeckId)
                           && (!j.Deck.ParentDeckId.HasValue || !excludedDeckIds.Contains(j.Deck.ParentDeckId.Value)))
               .OrderBy(_ => EF.Functions.Random())
               .Take(take * OversampleFactor)
               .Select(j => new PickedSentence(
                           j.Sentence.SentenceId, j.Sentence.Text, j.Sentence.Difficulty,
                           j.Deck.DeckId, j.Deck.ParentDeckId, fromStudyDeck, j.Sentence.Tokens))
               .ToListAsync();

        return picked.DistinctBy(p => p.ParentDeckId ?? p.DeckId).Take(take).ToList();
    }

    private async Task<List<ExampleSentenceDto>> BuildExampleSentenceDtos(List<PickedSentence> picked, int wordId, int readingIndex)
    {
        var positionMap = new Dictionary<long, (byte Position, byte Length)>();
        foreach (var p in picked)
        {
            if (ExampleSentenceTokens.FindForm(ExampleSentenceTokens.Decode(p.Tokens), wordId, (byte)readingIndex) is { } token)
                positionMap[p.SentenceId] = (token.Position, token.Length);
        }

        var deckIds = picked.Select(p => p.DeckId)
                            .Concat(picked.Where(p => p.ParentDeckId.HasValue).Select(p => p.ParentDeckId!.Value))
                            .Distinct()
                            .ToList();
        var decks = await context.Decks.AsNoTracking()
                                 .Where(d => deckIds.Contains(d.DeckId))
                                 .Select(d => new StudyExampleSourceDto
                                 {
                                     DeckId = d.DeckId,
                                     OriginalTitle = d.OriginalTitle,
                                     RomajiTitle = d.RomajiTitle,
                                     EnglishTitle = d.EnglishTitle,
                                     MediaType = (int)d.MediaType
                                 })
                                 .ToDictionaryAsync(d => d.DeckId);

        var furigana = await sentenceTokens.BuildFuriganaDtosAsync(picked.Select(p => (p.SentenceId, p.Text, (byte[]?)p.Tokens)));

        return picked.Select(p =>
        {
            var hasPosition = positionMap.TryGetValue(p.SentenceId, out var pos);
            decks.TryGetValue(p.DeckId, out var sourceDeck);
            StudyExampleSourceDto? parentDeck = null;
            if (p.ParentDeckId.HasValue)
                decks.TryGetValue(p.ParentDeckId.Value, out parentDeck);

            return new ExampleSentenceDto
            {
                SentenceId = p.SentenceId,
                Text = p.Text,
                Difficulty = p.Difficulty,
                WordPosition = hasPosition ? pos.Position : 0,
                WordLength = hasPosition ? pos.Length : 0,
                SourceDeck = sourceDeck,
                SourceDeckParent = parentDeck,
                FromStudyDeck = p.FromStudyDeck,
                Furigana = furigana.GetValueOrDefault(p.SentenceId)
            };
        }).ToList();
    }
}
