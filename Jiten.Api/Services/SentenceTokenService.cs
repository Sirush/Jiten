using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public record IPlusOneSentence(
    long SentenceId,
    int DeckId,
    string Text,
    float Difficulty,
    int UnknownCount,
    List<FuriganaGroup> Furigana);

/// <summary>Ruby groups and, for a signed-in caller, every word span to colour by state.</summary>
public record SentenceAnnotations(List<SentenceFuriganaDto> Furigana, List<SentenceWordDto>? Words);

public interface ISentenceTokenService
{
    /// <summary>Ruby groups per sentence; sentences without token spans are absent from the result.</summary>
    Task<Dictionary<long, List<FuriganaGroup>>> BuildFuriganaAsync(IEnumerable<(long SentenceId, string Text, byte[]? Tokens)> sentences);

    /// <summary><see cref="BuildFuriganaAsync"/> with each group's word judged against the current user's known words, plus every word span to colour.</summary>
    Task<Dictionary<long, SentenceAnnotations>> BuildFuriganaDtosAsync(IEnumerable<(long SentenceId, string Text, byte[]? Tokens)> sentences);

    /// <summary>Sentences with every other content word known to the current user, from a random sample of fully parsed sentences.</summary>
    Task<List<IPlusOneSentence>> FindIPlusOneAsync(int wordId, byte readingIndex, int take, int maxUnknown = 0);

    /// <summary>Unknown content words of each (sentence, target word) pair for the current user; partial rows are absent.</summary>
    Task<Dictionary<(long SentenceId, int TargetWordId), int>> CountUnknownAsync(
        IEnumerable<(long SentenceId, int TargetWordId, byte[] Tokens)> candidates);

    /// <summary>Every occurrence of an unknown content word in each (sentence, target word) pair for the current user; partial rows are absent.</summary>
    Task<Dictionary<(long SentenceId, int TargetWordId), List<SentenceToken>>> UnknownTokensAsync(
        IEnumerable<(long SentenceId, int TargetWordId, byte[] Tokens)> candidates);
}

public class SentenceTokenService(JitenDbContext context, ICurrentUserService currentUser) : ISentenceTokenService
{
    private const int CandidateCap = 500;

    public async Task<Dictionary<long, List<FuriganaGroup>>> BuildFuriganaAsync(
        IEnumerable<(long SentenceId, string Text, byte[]? Tokens)> sentences)
    {
        var decoded = sentences.Where(s => s.Tokens != null)
                               .Select(s => (s.SentenceId, s.Text, Tokens: ExampleSentenceTokens.Decode(s.Tokens!)))
                               .ToList();
        if (decoded.Count == 0) return [];

        var wordIds = decoded.SelectMany(s => s.Tokens).Select(t => t.WordId).Distinct().ToList();
        var forms = await WordFormHelper.LoadWordForms(context, wordIds);

        return decoded.ToDictionary(s => s.SentenceId, s => SentenceFurigana.Build(s.Text, s.Tokens, forms));
    }

    public async Task<Dictionary<long, SentenceAnnotations>> BuildFuriganaDtosAsync(
        IEnumerable<(long SentenceId, string Text, byte[]? Tokens)> sentences)
    {
        var list = sentences.ToList();
        var groups = await BuildFuriganaAsync(list);
        if (groups.Count == 0) return [];

        var authenticated = currentUser.IsAuthenticated;
        var wordTokens = authenticated
            ? list.Where(s => s.Tokens != null && groups.ContainsKey(s.SentenceId))
                  .ToDictionary(s => s.SentenceId, s => ExampleSentenceTokens.Decode(s.Tokens!))
            : [];

        var forms = groups.Values.SelectMany(g => g).Select(g => (g.WordId, g.ReadingIndex))
                          .Concat(wordTokens.Values.SelectMany(t => t).Select(t => (t.WordId, t.ReadingIndex)))
                          .ToHashSet();
        var states = authenticated ? await currentUser.GetKnownWordsState(forms) : [];

        return groups.ToDictionary(s => s.Key, s => new SentenceAnnotations(
            s.Value.Select(g =>
            {
                var state = states.GetValueOrDefault((g.WordId, g.ReadingIndex));
                return new SentenceFuriganaDto
                {
                    Position = g.Position, Length = g.Length, Reading = g.Reading, WordId = g.WordId,
                    Known = state != null && SentenceComprehension.IsKnown(state),
                    States = authenticated ? state ?? [] : null,
                };
            }).ToList(),
            wordTokens.TryGetValue(s.Key, out var tokens)
                ? tokens.Select(t => new SentenceWordDto
                {
                    Position = t.Position, Length = t.Length, WordId = t.WordId,
                    States = states.GetValueOrDefault((t.WordId, t.ReadingIndex)) ?? [],
                }).ToList()
                : null));
    }

