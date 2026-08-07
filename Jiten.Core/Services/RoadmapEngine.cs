using Jiten.Core.Data.Billing;

namespace Jiten.Core.Services;

/// <summary>A deck word reduced to what the roadmap needs. <see cref="Key"/> is <see cref="RoadmapEngine.PackKey"/>.</summary>
public readonly record struct RoadmapWord(long Key, int Occurrences);

/// <summary>
/// A candidate deck, pre-loaded. <see cref="Words"/> MUST be sorted by <see cref="RoadmapWord.Occurrences"/>
/// descending — the gap walk depends on it and does not re-sort.
/// </summary>
public sealed class RoadmapCandidate
{
    public int DeckId { get; init; }

    /// <summary>Total token count (<c>Deck.WordCount</c>), the denominator of coverage.</summary>
    public long WordCount { get; init; }

    /// <summary>
    /// Estimated hours to consume, the denominator of the efficiency preference. Zero when the deck carries
    /// no length data, which falls the cost back to <see cref="WordCount"/>.
    /// </summary>
    public double LengthHours { get; init; }

    public RoadmapWord[] Words { get; init; } = [];

    public float[]? Vector { get; init; }
}

public sealed class RoadmapEngineResult
{
    public List<RoadmapEngineStep> Steps { get; } = new();
    public RoadmapEngineDrill? Drill { get; set; }
    public bool GoalReached { get; set; }
    public double? GoalCoverageFinal { get; set; }
    public int? GoalWordsRemaining { get; set; }

    /// <summary>
    /// Goal mode: the plan reached the most it can from other titles, but that ceiling sits below the target
    /// because some of the goal's words appear only in the goal itself. Distinct from <see cref="GoalReached"/>
    /// (hit the target) and from a plain shortfall (more titles would still help).
    /// </summary>
    public bool GoalCeilingReached { get; set; }

    /// <summary>Goal words that appear in no candidate title, so they can only be learned by reading the goal.</summary>
    public int GoalUnreachableWords { get; set; }

    /// <summary>
    /// Goal mode: the fewest words that would take the user from today's known set to the target, learning the
    /// goal's highest-occurrence unknowns first. The floor any plan is measured against — a route through real
    /// titles always teaches more than this, because titles carry vocabulary the goal never uses.
    /// </summary>
    public int? GoalWordsAtStart { get; set; }
}

public sealed record RoadmapEngineStep(
    int Index,
    int DeckId,
    double Coverage,
    IReadOnlyList<RoadmapWord> AcquiredWords,
    double Score,
    double? GoalCoverageAfter)
{
    /// <summary>Goal mode: how many of <see cref="AcquiredWords"/> the goal actually uses.</summary>
    public int GoalNewWords { get; init; }
}

public sealed record RoadmapEngineDrill(
    int DeckId,
    double Coverage,
    IReadOnlyList<RoadmapWord> Words);

/// <summary>
/// The roadmap search. Deliberately free of EF, HTTP and configuration so it can be unit-tested against
/// hand-built candidate sets; <c>RoadmapDataLoader</c> owns everything I/O.
///
/// The objective is <b>maximise value-weighted words acquired, subject to coverage ≥ floor</b>. "Fewer new
/// words / gentler steps" is not a separate mode — it is the floor moving up. See PLAN_LearningRoadmap.md.
/// </summary>
public static class RoadmapEngine
{
    /// <summary>ReadingIndex is a byte on DeckWord, so 8 bits is exact, not a lossy hash.</summary>
    public static long PackKey(int wordId, int readingIndex) => ((long)wordId << 8) | (byte)readingIndex;

    public static int UnpackWordId(long key) => (int)(key >> 8);

    public static int UnpackReadingIndex(long key) => (int)(key & 0xFF);

