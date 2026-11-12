using Jiten.Api.Services.ReadingCheck;
using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Services.WordCheck;

/// <summary><paramref name="Reading"/> and <paramref name="Meanings"/> are shown only after the answer; both are null for a word that doesn't exist.</summary>
public sealed record WordCheckItemDto(int Id, string Text, string? Reading = null, List<string>? Meanings = null);

public sealed record WordCheckStepDto(string SessionId, string Phase, List<WordCheckItemDto> Items, int Answered, int EstimatedTotal);

public sealed record WordCheckAnswerDto(int Id, WordCheckAnswer Answer);

/// <summary><paramref name="WordsKnown"/> counts every word in the measured range; <paramref name="WordsMarked"/> only those the account had no card for.</summary>
public sealed record WordCheckResultDto(int WordsKnown, int WordsMarked, Guid? BatchId);

public interface IWordCheckService
{
    Task<WordCheckStepDto> StartAsync(string userId, WordCheckLevel? level);
    Task<WordCheckStepDto> AnswerAsync(string userId, string sessionId, IReadOnlyList<WordCheckAnswerDto> answers);
    Task<WordCheckResultDto> ConfirmAsync(string userId, string sessionId, IReadOnlyCollection<int> unknownIds);
}

public sealed class WordCheckService(
    IWordCheckPool pool,
    IReadingCheckData words,
    CheckSessionStore sessions,
    ICardBatchService cardBatches,
    IOnboardingStore onboarding,
    ILogger<WordCheckService> logger) : IWordCheckService
{
    private const string SessionPrefix = "word-check";

    public async Task<WordCheckStepDto> StartAsync(string userId, WordCheckLevel? level)
    {
        var data = await pool.GetAsync();
        var session = new WordCheckSession
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Level = level,
            Phase = WordCheckPhase.Probe
        };

        AddRound(session, data, WordCheckEngine.FirstWindow(level));
        return await SaveStep(session);
    }

    public async Task<WordCheckStepDto> AnswerAsync(string userId, string sessionId, IReadOnlyList<WordCheckAnswerDto> answers)
    {
        var session = await Load(userId, sessionId);
        if (session.Phase != WordCheckPhase.Probe) throw new CheckException("This word check is past the word stage.");

        var pending = session.Pending.ToDictionary(i => i.Id);
        foreach (var answer in answers)
        {
            if (pending.TryGetValue(answer.Id, out var item))
                item.Answer = answer.Answer;
        }

        if (session.Pending.Any()) throw new CheckException("Answer every word before continuing.");

        var (trapsShown, trapsClaimed) = session.Traps();
        if (trapsClaimed >= WordCheckEngine.SoftFailTrapHits)
        {
            session.Phase = WordCheckPhase.Failed;
            LogOutcome(session, 0, 0);
            return await SaveStep(session);
        }

        var data = await pool.GetAsync();
        session.Cutoff = WordCheckEstimator.Cutoff(session.Observations(), WordCheckEngine.FalseAlarmRate(trapsShown, trapsClaimed));
        if (session.RoundsAsked < WordCheckEngine.Rounds)
        {
            AddRound(session, data, WordCheckEngine.NextWindow(session.Cutoff));
            return await SaveStep(session);
        }

        session.Phase = WordCheckPhase.Verify;
        if (session.Cutoff > 0)
            AddVerification(session, data);

        return await SaveStep(session);
    }

    public async Task<WordCheckResultDto> ConfirmAsync(string userId, string sessionId, IReadOnlyCollection<int> unknownIds)
    {
        var session = await Load(userId, sessionId);
        if (session.Phase == WordCheckPhase.Failed)
            return new WordCheckResultDto(0, 0, null);
        if (session.Phase != WordCheckPhase.Verify) throw new CheckException("This word check is not ready to confirm.");

        foreach (var item in session.Items.Where(i => i.Kind == WordCheckItemKind.Verify))
            item.Answer = unknownIds.Contains(item.Id) ? WordCheckAnswer.No : WordCheckAnswer.Know;

        var (trapsShown, trapsClaimed) = session.Traps();
        var cutoff = WordCheckEstimator.Cutoff(session.Observations(), WordCheckEngine.FalseAlarmRate(trapsShown, trapsClaimed));
        var (inRange, words, batchId) = cutoff > 0 ? await SeedAsync(userId, cutoff) : (0, 0, null);
        await onboarding.RecordWordCheckAsync(userId, inRange, batchId);

        LogOutcome(session, cutoff, words);
        await sessions.DeleteAsync(SessionPrefix, session.Id);

        return new WordCheckResultDto(inRange, words, batchId);
    }

    private async Task<(int WordsInRange, int WordsAdded, Guid? BatchId)> SeedAsync(string userId, int cutoffRank)
    {
        var (seed, inRange) = await cardBatches.SeedFrequencyRangeAsync(userId, 1, cutoffRank, CardBatchDetails.Of(CardBatchKind.WordCheck));
        return (inRange, seed.InsertedWords, seed.BatchId);
    }

    private async Task<WordCheckStepDto> SaveStep(WordCheckSession session)
    {
        await sessions.SaveAsync(SessionPrefix, session);
        return await ToStep(session);
    }

    private Task<WordCheckSession> Load(string userId, string sessionId)
        => sessions.LoadAsync<WordCheckSession>(SessionPrefix, userId, sessionId, "This word check has expired. Start again.");

    private static void AddRound(WordCheckSession session, WordCheckPoolData data, (int Min, int Max) window)
    {
        var random = Random.Shared;
        var round = session.RoundsAsked;
        var usedText = session.Items.Select(i => i.Text).ToHashSet();

        var batch = PickWords(session, data, window, WordCheckEngine.WordsPerRound, random)
                    .OrderBy(_ => random.Next())
                    .Select(w => NewItem(WordCheckItemKind.Word, round, w))
                    .ToList();

        var traps = data.PseudoWords.Where(p => !usedText.Contains(p))
                        .OrderBy(_ => random.Next())
                        .Take(WordCheckEngine.TrapsInRound(round))
                        .Select(p => NewItem(WordCheckItemKind.Pseudo, round, new PoolWord(0, 0, 0, p)))
                        .ToList();

        var slots = WordCheckEngine.TrapSlots(batch.Count, traps.Count, random);
        for (var i = slots.Count - 1; i >= 0; i--)
            batch.Insert(slots[i], traps[i]);

        Append(session, batch);
        session.RoundsAsked++;
    }

    private static void AddVerification(WordCheckSession session, WordCheckPoolData data)
        => Append(session, PickWords(session, data, WordCheckEngine.VerificationWindow(session.Cutoff), WordCheckEngine.VerificationWords, Random.Shared)
                           .OrderBy(_ => Random.Shared.Next())
                           .Select(w => NewItem(WordCheckItemKind.Verify, -1, w)));

    /// <summary>Picks spread evenly over the logarithm of rank; a window short of unused words is topped up with the nearest ranks outside it.</summary>
    private static List<PoolWord> PickWords(WordCheckSession session, WordCheckPoolData data, (int Min, int Max) window, int count, Random random)
    {
        var used = session.Items.Select(i => i.WordId).ToHashSet();
        var unused = data.Words.Where(w => !used.Contains(w.WordId)).ToList();

        var picked = unused.Where(w => w.Rank >= window.Min && w.Rank <= window.Max)
                           .OrderByDescending(w => Math.Log(random.NextDouble()) * w.Rank)
                           .Take(count)
                           .ToList();
        if (picked.Count == count) return picked;

        var centre = (Math.Log(Math.Max(1, window.Min)) + Math.Log(Math.Max(1, window.Max))) / 2;
        picked.AddRange(unused.Where(w => w.Rank < window.Min || w.Rank > window.Max)
                              .OrderBy(w => Math.Abs(Math.Log(w.Rank) - centre))
                              .Take(count - picked.Count));
        return picked;
    }

    private static void Append(WordCheckSession session, IEnumerable<WordCheckItem> items)
    {
        var nextId = session.Items.Count + 1;
        foreach (var item in items)
        {
            item.Id = nextId++;
            session.Items.Add(item);
        }
    }

    private static WordCheckItem NewItem(WordCheckItemKind kind, int round, PoolWord word)
        => new() { Kind = kind, Round = round, Rank = word.Rank, WordId = word.WordId, ReadingIndex = word.ReadingIndex, Text = word.Text };

    private async Task<WordCheckStepDto> ToStep(WordCheckSession session)
    {
        var items = session.Phase switch
        {
            WordCheckPhase.Probe => await DescribePending(session),
            WordCheckPhase.Verify => session.Items.Where(i => i.Kind == WordCheckItemKind.Verify)
                                            .Select(i => new WordCheckItemDto(i.Id, i.Text)).ToList(),
            _ => []
        };

        return new WordCheckStepDto(session.Id, session.Phase.ToString().ToLowerInvariant(), items, session.Answered, WordCheckEngine.ProbeQuestions);
    }

    private async Task<List<WordCheckItemDto>> DescribePending(WordCheckSession session)
    {
        var pending = session.Pending.ToList();
        var described = await words.DescribeAsync(pending.Where(i => i.Kind == WordCheckItemKind.Word).Select(i => (i.WordId, i.ReadingIndex)));

        return pending.Select(i => i.Kind == WordCheckItemKind.Word && described.TryGetValue((i.WordId, i.ReadingIndex), out var d)
                                  ? new WordCheckItemDto(i.Id, i.Text, d.Reading, d.Meanings)
                                  : new WordCheckItemDto(i.Id, i.Text))
                      .ToList();
    }

    private void LogOutcome(WordCheckSession session, int finalCutoff, int words)
    {
        var (trapsShown, trapsClaimed) = session.Traps();
        var rounds = string.Join(",", session.Items.Where(i => i.Kind != WordCheckItemKind.Pseudo && i.Answer != null)
                                             .GroupBy(i => i.Round)
                                             .Select(g => $"{g.Min(i => i.Rank)}-{g.Max(i => i.Rank)}:{g.Count(i => i.Answer == WordCheckAnswer.Know)}/{g.Count()}"));
        logger.LogInformation(
            "Word check finished: Phase={Phase}, Level={Level}, Rounds={Rounds}, TrapsShown={TrapsShown}, TrapsClaimed={TrapsClaimed}, Cutoff={Cutoff}, FinalCutoff={FinalCutoff}, Words={Words}",
            session.Phase, session.Level, rounds, trapsShown, trapsClaimed, session.Cutoff, finalCutoff, words);
    }
}
