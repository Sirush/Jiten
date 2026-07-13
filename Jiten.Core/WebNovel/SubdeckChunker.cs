using System.Text.RegularExpressions;

namespace Jiten.Core.WebNovel;

/// <summary>
/// An episode about to be placed into a subdeck.
/// </summary>
public record ChunkEpisode(int Number, string Title, int CharCount);

/// <summary>
/// A subdeck that already exists. Only the last one can still grow.
/// </summary>
public record ExistingChunk(int ChunkIndex, int ChildDeckId, int StartEpisode, int EndEpisode, int EpisodeCount, int CharCount);

/// <summary>
/// One subdeck to create or extend. <see cref="ChildDeckId"/> is null when the subdeck doesn't exist yet.
/// </summary>
public record ChunkPlan
{
    public int ChunkIndex { get; init; }
    public int? ChildDeckId { get; init; }
    public List<ChunkEpisode> EpisodesToAppend { get; init; } = new();

    /// <summary>
    /// Episode range of the subdeck once this plan is applied, counting episodes already in it
    /// </summary>
    public int StartEpisode { get; init; }
    public int EndEpisode { get; init; }

    public bool IsNew => ChildDeckId == null;

    public string Title => SubdeckChunker.BuildTitle(StartEpisode, EndEpisode);
}

public static partial class SubdeckChunker
{
    /// <summary>
    /// Roughly a light-novel volume, matching the size of existing per-volume novel decks
    /// </summary>
    public const int DefaultCharBudget = 150_000;

    /// <summary>
    /// Backstop for works with micro-chapters, which would otherwise never reach the budget
    /// </summary>
    public const int MaxEpisodesPerChunk = 150;

    /// <summary>
    /// Subdecks prefer to end on a round episode number — 第1話〜第60話 rather than 第1話〜第57話 — so the
    /// split lands where a reader would expect it. Steps are tried largest first, and one is only used when
    /// the subdeck holds at least twice its size, which keeps the rounding a small nudge rather than a
    /// wholesale redraw of the boundary. Works with very long chapters fall through and split at the budget.
    /// </summary>
    private static readonly int[] BoundarySteps = [10, 5];

    /// <summary>
    /// Assigns episodes to subdecks by character budget, splitting on a round episode number where possible.
    ///
    /// Only the last subdeck is ever appended to; once it reaches the budget it closes and a new one opens.
    /// Closed subdecks are immutable ranges, so no episode ever moves between subdecks and user progress
    /// (SRS, coverage, known-word marks) stays stable.
    /// </summary>
    public static List<ChunkPlan> Plan(IReadOnlyList<ExistingChunk> existing,
                                       IReadOnlyList<ChunkEpisode> newEpisodes,
                                       int charBudget = DefaultCharBudget,
                                       int maxEpisodesPerChunk = MaxEpisodesPerChunk)
    {
        if (newEpisodes.Count == 0)
            return [];

        var plans = new List<ChunkPlan>();
        var lastChunk = existing.OrderBy(c => c.ChunkIndex).LastOrDefault();
        var nextIndex = (lastChunk?.ChunkIndex ?? 0) + 1;
        var episodes = newEpisodes.OrderBy(e => e.Number).ToList();

        // The open subdeck is simply the last one — unless it is already full, in which case we open a new one
        var open = lastChunk != null && !IsFull(lastChunk.CharCount, lastChunk.EpisodeCount, charBudget, maxEpisodesPerChunk)
            ? new OpenChunk(lastChunk.ChunkIndex, lastChunk.ChildDeckId, lastChunk.StartEpisode,
                            lastChunk.EndEpisode, lastChunk.EpisodeCount, lastChunk.CharCount)
            : null;

        var index = 0;
        while (index < episodes.Count)
        {
            open ??= new OpenChunk(nextIndex, null, episodes[index].Number, sealedEnd: null, 0, 0);

            open.Append(episodes[index]);
            index++;

            if (!IsFull(open.CharCount, open.EpisodeCount, charBudget, maxEpisodesPerChunk))
                continue;

            open.TargetEnd ??= PreferredEnd(open);

            // The round boundary is still ahead of us: keep the subdeck open and grow into it
            if (open.LastEpisode < open.TargetEnd)
                continue;

            // Rounding down hands the overshooting episodes back to the next subdeck
            index -= open.TruncateAfter(open.TargetEnd.Value);

            if (open.Appended.Count > 0)
                plans.Add(open.ToPlan());

            if (open.ChildDeckId == null && open.Appended.Count > 0)
                nextIndex++;

            open = null;
        }

        // A partially filled subdeck stays open for the next sync
        if (open is { Appended.Count: > 0 })
            plans.Add(open.ToPlan());

        return plans;
    }