    /// <summary>
    /// Heavily discounts the long tail: without it the search rewards decks stuffed with rare vocabulary,
    /// because raw new-word count treats a hapax proper noun and a top-2000 verb as equal.
    /// </summary>
    public static double WordValue(int frequencyRank)
    {
        if (frequencyRank <= 0)
            return 0.35; // unranked: real but unverifiable value, worth less than anything ranked
        return 1.0 / Math.Log(2.0 + frequencyRank / 1000.0);
    }

    public static double Coverage(RoadmapCandidate deck, HashSet<long> known)
    {
        if (deck.WordCount <= 0)
            return 0;

        long hit = 0;
        foreach (var word in deck.Words)
        {
            if (known.Contains(word.Key))
                hit += word.Occurrences;
        }

        return Math.Min(1.0, (double)hit / deck.WordCount);
    }

    /// <summary>
    /// The words reading this deck is projected to teach: unknown, and met at least
    /// <paramref name="acquisitionThreshold"/> times inside this deck.
    /// </summary>
    public static List<RoadmapWord> AcquisitionSet(RoadmapCandidate deck, HashSet<long> known, int acquisitionThreshold)
    {
        var acquired = new List<RoadmapWord>();
        foreach (var word in deck.Words)
        {
            // Words are sorted by occurrences desc, so the first word below the threshold ends the scan.
            if (word.Occurrences < acquisitionThreshold)
                break;
            if (!known.Contains(word.Key))
                acquired.Add(word);
        }

        return acquired;
    }

    /// <summary>
    /// The unknown words that must be learned before this deck clears the floor, highest-occurrence first.
    /// Distinct from <see cref="AcquisitionSet"/>: this is the cost of <i>unlocking</i> the deck, not what
    /// reading it teaches. Returns empty when the deck already clears the floor.
    /// </summary>
    public static List<RoadmapWord> GapToReadable(RoadmapCandidate deck, HashSet<long> known, double floor)
    {
        var needed = new List<RoadmapWord>();
        if (deck.WordCount <= 0)
            return needed;

        var target = floor * deck.WordCount;
        long covered = 0;

        foreach (var word in deck.Words)
        {
            if (known.Contains(word.Key))
                covered += word.Occurrences;
        }

        if (covered >= target)
            return needed;

        foreach (var word in deck.Words)
        {
            if (known.Contains(word.Key))
                continue;

            needed.Add(word);
            covered += word.Occurrences;
            if (covered >= target)
                break;
        }

        return needed;
    }

    public static double CosineSimilarity(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length)
            return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        if (na <= 0 || nb <= 0)
            return 0;

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>
    /// Bounds the taste multiplier. exp(λ·cos) is unbounded in λ, and an unclamped large λ lets semantic
    /// similarity overwhelm the acquisition score entirely, degrading the roadmap into a "more like this"
    /// list that ignores vocabulary.
    /// </summary>
    private const double MaxSimilarityMultiplier = 3.0;

    /// <summary>
    /// Weight applied to a title sitting exactly on the comprehension floor, rising linearly to 1 at the
    /// comfort target. The justification is not only comfort — acquisition per exposure falls as
    /// comprehension drops, because meaning cannot be inferred from context that is itself unknown, so the
    /// raw new-word count overstates what is actually learned down there.
    ///
    /// Tuning: this value sets how much richer a floor-level title must be to beat a comfortable one — at
    /// 0.3 it needs roughly 3.3× the value. Low enough to bite (a mild penalty would never change the
    /// ranking, since floor-level titles always teach more) but not so low that the floor becomes
    /// unreachable in practice, which would make the lower handle decorative.
    /// </summary>
    private const double FloorComfortWeight = 0.3;

    /// <summary>
    /// Scales a title's score by how far above the hard floor it sits. Titles at or above
    /// <see cref="RoadmapDefinition.ComfortTarget"/> are unpenalised.
    /// </summary>
    public static double ComfortWeight(double coverage, RoadmapDefinition settings)
    {
        var target = Math.Max(settings.ComfortTarget, settings.ComprehensionFloor);
        if (coverage >= target)
            return 1.0;

        var span = target - settings.ComprehensionFloor;
        if (span <= 0)
            return 1.0;

        var position = Math.Clamp((coverage - settings.ComprehensionFloor) / span, 0.0, 1.0);
        return FloorComfortWeight + (1.0 - FloorComfortWeight) * position;
    }

