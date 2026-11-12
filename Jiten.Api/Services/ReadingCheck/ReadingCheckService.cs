using System.Text.Json;
using Jiten.Core;
using Jiten.Core.Data.User;

namespace Jiten.Api.Services.ReadingCheck;

public interface IReadingCheckService
{
    Task<ReadingCountDto> CountAsync(string userId, IReadOnlyCollection<int> deckIds);
    Task<ReadingStepDto> StartAsync(string userId, IReadOnlyCollection<int> deckIds);
    Task<ReadingStepDto> AnswerAsync(string userId, string sessionId, IReadOnlyList<ReadingAnswerDto> answers);
    Task<ReadingStepDto> VerifyAsync(string userId, string sessionId, int? level);
    Task<ReadingStepDto> ReviewAsync(string userId, string sessionId, IReadOnlyCollection<int> unknownIds);
    Task<ReadingResultDto> ConfirmAsync(string userId, string sessionId, IReadOnlyCollection<int> unknownIds);
}

public sealed class ReadingCheckService(
    IReadingCheckData data,
    ReadingCheckWriter writer,
    IOnboardingStore onboarding,
    CheckSessionStore sessions,
    UserDbContext userContext,
    ILogger<ReadingCheckService> logger) : IReadingCheckService
{
    private const int StudyListCap = 500;

    /// <summary>A word met in this many works or more counts as broadly used; past it, frequency decides the order.</summary>
    private const int StudyBreadthCap = 3;
    private const string SessionPrefix = "reading-check";

    public async Task<ReadingCountDto> CountAsync(string userId, IReadOnlyCollection<int> deckIds)
    {
        var leaves = await data.ResolveLeavesAsync(deckIds);
        if (leaves.Count == 0) return new ReadingCountDto(0, 0);
        var candidates = await data.GetCandidatesAsync(userId, leaves);
        return new ReadingCountDto(leaves.Count, candidates.Count(c => c.Seedable));
    }

    public async Task<ReadingStepDto> StartAsync(string userId, IReadOnlyCollection<int> deckIds)
    {
        var leaves = await data.ResolveLeavesAsync(deckIds);
        if (leaves.Count < ReadingCheckEngine.MinUnits)
            throw new CheckException($"Pick at least {ReadingCheckEngine.MinUnits} volumes or works.");

        var candidates = await data.GetCandidatesAsync(userId, leaves);
        if (candidates.Count(c => c.Seedable) < ReadingCheckEngine.MinCandidates)
            throw new CheckException("Add more volumes or works for a reliable result.");

        var session = new ReadingCheckSession
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            DeckIds = leaves.OrderBy(i => i).ToList(),
            Phase = ReadingPhase.Probe
        };

        if (!AddRound(session, candidates))
            throw new CheckException("Add more volumes or works for a reliable result.");

        return await SaveStep(session, candidates);
    }

    public async Task<ReadingStepDto> AnswerAsync(string userId, string sessionId, IReadOnlyList<ReadingAnswerDto> answers)
    {
        var session = await Load(userId, sessionId);
        if (session.Phase != ReadingPhase.Probe) throw new CheckException("There are no words waiting for an answer.");

        var pending = session.Pending.Where(i => i.Kind == ReadingItemKind.Probe).ToDictionary(i => i.Id);
        foreach (var answer in answers)
            if (pending.TryGetValue(answer.Id, out var item))
                item.Answer = answer.Answer;
        if (session.Pending.Any(i => i.Kind == ReadingItemKind.Probe))
            throw new CheckException("Answer every word before continuing.");

        var candidates = await Candidates(session);
        var fit = ReadingCheckEngine.Fit(session.FitData());

        if (session.Answered >= ReadingCheckEngine.MinAnswers)
        {
            var known = SelectionAt(session, fit, candidates, ReadingCheckEngine.DefaultLevel).Known.Count;
            var stable = ReadingCheckEngine.IsStable(session.LastKnown, known);
            session.LastKnown = known;
            if (stable || session.Answered >= ReadingCheckEngine.MaxAnswers)
                return await Finish(session, candidates);
        }

        if (!AddRound(session, candidates)) return await Finish(session, candidates);
        return await SaveStep(session, candidates);
    }

    public async Task<ReadingStepDto> VerifyAsync(string userId, string sessionId, int? level)
    {
        var session = await Load(userId, sessionId);
        if (session.Phase != ReadingPhase.Fit) throw new CheckException("This check is not ready for the final step.");

        session.Level = ReadingCheckEngine.LevelOf(level).Level;
        var candidates = await Candidates(session);
        AddReviewPage(session, candidates);
        session.Phase = ReadingPhase.Verify;
        return await SaveStep(session, candidates);
    }

    public async Task<ReadingStepDto> ReviewAsync(string userId, string sessionId, IReadOnlyCollection<int> unknownIds)
    {
        var session = await Load(userId, sessionId);
        if (session.Phase != ReadingPhase.Verify) throw new CheckException("This check is not ready for a review page.");

        AnswerReviewPage(session, unknownIds);
        var candidates = await Candidates(session);
        AddReviewPage(session, candidates);
        return await SaveStep(session, candidates);
    }

    public async Task<ReadingResultDto> ConfirmAsync(string userId, string sessionId, IReadOnlyCollection<int> unknownIds)
    {
        var session = await Load(userId, sessionId);
        if (session.Phase != ReadingPhase.Verify) throw new CheckException("This check is not ready to confirm.");

        AnswerReviewPage(session, unknownIds);
        var verifyItems = session.Items.Where(i => i.Kind == ReadingItemKind.Verify).ToList();

        var candidates = await Candidates(session);
        var fit = ReadingCheckEngine.Fit(session.FitData());
        var selection = SelectionAt(session, fit, candidates, session.Level);

        var known = session.Items.Where(i => i.Answer == ReadingCheckAnswer.Knew)
                           .Select(i => (i.WordId, i.ReadingIndex))
                           .Concat(selection.Known.Select(c => (c.WordId, c.ReadingIndex)))
                           .ToHashSet();
        var study = candidates.Where(c => c.Seedable && !known.Contains((c.WordId, c.ReadingIndex))).ToList();

        var (seeded, batchId) = await writer.SeedAsync(userId, known, session.DeckIds.Count);

        var seedable = candidates.Where(c => c.Seedable).ToList();
        var medianRank = seedable.Select(c => c.Rank).OrderBy(r => r).ElementAtOrDefault(seedable.Count / 2);
        var retention = ReadingCheckEngine.RetentionEncounters(fit, medianRank);

        var studyTop = study.OrderByDescending(c => Math.Min(c.Works, StudyBreadthCap)).ThenByDescending(c => c.Occurrences).Take(StudyListCap).ToList();
        var described = await data.DescribeAsync(studyTop.Select(c => (c.WordId, c.ReadingIndex)));
        var studyList = studyTop.Where(c => described.ContainsKey((c.WordId, c.ReadingIndex)))
                                .Select(c =>
                                {
                                    var d = described[(c.WordId, c.ReadingIndex)];
                                    return new ReadingStudyWordDto(c.WordId, c.ReadingIndex, d.Text, d.Reading, d.Meanings.FirstOrDefault() ?? "",
                                                                   c.Occurrences);
                                })
                                .ToList();

        await onboarding.RecordReadingCheckAsync(userId, new ReadingCheckRunState
        {
            FinishedAt = DateTime.UtcNow,
            Units = session.DeckIds.Count,
            Seeded = seeded,
            RetentionEncounters = retention,
            BatchId = batchId
        });

        logger.LogInformation(
            "Reading check finished: Units={Units}, Candidates={Candidates}, Answered={Answered}, Known={Known}/{Answers}, Reviewed={Reviewed}, ReviewUnknown={ReviewUnknown}, Level={Level}, Seeded={Seeded}, ExpectedWrong={ExpectedWrong:F0}, A={A:F3}, B={B:F3}, C={C:F3}, E={E:F3}, Retention={Retention}",
            session.DeckIds.Count, seedable.Count, session.Answered, session.Items.Count(i => i.Answer == ReadingCheckAnswer.Knew), session.Items.Count,
            verifyItems.Count, verifyItems.Count(i => i.Answer == ReadingCheckAnswer.DidntKnow), session.Level, seeded, selection.ExpectedWrong, fit.A, fit.B,
            fit.C, fit.E, retention);

        userContext.ReadingCheckRuns.Add(new ReadingCheckRun
        {
            UserId = userId, CreatedAt = DateTime.UtcNow, Units = session.DeckIds.Count, Candidates = seedable.Count, Level = session.Level,
            Seeded = seeded, A = fit.A, B = fit.B, C = fit.C, BatchId = batchId, AnswersJson = AnswersJson(session)
        });
        await userContext.SaveChangesAsync();

        await sessions.DeleteAsync(SessionPrefix, session.Id);
        return new ReadingResultDto(seeded, batchId, retention, studyList);
    }

    private static string AnswersJson(ReadingCheckSession session)
        => JsonSerializer.Serialize(session.Items.Where(i => i.Answer != null)
                                           .Select(i => new[] { i.WordId, i.ReadingIndex, i.Occurrences, i.Rank, i.Works, (int)i.Answer!.Value, i.Kind == ReadingItemKind.Verify ? 1 : 0, i.Expression ? 1 : 0 }));

    private async Task<ReadingStepDto> Finish(ReadingCheckSession session, List<ReadingCandidate> candidates)
    {
        session.Phase = ReadingPhase.Fit;
        return await SaveStep(session, candidates);
    }

    /// <summary>Markable words the reader has not been shown.</summary>
    private static List<ReadingCandidate> Open(ReadingCheckSession session, List<ReadingCandidate> candidates)
    {
        var shown = session.Items.Select(i => (i.WordId, i.ReadingIndex)).ToHashSet();
        return candidates.Where(c => c.Seedable && !shown.Contains((c.WordId, c.ReadingIndex))).ToList();
    }

    private static List<ReadingSelection> Selections(ReadingCheckSession session, ReadingFit fit, List<ReadingCandidate> candidates)
        => ReadingCheckEngine.Select(fit, Open(session, candidates));

    private static void AnswerReviewPage(ReadingCheckSession session, IReadOnlyCollection<int> unknownIds)
    {
        foreach (var item in session.PendingReview.ToList())
            item.Answer = unknownIds.Contains(item.Id) ? ReadingCheckAnswer.DidntKnow : ReadingCheckAnswer.Knew;
    }

    private static void AddReviewPage(ReadingCheckSession session, List<ReadingCandidate> candidates)
    {
        var fit = ReadingCheckEngine.Fit(session.FitData());
        AddItems(session, ReadingCheckEngine.ReviewPage(fit, Open(session, candidates), session.Level), ReadingItemKind.Verify);
    }

    private static ReadingSelection SelectionAt(ReadingCheckSession session, ReadingFit fit, List<ReadingCandidate> candidates, int level)
        => Selections(session, fit, candidates).First(t => t.Level == level);

    /// <summary>Adds the next round; false when no askable word is left.</summary>
    private static bool AddRound(ReadingCheckSession session, List<ReadingCandidate> candidates)
    {
        var used = session.Items.Select(i => (i.WordId, i.ReadingIndex)).ToHashSet();
        var pool = candidates.Where(c => c.Probeable && !used.Contains((c.WordId, c.ReadingIndex)));
        var picked = ReadingCheckEngine.NextRound(pool, session.Items.Count(i => i.Expression), Random.Shared);
        if (picked.Count == 0) return false;
        AddItems(session, picked, ReadingItemKind.Probe);
        return true;
    }

    private static void AddItems(ReadingCheckSession session, IEnumerable<ReadingCandidate> picked, ReadingItemKind kind)
    {
        var nextId = session.Items.Count + 1;
        foreach (var c in picked.OrderBy(_ => Random.Shared.Next()))
        {
            session.Items.Add(new ReadingCheckItem
            {
                Id = nextId++, Kind = kind, WordId = c.WordId, ReadingIndex = c.ReadingIndex, Occurrences = c.Occurrences, Rank = c.Rank,
                Works = c.Works, Offset = c.Offset, Expression = c.Expression
            });
        }
    }

    private async Task<ReadingStepDto> ToStep(ReadingCheckSession session, List<ReadingCandidate> candidates)
    {
        var shown = session.Phase switch
        {
            ReadingPhase.Probe => session.Pending.Where(i => i.Kind == ReadingItemKind.Probe).ToList(),
            ReadingPhase.Verify => session.PendingReview.ToList(),
            _ => []
        };

        var described = await data.DescribeAsync(shown.Select(i => (i.WordId, i.ReadingIndex)));
        var items = shown.Where(i => described.ContainsKey((i.WordId, i.ReadingIndex)))
                         .Select(i =>
                         {
                             var d = described[(i.WordId, i.ReadingIndex)];
                             return new ReadingItemDto(i.Id, d.Text, d.Reading, d.Meanings);
                         })
                         .ToList();

        if (session.Phase == ReadingPhase.Probe)
        {
            var asked = session.Answered + shown.Count(i => i.Answer == null);
            var remaining = asked < ReadingCheckEngine.MinAnswers ? ReadingCheckEngine.MinAnswers - asked + ReadingCheckEngine.RoundSize : 2 * ReadingCheckEngine.RoundSize;
            session.EstimatedTotal = Math.Max(session.EstimatedTotal, Math.Min(ReadingCheckEngine.MaxAnswers, asked + remaining));
        }

        var levels = new List<ReadingLevelOptionDto>();
        if (session.Phase != ReadingPhase.Probe)
        {
            var answeredKnew = session.Items.Count(i => i.Answer == ReadingCheckAnswer.Knew);
            levels = Selections(session, ReadingCheckEngine.Fit(session.FitData()), candidates)
                     .Select(t => new ReadingLevelOptionDto(t.Level, t.Known.Count + answeredKnew, (int)Math.Round(t.ExpectedWrong)))
                     .ToList();
        }

        var known = session.Phase == ReadingPhase.Verify ? levels.First(l => l.Level == session.Level).Known : 0;
        return new ReadingStepDto(session.Id, session.Phase.ToString().ToLowerInvariant(), items, session.Answered, session.EstimatedTotal,
                                  session.Phase == ReadingPhase.Fit ? levels : [], session.Level, known);
    }

    private async Task<List<ReadingCandidate>> Candidates(ReadingCheckSession session)
        => await data.GetCandidatesAsync(session.UserId, session.DeckIds);

    /// <summary>Builds the step before saving, so the estimate it raises persists.</summary>
    private async Task<ReadingStepDto> SaveStep(ReadingCheckSession session, List<ReadingCandidate> candidates)
    {
        var step = await ToStep(session, candidates);
        await sessions.SaveAsync(SessionPrefix, session);
        return step;
    }

    private Task<ReadingCheckSession> Load(string userId, string sessionId)
        => sessions.LoadAsync<ReadingCheckSession>(SessionPrefix, userId, sessionId, "This check has expired. Start again.");
}