    public async Task<List<IPlusOneSentence>> FindIPlusOneAsync(int wordId, byte readingIndex, int take, int maxUnknown = 0)
    {
        if (!currentUser.IsAuthenticated) return [];

        var key = ExampleSentenceTokens.WordKey(wordId, readingIndex);
        var sampled = (await SentenceSampler.SampleAsync(context, [key], CandidateCap)).GetValueOrDefault(key, []);
        var candidates = await context.ExampleSentences
            .AsNoTracking()
            .Where(s => sampled.Contains(s.SentenceId))
            .Select(s => new { s.SentenceId, s.DeckId, s.Text, s.Difficulty, s.Tokens })
            .ToListAsync();

        var unknownCounts = await CountUnknownAsync(candidates.Select(c => (c.SentenceId, wordId, c.Tokens)));

        var picked = candidates.Where(c => unknownCounts.ContainsKey((c.SentenceId, wordId)))
                               .Select(c => (Sentence: c, Unknown: unknownCounts[(c.SentenceId, wordId)]))
                               .Where(d => d.Unknown <= maxUnknown)
                               .OrderBy(d => d.Unknown)
                               .ThenBy(_ => Random.Shared.Next())
                               .Take(take)
                               .ToList();

        var furigana = await BuildFuriganaAsync(picked.Select(p => (p.Sentence.SentenceId, p.Sentence.Text, (byte[]?)p.Sentence.Tokens)));

        return picked.Select(p => new IPlusOneSentence(p.Sentence.SentenceId, p.Sentence.DeckId, p.Sentence.Text,
                                                       p.Sentence.Difficulty, p.Unknown,
                                                       furigana.GetValueOrDefault(p.Sentence.SentenceId, [])))
                     .ToList();
    }

    public async Task<Dictionary<(long SentenceId, int TargetWordId), int>> CountUnknownAsync(
        IEnumerable<(long SentenceId, int TargetWordId, byte[] Tokens)> candidates) =>
        (await UnknownTokensAsync(candidates)).ToDictionary(kv => kv.Key, kv => kv.Value.Select(t => t.WordKey).Distinct().Count());

    public async Task<Dictionary<(long SentenceId, int TargetWordId), List<SentenceToken>>> UnknownTokensAsync(
        IEnumerable<(long SentenceId, int TargetWordId, byte[] Tokens)> candidates)
    {
        if (!currentUser.IsAuthenticated) return [];

        var decoded = candidates.Where(c => !ExampleSentenceTokens.IsPartial(c.Tokens))
                                .Select(c => (c.SentenceId, c.TargetWordId, Tokens: ExampleSentenceTokens.Decode(c.Tokens)))
                                .ToList();
        if (decoded.Count == 0) return [];

        var contentForms = decoded.SelectMany(d => d.Tokens)
                                  .Where(t => !t.IsFunctionWord)
                                  .Select(t => (t.WordId, t.ReadingIndex))
                                  .ToHashSet();
        var states = await currentUser.GetKnownWordsState(contentForms);

        bool IsKnown(int id, byte ri) => states.TryGetValue((id, ri), out var s) && SentenceComprehension.IsKnown(s);

        var unknown = new Dictionary<(long, int), List<SentenceToken>>();
        foreach (var (sentenceId, targetWordId, tokens) in decoded)
            unknown.TryAdd((sentenceId, targetWordId), SentenceComprehension.UnknownTokens(tokens, targetWordId, IsKnown).ToList());
        return unknown;
    }
}