    private static double SimilarityMultiplier(RoadmapCandidate deck, float[]? centroid, double lambda)
    {
        if (Math.Abs(lambda) < 1e-9 || centroid is null || deck.Vector is null)
            return 1.0;

        var cos = CosineSimilarity(deck.Vector, centroid);
        var raw = Math.Exp(lambda * cos);
        return Math.Clamp(raw, 1.0 / MaxSimilarityMultiplier, MaxSimilarityMultiplier);
    }

    public sealed class RoadmapInput
    {
        public required RoadmapDefinition Settings { get; init; }
        public required IReadOnlyList<RoadmapCandidate> Candidates { get; init; }
        public required HashSet<long> KnownWords { get; init; }

        /// <summary>Word key → global frequency rank. Missing keys are treated as unranked.</summary>
        public required IReadOnlyDictionary<long, int> FrequencyRanks { get; init; }

        /// <summary>Deck id → deck ids that must be consumed (or already completed) before it.</summary>
        public IReadOnlyDictionary<int, int[]> Prerequisites { get; init; } = new Dictionary<int, int[]>();

        /// <summary>Decks the user already finished — satisfy prerequisites, and are never suggested.</summary>
        public IReadOnlySet<int> CompletedDeckIds { get; init; } = new HashSet<int>();

        /// <summary>Vectors of the decks the user has read or is reading, used to seed the taste centroid.</summary>
        public IReadOnlyList<float[]> SeedVectors { get; init; } = [];

        /// <summary>Goal-mode target. Its <see cref="RoadmapCandidate.Words"/> weight the scoring.</summary>
        public RoadmapCandidate? Goal { get; init; }
    }

