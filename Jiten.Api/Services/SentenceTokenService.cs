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

public interface ISentenceTokenService
{
    /// <summary>Ruby groups per sentence; sentences without token spans are absent from the result.</summary>
    Task<Dictionary<long, List<FuriganaGroup>>> BuildFuriganaAsync(IEnumerable<(long SentenceId, string Text, byte[]? Tokens)> sentences);

    /// <summary><see cref="BuildFuriganaAsync"/> with each group's word judged against the current user's known words.</summary>
    Task<Dictionary<long, List<SentenceFuriganaDto>>> BuildFuriganaDtosAsync(IEnumerable<(long SentenceId, string Text, byte[]? Tokens)> sentences);

    /// <summary>Sentences with every other content word known to the current user, from a random sample of fully parsed sentences.</summary>
    Task<List<IPlusOneSentence>> FindIPlusOneAsync(int wordId, byte readingIndex, int take, int maxUnknown = 0);
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

    public async Task<Dictionary<long, List<SentenceFuriganaDto>>> BuildFuriganaDtosAsync(
        IEnumerable<(long SentenceId, string Text, byte[]? Tokens)> sentences)
    {
        var groups = await BuildFuriganaAsync(sentences);
        if (groups.Count == 0) return [];

        var forms = groups.Values.SelectMany(g => g).Select(g => (g.WordId, g.ReadingIndex)).ToHashSet();
        var states = currentUser.IsAuthenticated ? await currentUser.GetKnownWordsState(forms) : [];

        return groups.ToDictionary(s => s.Key, s => s.Value.Select(g => new SentenceFuriganaDto
        {
            Position = g.Position, Length = g.Length, Reading = g.Reading, WordId = g.WordId,
            Known = states.TryGetValue((g.WordId, g.ReadingIndex), out var state) && SentenceComprehension.IsKnown(state)
        }).ToList());
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

        // A partial row lists only its picked words, so every other word in it would wrongly read as known
        var decoded = candidates.Where(c => !ExampleSentenceTokens.IsPartial(c.Tokens))
                                .Select(c => (Sentence: c, Tokens: ExampleSentenceTokens.Decode(c.Tokens)))
                                .ToList();

        var contentForms = decoded.SelectMany(d => d.Tokens)
                                  .Where(t => !t.IsFunctionWord && t.WordId != wordId)
                                  .Select(t => (t.WordId, t.ReadingIndex))
                                  .ToHashSet();
        var states = await currentUser.GetKnownWordsState(contentForms);

        bool IsKnown(int id, byte ri) => states.TryGetValue((id, ri), out var s) && SentenceComprehension.IsKnown(s);

        var picked = decoded.Select(d => (d.Sentence, d.Tokens, Unknown: SentenceComprehension.CountUnknown(d.Tokens, wordId, IsKnown)))
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
}
