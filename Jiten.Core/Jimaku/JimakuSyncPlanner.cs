using System.Globalization;
using Jiten.Core.Data.Jimaku;
using Jiten.Core.Data.Providers.Jimaku;

namespace Jiten.Core.Jimaku;

public sealed record JimakuRemoteFile(string Name, string Url, long Size, DateTimeOffset? ModifiedAt)
{
    public static JimakuRemoteFile From(JimakuFile file) =>
        new(file.Name, file.Url, file.Size,
            DateTimeOffset.TryParse(file.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified)
                ? modified
                : null);
}

public sealed record JimakuLedgerEntry(string FileName, JimakuFileStatus Status, double? Episode);

public sealed record JimakuFileDecision(JimakuRemoteFile File, double? Episode, JimakuFileStatus Status, string? HoldReason);

public static class JimakuSyncPlanner
{
    /// <summary>
    /// The matching files a deck already holds when tracking starts: the ones its subdecks were made from, or,
    /// for a deck whose subdecks carry no file names, as many of the first episodes as it has subdecks.
    /// </summary>
    public static List<(JimakuRemoteFile File, double? Episode)> MatchExisting(string pattern,
                                                                             IReadOnlyList<JimakuRemoteFile> entryFiles,
                                                                             IReadOnlySet<string> subdeckFileNames,
                                                                             int subdeckCount)
    {
        var regex = JimakuFilePattern.Compile(pattern);
        var matched = entryFiles
                      .Where(f => f.Name.Length <= JimakuTrackedFile.MaxFileNameLength && regex.IsMatch(f.Name) &&
                                  JimakuFilePattern.IsSubtitle(f.Name))
                      .DistinctBy(f => f.Name, StringComparer.Ordinal)
                      .ToList();

        var episodes = JimakuEpisodeNumbers.Detect(matched.Select(f => f.Name).ToList());
        var ordered = matched.Select((f, i) => (File: f, Episode: episodes[i]))
                             .OrderBy(x => x.Episode ?? double.MaxValue)
                             .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
                             .ToList();

        var named = ordered.Where(x => subdeckFileNames.Contains(x.File.Name)).ToList();
        return named.Count > 0 ? named : ordered.Take(subdeckCount).ToList();
    }

    /// <summary>
    /// Decides every matching file that is new or still held. Imported, queued and ignored rows are final and only
    /// feed the checks: an episode number already present, a jump in the numbering and a count past the expected
    /// total all hold the file instead of ingesting it.
    /// </summary>
    public static List<JimakuFileDecision> Plan(string pattern,
                                                IReadOnlyList<JimakuRemoteFile> entryFiles,
                                                IReadOnlyList<JimakuLedgerEntry> ledger,
                                                int? expectedEpisodes)
    {
        var regex = JimakuFilePattern.Compile(pattern);
        var matched = entryFiles
                      .Where(f => f.Name.Length <= JimakuTrackedFile.MaxFileNameLength && regex.IsMatch(f.Name))
                      .Where(f => JimakuFilePattern.IsSubtitle(f.Name) || JimakuFilePattern.IsArchive(f.Name))
                      .DistinctBy(f => f.Name, StringComparer.Ordinal)
                      .ToList();

        var subtitles = matched.Where(f => JimakuFilePattern.IsSubtitle(f.Name)).ToList();
        var detected = JimakuEpisodeNumbers.Detect(subtitles.Select(f => f.Name).ToList());
        var episodeByName = subtitles.Select((f, i) => (f.Name, Episode: detected[i]))
                                     .ToDictionary(x => x.Name, x => x.Episode, StringComparer.Ordinal);

        var ledgerByName = ledger.ToDictionary(l => l.FileName, StringComparer.Ordinal);
        var settled = ledger.Where(l => l.Status is JimakuFileStatus.Imported or JimakuFileStatus.Queued).ToList();

        var taken = settled.Select(l => l.Episode ?? episodeByName.GetValueOrDefault(l.FileName))
                           .OfType<double>()
                           .ToHashSet();
        var acceptedCount = settled.Count;
        double? highest = taken.Count > 0 ? taken.Max() : null;

        var candidates = matched
                         .Where(f => !ledgerByName.TryGetValue(f.Name, out var row) || row.Status == JimakuFileStatus.Held)
                         .OrderBy(f => episodeByName.GetValueOrDefault(f.Name) ?? double.MaxValue)
                         .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();

        var contested = candidates.Select(f => episodeByName.GetValueOrDefault(f.Name))
                                  .OfType<double>()
                                  .GroupBy(e => e)
                                  .Where(g => g.Count() > 1)
                                  .Select(g => g.Key)
                                  .ToHashSet();

        var decisions = new List<JimakuFileDecision>();
        foreach (var file in candidates)
        {
            var episode = episodeByName.GetValueOrDefault(file.Name);
            var reason = HoldReason(file, episode);
            decisions.Add(new JimakuFileDecision(file, episode, reason == null ? JimakuFileStatus.Queued : JimakuFileStatus.Held, reason));

            if (reason != null)
                continue;

            acceptedCount++;
            if (episode is { } accepted)
            {
                taken.Add(accepted);
                highest = highest is { } h ? Math.Max(h, accepted) : accepted;
            }
        }

        return decisions;

        string? HoldReason(JimakuRemoteFile file, double? episode)
        {
            if (JimakuFilePattern.IsArchive(file.Name))
                return "archive: add its episodes by hand";

            if (episode is not { } number)
                return acceptedCount == 0 && subtitles.Count == 1 ? null : "unnumbered: no episode number in the file name";

            var label = JimakuEpisodeNumbers.Format(number);
            if (taken.Contains(number))
                return $"duplicate: episode {label} is already in the deck";
            if (contested.Contains(number))
                return $"duplicate: several files for episode {label}";
            if (highest is { } last && number > last + 1)
                return $"gap: episode {JimakuEpisodeNumbers.Format(Math.Floor(last) + 1)} is missing";
            if (expectedEpisodes is { } expected && acceptedCount + 1 > expected)
                return $"over: only {expected} episodes expected";

            return null;
        }
    }
}