    public static RoadmapEngineResult Build(RoadmapInput input)
    {
        var settings = input.Settings;
        var result = new RoadmapEngineResult();

        var known = new HashSet<long>(input.KnownWords);
        var consumed = new HashSet<int>();

        // Taste reference: the mean of what the user already read, drifting as the route is built so
        // "similar"/"different" is measured against the route so far, not only against their history.
        var tasteVectors = new List<float[]>(input.SeedVectors);
        var centroid = BuildCentroid(tasteVectors);

        // Fold a chosen deck into the running state: its acquired words become known, it is marked consumed,
        // and its vector drifts the taste centroid the subsequent picks are scored against.
        void ConsumeDeck(RoadmapCandidate deck, List<RoadmapWord> acquired)
        {
            foreach (var word in acquired)
                known.Add(word.Key);

            consumed.Add(deck.DeckId);
            if (deck.Vector is not null)
            {
                tasteVectors.Add(deck.Vector);
                centroid = BuildCentroid(tasteVectors);
            }
        }

        var goalWeights = BuildGoalWeights(input.Goal);

        var excluded = settings.ExcludedDeckIds.ToHashSet();

        var remaining = input.Candidates
                             .Where(c => c.WordCount > 0)
                             .Where(c => !input.CompletedDeckIds.Contains(c.DeckId))
                             .Where(c => !excluded.Contains(c.DeckId))
                             .ToList();

        // The goal is "reached" at its own target, not the stepping-stone readability floor: a named goal is
        // something the user wants to understand well, and the floor only decides which intermediate titles
        // are readable enough to suggest.
        var goalTarget = settings.GoalComprehensionTarget;

        if (input.Goal is not null)
        {
            // The goal itself is the destination, never a step on the way to itself.
            remaining = remaining.Where(c => c.DeckId != input.Goal.DeckId).ToList();

            if (Coverage(input.Goal, known) >= goalTarget)
            {
                result.GoalReached = true;
                result.GoalCoverageFinal = Coverage(input.Goal, known);
                result.GoalWordsRemaining = 0;
                result.GoalWordsAtStart = 0;
                return result;
            }

            // Measured against the known set as it stands now, before any step folds words into it.
            result.GoalWordsAtStart = GapToReadable(input.Goal, known, goalTarget).Count;
        }

        // Only prerequisites the plan can account for may block a deck: schedulable candidates, decks the
        // user swapped away (rejecting a prequel must not promote its sequel), and the goal itself — its own
        // sequels are the maximal-overlap decks under goal weighting and would otherwise be scheduled before
        // it. A prequel outside all of these (filtered out, or absent from the catalogue) must not deadlock
        // its sequel forever.
        var blockingPrereqs = remaining.Select(c => c.DeckId).ToHashSet();
        blockingPrereqs.UnionWith(excluded);
        if (input.Goal is not null)
            blockingPrereqs.Add(input.Goal.DeckId);

        // Goal mode walks until the goal reaches its target (or a dead-end forces a drill) within its own,
        // larger budget; falling short of the target is reported rather than papered over.
        var steps = input.Goal is not null
            ? Math.Clamp(settings.GoalSteps, RoadmapDefinition.MinSteps, RoadmapDefinition.MaxGoalSteps)
            : Math.Clamp(settings.Steps, RoadmapDefinition.MinSteps, RoadmapDefinition.MaxSteps);
        var byId = remaining.ToDictionary(c => c.DeckId);
        var stepIndex = 0;

        // Replay the accepted prefix verbatim. Swapping a later step must not reshuffle earlier ones, so
        // pinned decks are folded forward without being re-scored against the (now different) candidate set.
        foreach (var pinnedId in settings.PinnedDeckIds)
        {
            if (stepIndex >= steps)
                break;
            if (!byId.TryGetValue(pinnedId, out var pinned) || consumed.Contains(pinnedId))
                continue;

            var pinnedCoverage = Coverage(pinned, known);
            var pinnedAcquired = AcquisitionSet(pinned, known, settings.AcquisitionThreshold);

            ConsumeDeck(pinned, pinnedAcquired);

            stepIndex++;
            result.Steps.Add(new RoadmapEngineStep(
                                 stepIndex, pinnedId, pinnedCoverage, pinnedAcquired, 0,
                                 input.Goal is null ? null : Coverage(input.Goal, known))
                             {
                                 GoalNewWords = CountGoalWords(pinnedAcquired, goalWeights)
                             });
        }

        if (input.Goal is not null && result.Steps.Count > 0
            && result.Steps[^1].GoalCoverageAfter >= goalTarget)
        {
            result.GoalReached = true;
            result.GoalCoverageFinal = Coverage(input.Goal, known);
            result.GoalWordsRemaining = GapToReadable(input.Goal, known, goalTarget).Count;
            return result;
        }

        for (var i = stepIndex + 1; i <= steps; i++)
        {
            RoadmapCandidate? best = null;
            List<RoadmapWord>? bestAcquired = null;
            double bestScore = double.NegativeInfinity;
            double bestCoverage = 0;

            foreach (var deck in remaining)
            {
                if (consumed.Contains(deck.DeckId))
                    continue;

                if (!PrerequisitesSatisfied(deck.DeckId, input, consumed, blockingPrereqs))
                    continue;

                var coverage = Coverage(deck, known);
                if (coverage < settings.ComprehensionFloor)
                    continue;

                var acquired = AcquisitionSet(deck, known, settings.AcquisitionThreshold);
                if (acquired.Count == 0)
                    continue;

                var score = ScoreDeck(deck, acquired, input.FrequencyRanks, goalWeights, settings)
                            * SimilarityMultiplier(deck, centroid, settings.ContentSimilarity)
                            * ComfortWeight(coverage, settings);

                // Only goal mode can score zero (a deck teaching nothing the goal uses); an all-zero field
                // would otherwise "win" on iteration order and pad the plan with arbitrary titles.
                if (score <= 0)
                    continue;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = deck;
                    bestAcquired = acquired;
                    bestCoverage = coverage;
                }
            }

            if (best is null || bestAcquired is null)
            {
                result.Drill = BuildDrill(remaining, consumed, known, settings.ComprehensionFloor, input, blockingPrereqs);
                break;
            }

            ConsumeDeck(best, bestAcquired);

            double? goalCoverageAfter = input.Goal is null ? null : Coverage(input.Goal, known);

            result.Steps.Add(new RoadmapEngineStep(i, best.DeckId, bestCoverage, bestAcquired, bestScore, goalCoverageAfter)
                             {
                                 GoalNewWords = CountGoalWords(bestAcquired, goalWeights)
                             });

            if (input.Goal is not null && goalCoverageAfter >= goalTarget)
            {
                result.GoalReached = true;
                break;
            }
        }

