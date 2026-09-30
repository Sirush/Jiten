using System.Diagnostics;
using System.Text;
using Jiten.Core.Data;
using Jiten.Parser.SpeechBoundaries;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.Commands;

public class YouTubeSpeechCommands(CliContext context)
{
    private sealed record Video(string Channel, int DeckId, string RawText);

    private sealed class ChannelStats
    {
        public int Videos;
        public int Trusted;
        public int ModelOnly;
        public long Sentences;
        public long Usable;
        public int? SampleDeckId;
    }

    public async Task Preview(string target, string? videosCsv)
    {
        if (int.TryParse(target, out var deckId))
        {
            await PreviewVideo(deckId);
            return;
        }

        var stats = new Dictionary<string, ChannelStats>();
        await foreach (var video in StreamVideos(videosCsv))
        {
            if (!stats.TryGetValue(video.Channel, out var channel))
                stats[video.Channel] = channel = new ChannelStats();

            channel.Videos++;
            var (text, boundaries) = YouTubeSpeechText.Prepare(video.RawText, SpeechBoundaryModel.YouTube);
            if (boundaries == null)
                continue;

            if (IsTrusted(video.RawText))
                channel.Trusted++;
            else
                channel.ModelOnly++;
            channel.SampleDeckId ??= video.DeckId;
            foreach (var sentence in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(SpeechBoundaryCommands.CutOnEnders))
            {
                channel.Sentences++;
                if (SpeechBoundaryCommands.IsUsable(sentence))
                    channel.Usable++;
            }
        }

        Console.WriteLine($"{"videos",7} {"trusted",8} {"model",6} {"sentences",10} {"usable",8} {"sample",8}  channel");
        foreach (var (name, channel) in stats.OrderByDescending(kv => kv.Value.Usable))
            Console.WriteLine($"{channel.Videos,7} {channel.Trusted,8} {channel.ModelOnly,6} {channel.Sentences,10:N0} {channel.Usable,8:N0} {channel.SampleDeckId,8}  {name}");

        Console.WriteLine($"{stats.Values.Sum(c => c.Videos),7} {stats.Values.Sum(c => c.Trusted),8} {stats.Values.Sum(c => c.ModelOnly),6} " +
                          $"{stats.Values.Sum(c => c.Sentences),10:N0} {stats.Values.Sum(c => c.Usable),8:N0} {"",8}  TOTAL");
    }

    private async Task PreviewVideo(int deckId)
    {
        await using var db = await context.ContextFactory.CreateDbContextAsync();
        var rawText = await db.DeckRawTexts.AsNoTracking()
                              .Where(rt => rt.DeckId == deckId)
                              .Select(rt => rt.RawText)
                              .FirstOrDefaultAsync();
        if (rawText == null)
        {
            Console.WriteLine($"Deck {deckId} has no raw text.");
            return;
        }

        var (text, boundaries) = YouTubeSpeechText.Prepare(rawText, SpeechBoundaryModel.YouTube);
        Console.WriteLine(boundaries == null ? "No boundaries: features could not be extracted."
                          : IsTrusted(rawText) ? "Punctuation trusted, plus confident model cuts."
                          : "Punctuation not trusted: boundaries from the YouTube model.");
        if (boundaries != null)
        {
            foreach (var sentence in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(SpeechBoundaryCommands.CutOnEnders).Take(60))
                Console.WriteLine($"  {(SpeechBoundaryCommands.IsUsable(sentence) ? ' ' : '·')} {sentence}");
        }

        var deck = await Parser.Parser.ParseTextToDeck(context.ContextFactory, rawText, storeRawText: true, predictDifficulty: false,
                                                       mediatype: MediaType.YouTube);
        var examples = deck.ExampleSentences?.Select(s => s.Text).ToList() ?? [];
        Console.WriteLine();
        Console.WriteLine($"Full parse as YouTube: {deck.SentenceCount} sentences, {deck.UniqueWordCount} unique words, " +
                          $"{examples.Count} example sentences, bitmap {deck.RawText?.SpeechBoundaries?.Length ?? 0} bytes");
        foreach (var sentence in examples.Take(40))
            Console.WriteLine($"    {sentence}");
    }

