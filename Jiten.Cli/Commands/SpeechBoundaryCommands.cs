using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.SpeechBoundaries;

namespace Jiten.Cli.Commands;

public class SpeechBoundaryCommands(CliContext context)
{
    private const int SampleSeed = 20260924;
    private const double MinKanaLineShare = 0.3;

    private static readonly string[] ExtensionPreference = [".srt", ".ssa", ".ass"];
    private static readonly char[] SpeakerDashes = ['-', '－', '‐', '―', '–', '—'];
    private static readonly char[] ContinuationMarks = ['、', '，'];
    // Subtitles mark a cue that runs on into the next one with a trailing arrow or dash.
    private static readonly char[] ContinuationMarkers = ['➡', '→', '―', '—'];
    private static readonly char[] SentenceEnders = ['。', '！', '？', '!', '?', '｡'];

    private long _filesDone;
    private long _filesSkippedNonJapanese;
    private long _filesMisaligned;
    private long _filesFailed;
    private long _rows;
    private long _positives;
    private readonly ConcurrentBag<string> _misalignedPaths = [];

    public async Task Export(CliOptions options)
    {
        var outputPath = options.ExportSpeechBoundaries!;
        var roots = options.SpeechBoundaryRoots?.ToList() ?? [];
        if (roots.Count == 0)
        {
            Console.WriteLine("--speech-boundary-roots is required.");
            return;
        }

        var files = SampleFiles(roots, options.SpeechBoundaryFilesPerFolder);
        Console.WriteLine($"Sampled {files.Count:N0} subtitle files ({options.SpeechBoundaryFilesPerFolder} per folder) from {roots.Count} roots");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        var writeLock = new object();
        await writer.WriteLineAsync(string.Join('\t',
            new[] { "group", "file", "idx", "next_idx", "label", "cue_end" }
                .Concat(LineBreakFeatureExtractor.CategoricalNames)
                .Concat(LineBreakFeatureExtractor.NumericNames)
                .Concat(["prev", "next"])));

        var sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (job, _) =>
            {
                string? block;
                try
                {
                    block = await ProcessFile(job.Group, job.Path);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _filesFailed);
                    block = null;
                }

                if (block != null)
                {
                    lock (writeLock)
                        writer.Write(block);
                }

                var done = Interlocked.Increment(ref _filesDone);
                if (done % 500 == 0)
                    Report(done, files.Count, sw.Elapsed);
            });

        Report(_filesDone, files.Count, sw.Elapsed);
        Console.WriteLine($"Written to {outputPath}");

        if (!_misalignedPaths.IsEmpty)
        {
            var misalignedPath = outputPath + ".misaligned.txt";
            await File.WriteAllLinesAsync(misalignedPath, _misalignedPaths.Order(StringComparer.Ordinal));
            Console.WriteLine($"Misaligned files listed in {misalignedPath}");
        }
    }

    public void Parity(string modelPath)
    {
        var model = SpeechBoundaryModel.Load(modelPath);
        var parityPath = Path.ChangeExtension(modelPath, null) + ".parity.tsv";
        var lines = File.ReadAllLines(parityPath);
        var header = lines[0].Split('\t');
        int categoricalCount = LineBreakFeatureExtractor.CategoricalNames.Length;
        int numericCount = LineBreakFeatureExtractor.NumericNames.Length;
        int probColumn = Array.IndexOf(header, "prob");

        var rows = lines.Skip(1).Select(line =>
        {
            var fields = line.Split('\t');
            var breakFeatures = new LineBreakFeatureExtractor.LineBreak(
                0, 1,
                fields[..categoricalCount],
                fields[categoricalCount..(categoricalCount + numericCount)]
                    .Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            return (Break: breakFeatures, Expected: double.Parse(fields[probColumn], System.Globalization.CultureInfo.InvariantCulture));
        }).ToList();

        double maxDiff = 0;
        foreach (var (lineBreak, expected) in rows)
            maxDiff = Math.Max(maxDiff, Math.Abs(model.Predict(lineBreak) - expected));

        var encoded = rows.Select(r => model.Encode(r.Break)).ToArray();
        const int Rounds = 20;
        double sink = 0;
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < Rounds; round++)
            foreach (var features in encoded)
                sink += model.PredictEncoded(features);
        sw.Stop();

        Console.WriteLine($"{rows.Count:N0} rows, max |C# - LightGBM| = {maxDiff:E2}");
        Console.WriteLine($"{sw.Elapsed.TotalMilliseconds * 1000 / (Rounds * encoded.Length):F2} µs per decision (single thread, checksum {sink:F0})");
    }

    private const int PreviewMaxFiles = 300;
    private const int UsableMinChars = 8;
    private const int UsableMaxChars = 45;
    private static readonly char[] TodaysEnders = ['。', '！', '？', '」'];

    public async Task Preview(CliOptions options)
    {
        if (options.SpeechBoundaryModel == null)
        {
            Console.WriteLine("--speech-boundary-model is required.");
            return;
        }

        var model = SpeechBoundaryModel.Load(options.SpeechBoundaryModel);
        var path = options.SpeechPreview!;
        var files = File.Exists(path)
            ? [path]
            : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                       .Where(f => ExtensionPreference.Contains(Path.GetExtension(f).ToLowerInvariant()))
                       .Order(StringComparer.Ordinal)
                       .OrderBy(_ => Random.Shared.Next())
                       .Take(PreviewMaxFiles)
                       .ToList();

        var today = new SentenceStats();
        var assembled = new SentenceStats();
        foreach (var file in files)
        {
            var lines = (await ExtractWithoutTouchingArchive(file)).Select(l => l.Text).ToList();
            if (lines.Count == 0)
                continue;

            var todaysSentences = CutOnEnders(string.Concat(lines)).ToList();
            var boundaries = SpeechTextAssembler.DetectBoundaries(lines, model);
            var newSentences = SpeechTextAssembler.Assemble(lines, boundaries)
                                                  .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                                  .SelectMany(CutOnEnders)
                                                  .ToList();
            today.Add(todaysSentences);
            assembled.Add(newSentences);

            if (files.Count == 1)
            {
                Console.WriteLine("Sentences with model boundaries:");
                foreach (var sentence in newSentences)
                    Console.WriteLine($"  {(IsUsable(sentence) ? ' ' : '·')} {sentence}");
                Console.WriteLine();
                Console.WriteLine("Longest sentences today:");
                foreach (var sentence in todaysSentences.OrderByDescending(ContentLength).Take(10))
                    Console.WriteLine($"    {sentence}");
                Console.WriteLine();
                await PreviewFullParse(string.Join('\n', lines));
            }
        }

        Console.WriteLine($"{files.Count} files; usable = {UsableMinChars}-{UsableMaxChars} content chars");
        Console.WriteLine($"  today:          {today}");
        Console.WriteLine($"  model + joins:  {assembled}");
    }

    /// <summary>Runs the real parser as an anime deck twice: once computing the bitmap, once reusing it, which must give the same sentences.</summary>
    private async Task PreviewFullParse(string rawText)
    {
        await Parser.Parser.ParseTextToDeck(context.ContextFactory, rawText, predictDifficulty: false, mediatype: MediaType.Anime);
        var sw = Stopwatch.StartNew();
        var first = await Parser.Parser.ParseTextToDeck(context.ContextFactory, rawText, storeRawText: true, predictDifficulty: false,
                                                        mediatype: MediaType.Anime);
        var firstMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var second = await Parser.Parser.ParseTextToDeck(context.ContextFactory, rawText, storeRawText: true, predictDifficulty: false,
                                                         mediatype: MediaType.Anime, speechBoundaries: first.RawText!.SpeechBoundaries);
        var secondMs = sw.ElapsedMilliseconds;

        var sentences = first.ExampleSentences?.Select(s => s.Text).ToList() ?? [];
        var reused = second.ExampleSentences?.Select(s => s.Text).ToList() ?? [];
        Console.WriteLine($"Full parse as anime: {first.SentenceCount} sentences, {sentences.Count} example sentences, " +
                          $"bitmap {first.RawText.SpeechBoundaries?.Length ?? 0} bytes, {firstMs} ms; " +
                          $"with stored bitmap {secondMs} ms, identical: {sentences.SequenceEqual(reused)}");
        foreach (var sentence in sentences.Take(40))
            Console.WriteLine($"    {sentence}");
        Console.WriteLine();
    }

    private sealed class SentenceStats
    {
        private long _sentences;
        private long _usable;
        private long _contentChars;
        private long _tooLong;

        public void Add(List<string> sentences)
        {
            foreach (var sentence in sentences)
            {
                var length = ContentLength(sentence);
                _sentences++;
                _contentChars += length;
                if (length > UsableMaxChars)
                    _tooLong++;
                else if (length >= UsableMinChars)
                    _usable++;
            }
        }

        public override string ToString() =>
            $"{_sentences:N0} sentences, {(double)_usable / Math.Max(_sentences, 1):P1} usable, " +
            $"{(double)_tooLong / Math.Max(_sentences, 1):P1} over {UsableMaxChars} chars, " +
            $"{_usable * 1000.0 / Math.Max(_contentChars, 1):F1} usable per 1000 chars";
    }

    private static bool IsUsable(string sentence) => ContentLength(sentence) is >= UsableMinChars and <= UsableMaxChars;

    private static int ContentLength(string text) =>
        text.Count(c => c is >= 'ぁ' and <= 'ヺ' or 'ー' or >= '一' and <= '龯' or '々' or >= '０' and <= '９' or >= 'Ａ' and <= 'ｚ');

    /// <summary>Mirrors the prose splitter: a sentence ends after a run of enders.</summary>
    private static IEnumerable<string> CutOnEnders(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(TodaysEnders, text[i]) < 0)
                continue;

            while (i + 1 < text.Length && Array.IndexOf(TodaysEnders, text[i + 1]) >= 0)
                i++;
            var sentence = text[start..(i + 1)].Trim();
            if (sentence.Length > 0)
                yield return sentence;
            start = i + 1;
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
            yield return tail;
    }

    private void Report(long done, int total, TimeSpan elapsed)
    {
        var positiveShare = _rows == 0 ? 0 : (double)_positives / _rows;
        Console.WriteLine($"  {done:N0}/{total:N0} files, {_rows:N0} breaks ({positiveShare:P1} boundaries), " +
                          $"skipped: {_filesSkippedNonJapanese:N0} non-Japanese, {_filesMisaligned:N0} misaligned, {_filesFailed:N0} failed, " +
                          $"{elapsed.TotalMinutes:F1} min");
    }

    private static List<(string Group, string Path)> SampleFiles(List<string> roots, int perFolder)
    {
        var rng = new Random(SampleSeed);
        var jobs = new List<(string, string)>();

        foreach (var root in roots)
        {
            var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                // An .ssa next to a same-named .ass is the extractor's own converted copy of it.
                var candidates = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                                          .Where(f => ExtensionPreference.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                          .GroupBy(f => Path.ChangeExtension(f, null), StringComparer.OrdinalIgnoreCase)
                                          .Select(g => g.OrderBy(f => Array.IndexOf(ExtensionPreference, Path.GetExtension(f).ToLowerInvariant())).First())
                                          .Order(StringComparer.Ordinal)
                                          .ToList();

                var group = $"{rootName}/{Path.GetFileName(folder)}";
                foreach (var file in candidates.OrderBy(_ => rng.Next()).Take(perFolder))
                    jobs.Add((group, file));
            }
        }

        return jobs;
    }

    private async Task<string?> ProcessFile(string group, string path)
    {
        var lines = await ExtractWithoutTouchingArchive(path);
        if (lines.Count < 2)
            return null;

        if (lines.Count(l => ContainsKana(l.Text)) < lines.Count * MinKanaLineShare)
        {
            Interlocked.Increment(ref _filesSkippedNonJapanese);
            return null;
        }

        var set = LineBreakFeatureExtractor.Extract(lines.Select(l => l.Text).ToList());
        if (set == null)
        {
            Interlocked.Increment(ref _filesMisaligned);
            _misalignedPaths.Add(path);
            return null;
        }

        var sb = new StringBuilder();
        var fileName = Clean(Path.GetFileName(path));
        int positives = 0;
        foreach (var lineBreak in set.Breaks)
        {
            var prev = set.CleanedLines[lineBreak.PrevLine];
            var next = set.CleanedLines[lineBreak.NextLine];
            // Lines dropped by cleaning can hold the cue end, so any cue end up to the next kept line counts.
            var cueEnd = Enumerable.Range(lineBreak.PrevLine, lineBreak.NextLine - lineBreak.PrevLine).Any(i => lines[i].EndsCue);
            var label = IsSentenceEnd(prev, next, cueEnd);
            if (label)
                positives++;

            sb.Append(group).Append('\t').Append(fileName).Append('\t').Append(lineBreak.PrevLine).Append('\t').Append(lineBreak.NextLine)
              .Append('\t').Append(label ? '1' : '0').Append('\t').Append(cueEnd ? '1' : '0');

            foreach (var value in lineBreak.Categorical)
                sb.Append('\t').Append(Clean(value));
            foreach (var value in lineBreak.Numeric)
                sb.Append('\t').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            sb.Append('\t').Append(Clean(prev)).Append('\t').Append(Clean(next)).Append('\n');
        }

        Interlocked.Add(ref _rows, set.Breaks.Length);
        Interlocked.Add(ref _positives, positives);
        return sb.ToString();
    }

    /// <summary>A cue end is a sentence end unless the cue signals it continues; a speaker dash starts a new sentence even mid-cue.</summary>
    private static bool IsSentenceEnd(string prev, string next, bool cueEnd)
    {
        if (next.Length > 0 && SpeakerDashes.Contains(next[0]))
            return true;

        if (!cueEnd)
            return false;

        var text = prev.TrimEnd();
        if (text.Length == 0)
            return true;

        // After a finished sentence a continuation marker only means the same speaker goes on.
        if (ContinuationMarkers.Contains(text[^1]))
        {
            var beforeMarker = text.TrimEnd(ContinuationMarkers).TrimEnd();
            return beforeMarker.Length > 0 && SentenceEnders.Contains(beforeMarker[^1]);
        }

        return !ContinuationMarks.Contains(text[^1]);
    }

    /// <summary>The extractor rewrites .ass/.ssa files in place, so those are read from a temporary copy.</summary>
    private static async Task<List<CueLine>> ExtractWithoutTouchingArchive(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".srt")
            return await new SubtitleExtractor().ExtractCueLines(path);

        var tempDir = Path.Combine(Path.GetTempPath(), "jiten-speech-boundaries", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var copy = Path.Combine(tempDir, "sub" + extension);
            File.Copy(path, copy);
            return await new SubtitleExtractor().ExtractCueLines(copy);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static bool ContainsKana(string text) => text.Any(c => c is >= 'ぁ' and <= 'ヺ');

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