        if (input.Goal is not null)
        {
            var finalCoverage = Coverage(input.Goal, known);
            result.GoalCoverageFinal = finalCoverage;
            result.GoalWordsRemaining = GapToReadable(input.Goal, known, goalTarget).Count;

            // If the plan fell short of the target but has learned everything the candidate titles can teach
            // toward the goal, the remaining gap is words that live only in the goal itself. Report that as a
            // ceiling — chasing more titles cannot cross it — rather than a plain shortfall, and drop the
            // stepping-stone drill, which is noise once the goal has topped out.
            if (!result.GoalReached)
            {
                var (ceiling, unreachableWords) = GoalCeiling(input.Goal, known, remaining, settings.AcquisitionThreshold);
                if (finalCoverage >= ceiling - 1e-6)
                {
                    result.GoalCeilingReached = true;
                    result.GoalUnreachableWords = unreachableWords;
                    result.Drill = null;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The most of a goal a plan could ever cover, and how many of its words are unreachable. A goal word is
    /// reachable if it is already known or appears in some candidate title at the acquisition threshold; the
    /// rest occur only in the goal, so no other title can teach them.
    /// </summary>
    private static (double Ceiling, int UnreachableWords) GoalCeiling(
        RoadmapCandidate goal, HashSet<long> known, IReadOnlyList<RoadmapCandidate> candidates, int threshold)
    {
        var teachable = new HashSet<long>();
        foreach (var deck in candidates)
        {
            foreach (var word in deck.Words)
            {
                // Words are sorted by descending occurrence, so the first sub-threshold word ends this deck.
                if (word.Occurrences < threshold)
                    break;
                teachable.Add(word.Key);
            }
        }

        long reachableOccurrences = 0;
        var unreachable = 0;
        foreach (var word in goal.Words)
        {
            if (known.Contains(word.Key) || teachable.Contains(word.Key))
                reachableOccurrences += word.Occurrences;
            else
                unreachable++;
        }

        var ceiling = goal.WordCount > 0 ? (double)reachableOccurrences / goal.WordCount : 0.0;
        return (ceiling, unreachable);
    }

    private static double ScoreDeck(
        RoadmapCandidate deck,
        List<RoadmapWord> acquired,
        IReadOnlyDictionary<long, int> ranks,
        IReadOnlyDictionary<long, int>? goalWeights,
        RoadmapDefinition settings)
    {
        double gross = 0;

        foreach (var word in acquired)
        {
            if (goalWeights is not null)
            {
                // Goal coverage is a share of the target's running text, so a word is worth exactly the
                // occurrences it accounts for there — linearly. Compressing that (a log, say) collapses the
                // 200:1 gap between a word the target leans on and one it mentions once into single digits,
                // and the search then buys thousands of tail words instead of the few that move coverage.
                // The global-rarity discount is deliberately absent here: it penalises precisely the
                // work-specific vocabulary that carries most of a hard target's remaining gap.
                if (!goalWeights.TryGetValue(word.Key, out var goalOcc))
                    continue;

                gross += goalOcc;
                continue;
            }

            gross += WordValue(ranks.GetValueOrDefault(word.Key, 0));
        }

        return gross / EffortCost(deck, settings);
    }

    /// <summary>Reading hours assumed for a deck with no length data, per token.</summary>
    private const double FallbackHoursPerToken = 1.0 / 10000.0;

    /// <summary>
    /// Fixed cost charged to every title on top of its running time, without which the plan fills with
    /// snack-sized picks: a forty-minute episode really does teach more per hour than a novel, so a purely
    /// per-hour ratio ranks a route of thirty shorts above one that reaches the goal. Two things justify it.
    /// A plan slot is scarce (<see cref="RoadmapDefinition.MaxGoalSteps"/>) and starting a new work costs the
    /// same effort whatever its length. And <see cref="AcquisitionSet"/> credits a deck with permanently
    /// teaching every word met its threshold number of times, which is far more generous for five exposures
    /// inside one short film than for five spread across a novel — so short content's yield is overstated in
    /// the first place, roughly in proportion to how short it is.
    /// </summary>
    private const double PerTitleCommitmentHours = 5.0;

    /// <summary>
    /// What the efficiency preference divides yield by: hours of the user's life, not tokens. Token count is
    /// only a stand-in for length, and it prices a two-hour film like a pamphlet.
    /// </summary>
    private static double EffortCost(RoadmapCandidate deck, RoadmapDefinition settings)
    {
        if (settings.Preference != RoadmapPreference.Efficiency)
            return 1.0;

        var hours = deck.LengthHours > 0 ? deck.LengthHours : deck.WordCount * FallbackHoursPerToken;
        return PerTitleCommitmentHours + hours;
    }

    private static int CountGoalWords(IReadOnlyList<RoadmapWord> acquired, IReadOnlyDictionary<long, int>? goalWeights)
    {
        if (goalWeights is null)
            return 0;

        var count = 0;
        foreach (var word in acquired)
        {
            if (goalWeights.ContainsKey(word.Key))
                count++;
        }

        return count;
    }

    private static Dictionary<long, int>? BuildGoalWeights(RoadmapCandidate? goal)
    {
        if (goal is null)
            return null;

        var weights = new Dictionary<long, int>(goal.Words.Length);
        foreach (var word in goal.Words)
            weights[word.Key] = word.Occurrences;

        return weights;
    }

    private static bool PrerequisitesSatisfied(int deckId, RoadmapInput input, HashSet<int> consumed, HashSet<int> blocking)
    {
        if (!input.Prerequisites.TryGetValue(deckId, out var prereqs) || prereqs.Length == 0)
            return true;

        foreach (var prereq in prereqs)
        {
            if (input.CompletedDeckIds.Contains(prereq) || consumed.Contains(prereq))
                continue;
            if (blocking.Contains(prereq))
                return false;
        }

        return true;
    }

    private static RoadmapEngineDrill? BuildDrill(
        IReadOnlyList<RoadmapCandidate> remaining,
        HashSet<int> consumed,
        HashSet<long> known,
        double floor,
        RoadmapInput input,
        HashSet<int> blocking)
    {
        RoadmapEngineDrill? best = null;
        var fewestWords = int.MaxValue;

        foreach (var deck in remaining)
        {
            if (consumed.Contains(deck.DeckId))
                continue;

            if (!PrerequisitesSatisfied(deck.DeckId, input, consumed, blocking))
                continue;

            var gap = GapToReadable(deck, known, floor);
            if (gap.Count == 0 || gap.Count >= fewestWords)
                continue;

            fewestWords = gap.Count;
            best = new RoadmapEngineDrill(deck.DeckId, Coverage(deck, known), gap);
        }

        return best;
    }

    private static float[]? BuildCentroid(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
            return null;

        var dim = vectors[0].Length;
        var centroid = new float[dim];
        var counted = 0;

        foreach (var vector in vectors)
        {
            if (vector.Length != dim)
                continue;
            for (var i = 0; i < dim; i++)
                centroid[i] += vector[i];
            counted++;
        }

        if (counted == 0)
            return null;

        for (var i = 0; i < dim; i++)
            centroid[i] /= counted;

        return centroid;
    }
}
