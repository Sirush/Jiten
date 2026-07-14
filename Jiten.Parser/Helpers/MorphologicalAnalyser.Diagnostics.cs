using System.Diagnostics;
using Jiten.Core.Data;
using Jiten.Parser.Diagnostics;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    private static List<SudachiToken> ParseSudachiOutputToDiagnosticTokens(string rawOutput)
    {
        var tokens = new List<SudachiToken>();
        var lines = rawOutput.Split('\n');

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line == "EOS") continue;

            var parts = line.Split('\t');
            if (parts.Length < 5) continue;

            var posDetail = parts[1].Split(',');
            tokens.Add(new SudachiToken
                       {
                           Surface = parts[0], PartOfSpeech = posDetail.Length > 0 ? posDetail[0] : "",
                           PosDetail = posDetail.Skip(1).ToArray(), NormalizedForm = parts.Length > 2 ? parts[2] : "",
                           DictionaryForm = parts.Length > 3 ? parts[3] : "", Reading = parts.Length > 4 ? parts[4] : ""
                       });
        }

        return tokens;
    }

    private static List<WordInfo> TrackStage(TokenStage stage, List<WordInfo> input, ParserDiagnostics? diagnostics,
                                             TokenFeatureScan? scan = null)
    {
        var inputSnapshot = diagnostics != null ? input.Select(TokenSnapshot.From).ToList() : null;
        var sw = diagnostics != null ? Stopwatch.StartNew() : null;

        var result = stage.Apply(input, scan);

        if (diagnostics == null)
            return result;

        sw!.Stop();
        var outputSnapshot = result.Select(TokenSnapshot.From).ToList();
        var modifications = DetectModifications(inputSnapshot!, outputSnapshot);
        diagnostics.TokenStages.Add(new TokenProcessingStage
        {
            StageName = stage.Name,
            StageGroup = stage.Group.ToString(),
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            InputTokenCount = inputSnapshot!.Count,
            OutputTokenCount = outputSnapshot.Count,
            Modifications = modifications,
            InputTokens = modifications.Count > 0 ? inputSnapshot.Select(s => s.Text).ToList() : null,
            OutputTokens = modifications.Count > 0 ? outputSnapshot.Select(s => s.Text).ToList() : null
        });

        return result;
    }

    internal readonly record struct TokenSnapshot(
        string Text, PartOfSpeech PartOfSpeech, PartOfSpeechSection PartOfSpeechSection1, string Reading,
        string DictionaryForm, string NormalizedForm, int? PreMatchedWordId, byte? PreMatchedReadingIndex, bool IsInvalid)
    {
        public static TokenSnapshot From(WordInfo w) =>
            new(w.Text, w.PartOfSpeech, w.PartOfSpeechSection1, w.Reading,
                w.DictionaryForm, w.NormalizedForm, w.PreMatchedWordId, w.PreMatchedReadingIndex, w.IsInvalid);
    }

    internal static List<TokenModification> DetectModifications(List<TokenSnapshot> inputTokens, List<TokenSnapshot> outputTokens)
    {
        int start = 0;
        int endIn = inputTokens.Count, endOut = outputTokens.Count;
        while (start < endIn && start < endOut && inputTokens[start] == outputTokens[start])
            start++;
        while (endIn > start && endOut > start && inputTokens[endIn - 1] == outputTokens[endOut - 1])
        {
            endIn--;
            endOut--;
        }

        int n = endIn - start, m = endOut - start;
        if (n == 0 && m == 0)
            return [];

        var a = inputTokens.GetRange(start, n);
        var b = outputTokens.GetRange(start, m);

        if ((long)(n + 1) * (m + 1) > 4_000_000)
        {
            return
            [
                new TokenModification
                {
                    Type = "replace",
                    InputTokens = a.Select(t => t.Text).ToArray(),
                    OutputTokens = b.Select(t => t.Text).ToArray(),
                    InputIndex = start, OutputIndex = start,
                    Reason = $"{n} → {m} tokens (window too large for a detailed diff)"
                }
            ];
        }

        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i].Text == b[j].Text ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var modifications = new List<TokenModification>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x].Text == b[y].Text)
            {
                if (a[x] != b[y])
                {
                    modifications.Add(new TokenModification
                                      {
                                          Type = "reclassify", InputTokens = [a[x].Text], OutputTokens = [b[y].Text],
                                          InputIndex = start + x, OutputIndex = start + y,
                                          Reason = DescribeAttributeChange(a[x], b[y])
                                      });
                }

                x++;
                y++;
                continue;
            }

            int hunkX = x, hunkY = y;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[x].Text == b[y].Text)
                    break;
                if (x < n && (y >= m || lcs[x + 1, y] >= lcs[x, y + 1]))
                    x++;
                else
                    y++;
            }

            var hunk = ClassifyHunk(a.GetRange(hunkX, x - hunkX), b.GetRange(hunkY, y - hunkY));
            hunk.InputIndex = start + hunkX;
            hunk.OutputIndex = start + hunkY;
            modifications.Add(hunk);
        }

        return modifications;
    }

    private static string DescribeAttributeChange(TokenSnapshot before, TokenSnapshot after)
    {
        var parts = new List<string>();
        if (before.PartOfSpeech != after.PartOfSpeech)
            parts.Add($"POS {before.PartOfSpeech} → {after.PartOfSpeech}");
        if (before.PartOfSpeechSection1 != after.PartOfSpeechSection1)
            parts.Add($"POS section {before.PartOfSpeechSection1} → {after.PartOfSpeechSection1}");
        if (before.Reading != after.Reading)
            parts.Add($"reading {before.Reading} → {after.Reading}");
        if (before.DictionaryForm != after.DictionaryForm)
            parts.Add($"dictionary form {before.DictionaryForm} → {after.DictionaryForm}");
        if (before.NormalizedForm != after.NormalizedForm)
            parts.Add($"normalized form {before.NormalizedForm} → {after.NormalizedForm}");
        if (before.PreMatchedWordId != after.PreMatchedWordId)
            parts.Add($"pinned word {(before.PreMatchedWordId?.ToString() ?? "none")} → {(after.PreMatchedWordId?.ToString() ?? "none")}");
        if (before.PreMatchedReadingIndex != after.PreMatchedReadingIndex)
            parts.Add($"pinned reading {(before.PreMatchedReadingIndex?.ToString() ?? "none")} → {(after.PreMatchedReadingIndex?.ToString() ?? "none")}");
        if (before.IsInvalid != after.IsInvalid)
            parts.Add(after.IsInvalid ? "marked invalid" : "invalid flag cleared");
        return string.Join("; ", parts);
    }

    private static TokenModification ClassifyHunk(List<TokenSnapshot> removedSnaps, List<TokenSnapshot> addedSnaps)
    {
        var removed = removedSnaps.Select(t => t.Text).ToArray();
        var added = addedSnaps.Select(t => t.Text).ToArray();
        bool sameText = string.Concat(removed) == string.Concat(added);

        if (added.Length == 0)
        {
            return new TokenModification
                   {
                       Type = "remove", InputTokens = removed,
                       Reason = removed.Length == 1 ? "Token removed" : $"Removed {removed.Length} tokens"
                   };
        }

        if (removed.Length == 0)
        {
            return new TokenModification
                   {
                       Type = "insert", InputTokens = [], OutputTokens = added,
                       Reason = $"Inserted {added.Length} token{(added.Length == 1 ? "" : "s")}"
                   };
        }

        if (added.Length == 1)
        {
            return new TokenModification
                   {
                       Type = sameText ? "merge" : "replace", InputTokens = removed, OutputTokens = added,
                       Reason = sameText
                           ? $"Merged {removed.Length} tokens"
                           : $"Rewrote {removed.Length} token{(removed.Length == 1 ? "" : "s")} in place"
                   };
        }

        if (removed.Length == 1)
        {
            return new TokenModification
                   {
                       Type = sameText ? "split" : "replace", InputTokens = removed, OutputTokens = added,
                       Reason = sameText ? $"Split into {added.Length} tokens" : $"Rewrote 1 token into {added.Length}"
                   };
        }

        return new TokenModification
               {
                   Type = sameText ? "resegment" : "replace", InputTokens = removed, OutputTokens = added,
                   Reason = sameText
                       ? $"Moved boundaries across {removed.Length} → {added.Length} tokens"
                       : $"Rewrote {removed.Length} → {added.Length} tokens"
               };
    }
}