    /// <summary>
    /// The episode to split on, given the budget ran out at <see cref="OpenChunk.LastEpisode"/>. Rounds to the
    /// nearest round number, but never before episodes the subdeck already holds: those are parsed and
    /// published, so the boundary can only ever move forward over them.
    /// </summary>
    private static int PreferredEnd(OpenChunk open)
    {
        foreach (var step in BoundarySteps)
        {
            if (open.EpisodeCount < step * 2)
                continue;

            var rounded = (int)Math.Round(open.LastEpisode / (double)step, MidpointRounding.AwayFromZero) * step;

            // Rounding down would cut episodes the subdeck already published; take the next boundary up instead,
            // leaving the subdeck open until the source publishes enough episodes to reach it
            if (rounded < open.MinimumEnd)
                rounded = (open.MinimumEnd + step - 1) / step * step;

            return rounded;
        }

        return open.LastEpisode;
    }

    private sealed class OpenChunk(int chunkIndex, int? childDeckId, int startEpisode, int? sealedEnd, int episodeCount,
                                   int charCount)
    {
        public int? ChildDeckId { get; } = childDeckId;
        public int EpisodeCount { get; private set; } = episodeCount;
        public int CharCount { get; private set; } = charCount;
        public List<ChunkEpisode> Appended { get; } = [];

        /// <summary>
        /// Where this subdeck will close, once decided. Fixed on the first overflow so that growing into the
        /// boundary can't keep moving it.
        /// </summary>
        public int? TargetEnd { get; set; }

        public int LastEpisode => Appended.Count > 0 ? Appended[^1].Number : sealedEnd ?? startEpisode;

        /// <summary>
        /// The earliest episode this subdeck can end on: it must keep everything already written to it, and a
        /// brand-new subdeck must still hold its first episode.
        /// </summary>
        public int MinimumEnd => sealedEnd ?? startEpisode;

        public void Append(ChunkEpisode episode)
        {
            Appended.Add(episode);
            EpisodeCount++;
            CharCount += episode.CharCount;
        }

        /// <summary>
        /// Drops the episodes past the boundary so the next subdeck can take them, and reports how many were
        /// handed back. Only ever touches episodes appended in this pass — <see cref="MinimumEnd"/> keeps the
        /// boundary at or beyond what is already on disk.
        /// </summary>
        public int TruncateAfter(int endEpisode)
        {
            var removed = 0;

            while (Appended.Count > 0 && Appended[^1].Number > endEpisode)
            {
                CharCount -= Appended[^1].CharCount;
                EpisodeCount--;
                Appended.RemoveAt(Appended.Count - 1);
                removed++;
            }

            return removed;
        }

        public ChunkPlan ToPlan() => new()
        {
            ChunkIndex = chunkIndex,
            ChildDeckId = ChildDeckId,
            EpisodesToAppend = Appended,
            StartEpisode = startEpisode,
            EndEpisode = Appended[^1].Number
        };
    }

    private static bool IsFull(int charCount, int episodeCount, int charBudget, int maxEpisodesPerChunk) =>
        charCount >= charBudget || episodeCount >= maxEpisodesPerChunk;

    /// <summary>
    /// Subdeck title, e.g. 第1話〜第60話. The open subdeck is renamed as it grows.
    /// </summary>
    public static string BuildTitle(int startEpisode, int endEpisode) =>
        startEpisode == endEpisode
            ? $"第{startEpisode}話"
            : $"第{startEpisode}話〜第{endEpisode}話";

    /// <summary>
    /// Length of the text as a reader sees it: inline furigana ({漢字'かんじ}) contributes only its base,
    /// and whitespace doesn't count — mirroring how Deck.CharacterCount is measured.
    /// </summary>
    public static int CountCharacters(string annotatedText)
    {
        if (string.IsNullOrEmpty(annotatedText))
            return 0;

        var stripped = FuriganaAnnotation().Replace(annotatedText, "$1");
        return stripped.Count(c => !char.IsWhiteSpace(c));
    }

    [GeneratedRegex(@"\{([^'{}]+)'([^}]+)\}")]
    private static partial Regex FuriganaAnnotation();
}
