using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

/// <param name="Number">Ledger number the episode will be ingested under</param>
public sealed record NewEpisode(WebNovelEpisodeRef Reference, int Number);

/// <param name="MissingAtSource">Ingested episodes the source no longer lists; their text stays in the deck</param>
/// <param name="InsertedBeforeIngested">New episodes the source placed ahead of ones already ingested; they are appended at the end</param>
public sealed record LedgerDiff(List<NewEpisode> NewEpisodes, int MissingAtSource, int InsertedBeforeIngested);

public static class WebNovelLedger
{
    /// <summary>
    /// Matches a fresh table of contents against the ingested episodes by source episode id. New episodes are
    /// numbered after the highest ingested one, so subdeck ranges stay contiguous and closed subdecks never change.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The table of contents repeats an id, or a positional provider lists fewer episodes than were ingested:
    /// a deletion there renumbers every later episode, so appending would attach text to the wrong episode.
    /// </exception>
    public static LedgerDiff Diff(IReadOnlyCollection<WebNovelChapter> ledger, IReadOnlyList<WebNovelEpisodeRef> toc,
                                  bool stableEpisodeIds)
    {
        EnsureUniqueIds(toc);

        if (PositionalShrinkError(stableEpisodeIds, toc.Count, ledger.Count) is { } shrink)
            throw new InvalidOperationException(shrink);

        var ingested = ledger.Select(c => c.SourceEpisodeId).ToHashSet(StringComparer.Ordinal);
        var tocIds = toc.Select(e => e.SourceEpisodeId).ToHashSet(StringComparer.Ordinal);

        var lastIngestedPosition = -1;
        for (var i = 0; i < toc.Count; i++)
        {
            if (ingested.Contains(toc[i].SourceEpisodeId))
                lastIngestedPosition = i;
        }

        var nextNumber = ledger.Count == 0 ? 1 : ledger.Max(c => c.EpisodeNumber) + 1;
        var newEpisodes = new List<NewEpisode>();
        var insertedBefore = 0;

        for (var i = 0; i < toc.Count; i++)
        {
            if (ingested.Contains(toc[i].SourceEpisodeId))
                continue;

            if (i < lastIngestedPosition)
                insertedBefore++;

            newEpisodes.Add(new NewEpisode(toc[i], nextNumber++));
        }

        var missing = ledger.Count(c => !tocIds.Contains(c.SourceEpisodeId));

        return new LedgerDiff(newEpisodes, missing, insertedBefore);
    }

    /// <summary>
    /// Non-null when a positional provider lists fewer episodes than were ingested: a deletion there renumbers
    /// every later episode, so the novel can't be synced until it is rebuilt
    /// </summary>
    public static string? PositionalShrinkError(bool stableEpisodeIds, int sourceCount, int ingestedCount) =>
        !stableEpisodeIds && sourceCount < ingestedCount
            ? $"The source lists {sourceCount} episodes but {ingestedCount} are ingested: episodes were " +
              "deleted or renumbered at the source. Rebuild the affected subdecks or re-import the novel."
            : null;

    /// <exception cref="InvalidOperationException">An id is empty or appears twice.</exception>
    public static void EnsureUniqueIds(IReadOnlyList<WebNovelEpisodeRef> toc)
    {
        if (toc.Any(e => string.IsNullOrEmpty(e.SourceEpisodeId)))
            throw new InvalidOperationException("The table of contents has an episode without an id (markup may have changed).");

        var duplicate = toc.GroupBy(e => e.SourceEpisodeId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"The table of contents lists episode {duplicate.Key} twice (markup may have changed).");
    }

    /// <summary>
    /// Flags every ingested episode whose table-of-contents timestamp is newer than the one it was ingested with.
    /// Sources without revision timestamps (Kakuyomu lists publish times only) never flag anything.
    /// </summary>
    /// <returns>How many episodes are flagged</returns>
    public static int MarkRevisions(IEnumerable<WebNovelChapter> ledger, IReadOnlyList<WebNovelEpisodeRef> toc)
    {
        var byId = toc.ToDictionary(e => e.SourceEpisodeId, StringComparer.Ordinal);
        var revised = 0;

        foreach (var chapter in ledger)
        {
            chapter.RevisedAtSource = byId.TryGetValue(chapter.SourceEpisodeId, out var e) &&
                                      e.UpdatedAt != null && chapter.SourceUpdatedAt != null &&
                                      e.UpdatedAt > chapter.SourceUpdatedAt;
            if (chapter.RevisedAtSource)
                revised++;
        }

        return revised;
    }
}