    /// <summary>Trusted videos with their punctuation stripped look like the untrusted ones the model must handle; their punctuation becomes the label.</summary>
    public async Task Export(string outputPath, string? videosCsv)
    {
        var videos = new List<Video>();
        await foreach (var video in StreamVideos(videosCsv))
            videos.Add(video);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        var writeLock = new object();
        await writer.WriteLineAsync(string.Join('\t',
            new[] { "group", "file", "idx", "next_idx", "label", "cue_end" }
                .Concat(LineBreakFeatureExtractor.CategoricalNames)
                .Concat(LineBreakFeatureExtractor.NumericNames)
                .Concat(["prev", "next"])));

        long trusted = 0, misaligned = 0, rows = 0, positives = 0;
        var sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(videos, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (video, _) =>
            {
                var lines = YouTubeSpeechText.Normalise(video.RawText).Split('\n');
                var cleaned = lines.Select(SpeechLineCleaner.Clean).ToArray();
                if (!YouTubeSpeechText.HasTrustedPunctuation(cleaned))
                    return ValueTask.CompletedTask;

                Interlocked.Increment(ref trusted);
                var labels = YouTubeSpeechText.PunctuationBoundaries(cleaned);
                var stripped = cleaned.Select(YouTubeSpeechText.StripPunctuation).ToArray();
                var set = LineBreakFeatureExtractor.Extract(stripped);
                if (set == null)
                {
                    Interlocked.Increment(ref misaligned);
                    return ValueTask.CompletedTask;
                }

                var sb = new StringBuilder();
                var group = Clean($"youtube/{video.Channel}");
                int videoPositives = 0;
                foreach (var lineBreak in set.Breaks)
                {
                    // A line that held only punctuation is dropped once stripped, so its label moves to the kept line before it.
                    var label = Enumerable.Range(lineBreak.PrevLine, lineBreak.NextLine - lineBreak.PrevLine).Any(i => labels[i]);
                    if (label)
                        videoPositives++;

                    sb.Append(group).Append('\t').Append(video.DeckId).Append('\t').Append(lineBreak.PrevLine).Append('\t').Append(lineBreak.NextLine)
                      .Append('\t').Append(label ? '1' : '0').Append("\t1");

                    foreach (var value in lineBreak.Categorical)
                        sb.Append('\t').Append(Clean(value));
                    foreach (var value in lineBreak.Numeric)
                        sb.Append('\t').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    sb.Append('\t').Append(Clean(set.CleanedLines[lineBreak.PrevLine])).Append('\t').Append(Clean(set.CleanedLines[lineBreak.NextLine])).Append('\n');
                }

                lock (writeLock)
                    writer.Write(sb.ToString());

                Interlocked.Add(ref rows, set.Breaks.Length);
                Interlocked.Add(ref positives, videoPositives);
                return ValueTask.CompletedTask;
            });

        Console.WriteLine($"{videos.Count:N0} videos, {trusted:N0} trusted, {misaligned:N0} misaligned; " +
                          $"{rows:N0} breaks ({(double)positives / Math.Max(rows, 1):P1} boundaries), {sw.Elapsed.TotalMinutes:F1} min");
        Console.WriteLine($"Written to {outputPath}");
    }

    private async IAsyncEnumerable<Video> StreamVideos(string? videosCsv)
    {
        if (videosCsv != null)
        {
            foreach (var video in ReadVideosCsv(videosCsv))
                yield return video;
            yield break;
        }

        await using var db = await context.ContextFactory.CreateDbContextAsync();
        var query = from source in db.YouTubeSources
                    join deck in db.Decks on (int?)source.DeckId equals deck.ParentDeckId
                    join raw in db.DeckRawTexts on deck.DeckId equals raw.DeckId
                    orderby source.DeckId, deck.DeckId
                    select new Video(source.ChannelName, deck.DeckId, raw.RawText);

        await foreach (var video in query.AsNoTracking().AsAsyncEnumerable())
            yield return video;
    }

    /// <summary>Reads the deck_id, channel_b64, raw_b64 export; base64 keeps every field free of delimiters, quotes and newlines.</summary>
    private static IEnumerable<Video> ReadVideosCsv(string path)
    {
        char[] delimiters = [',', ';', '\t'];
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var fields = line.Split(delimiters).Select(f => f.Trim().Trim('"')).ToArray();
            if (fields.Length < 3 || !int.TryParse(fields[0], out var deckId))
                continue;

            yield return new Video(Encoding.UTF8.GetString(Convert.FromBase64String(fields[1])), deckId,
                                   Encoding.UTF8.GetString(Convert.FromBase64String(fields[2])));
        }
    }

    private static bool IsTrusted(string rawText) =>
        YouTubeSpeechText.HasTrustedPunctuation(YouTubeSpeechText.Normalise(rawText).Split('\n').Select(SpeechLineCleaner.Clean).ToArray());

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
