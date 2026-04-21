using System.Diagnostics;
using System.Text;
using Jiten.Core.Data;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Runtime;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    public Func<string, bool>? HasCompoundLookup { get; set; }

    /// <summary>
    /// Parses the given text into a list of SentenceInfo objects by performing morphological analysis.
    /// Delegates to ParseBatch for a single codepath.
    /// </summary>
    /// <param name="text">The input text to be analyzed.</param>
    /// <param name="morphemesOnly">A boolean indicating whether the parsing should output only morphemes. When true, parsing will use mode 'A' for morpheme parsing.</param>
    /// <param name="preserveStopToken">A boolean indicating whether the stop token should be preserved in the processed text. Used in the ReaderController</param>
    /// <param name="diagnostics">Optional diagnostics container for verbose debug output.</param>
    /// <returns>A list of SentenceInfo objects representing the parsed output.</returns>
    public async Task<List<SentenceInfo>> Parse(string text, bool morphemesOnly = false, bool preserveStopToken = false,
                                                ParserDiagnostics? diagnostics = null)
    {
        var results = await ParseBatch([text], morphemesOnly, preserveStopToken, diagnostics);
        return results.Count > 0 ? results[0] : [];
    }

    /// <summary>
    /// Parses multiple texts in a single Sudachi call for efficiency.
    /// This is the main implementation - Parse() delegates here.
    /// </summary>
    /// <param name="texts">List of texts to parse.</param>
    /// <param name="morphemesOnly">A boolean indicating whether the parsing should output only morphemes.</param>
    /// <param name="preserveStopToken">A boolean indicating whether the stop token should be preserved.</param>
    /// <param name="diagnostics">Optional diagnostics container for verbose debug output.</param>
    /// <returns>List of SentenceInfo lists, one per input text.</returns>
    public Task<List<List<SentenceInfo>>> ParseBatch(List<string> texts, bool morphemesOnly = false, bool preserveStopToken = false,
                                                     ParserDiagnostics? diagnostics = null)
    {
        if (texts.Count == 0) return Task.FromResult<List<List<SentenceInfo>>>([]);

        diagnostics?.TokenStages.Clear();

        var runtimeSettings = ParserRuntimeSettings.Current;
        var dic = runtimeSettings.DictionaryPath;

        // Preprocess each text separately (preserves transformations per-text)
        const int sudachiMaxBytes = 49_000;
        var processedTexts = new List<string>(texts.Count);
        var originalTexts = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            var copy = text;
            if (!copy.Contains('\n') && Encoding.UTF8.GetByteCount(copy) > sudachiMaxBytes)
            {
                processedTexts.Add("");
                originalTexts.Add("");
                continue;
            }

            PreprocessText(ref copy, preserveStopToken);
            processedTexts.Add(copy);

            var cleanedOriginal = copy.Replace(" ", "");
            if (!preserveStopToken)
                cleanedOriginal = cleanedOriginal.Replace(_stopToken, "");
            originalTexts.Add(cleanedOriginal);
        }

        // Join with batch delimiter (only if multiple texts)
        var combinedText = texts.Count == 1
            ? processedTexts[0]
            : string.Join($" {_batchDelimiter} ", processedTexts);

        // Single Sudachi call
        var configPath = morphemesOnly
            ? runtimeSettings.SudachiNoUserDicConfigPath
            : runtimeSettings.SudachiConfigPath;

        var sudachiStopwatch = diagnostics != null ? Stopwatch.StartNew() : null;
        var mode = morphemesOnly ? 'A' : 'B';

        List<WordInfo> allWordInfos;

        // Use streaming when available and not in diagnostics mode
        if (diagnostics == null && SudachiInterop.StreamingAvailable)
        {
            allWordInfos = SudachiInterop.ProcessTextStreaming(configPath, combinedText, dic, mode: mode);
        }
        else
        {
            // Fall back to string-based ProcessText (needed for diagnostics raw output)
            var rawOutput = SudachiInterop.ProcessText(configPath, combinedText, dic, mode: mode);
            sudachiStopwatch?.Stop();

            if (diagnostics != null)
            {
                diagnostics.Sudachi = new SudachiDiagnostics
                                      {
                                          ElapsedMs = sudachiStopwatch!.Elapsed.TotalMilliseconds, RawOutput = rawOutput,
                                          Tokens = ParseSudachiOutputToDiagnosticTokens(rawOutput)
                                      };
            }

            var output = rawOutput.Split("\n");
            allWordInfos = new List<WordInfo>();
            foreach (var line in output)
            {
                if (line == "EOS") continue;
                var wi = new WordInfo(line);
                if (!wi.IsInvalid) allWordInfos.Add(wi);
            }
        }

        // Split by delimiter tokens (if batch)
        var batches = new List<List<WordInfo>>();
        if (texts.Count == 1)
        {
            batches.Add(allWordInfos);
        }
        else
        {
            var currentBatch = new List<WordInfo>();
            for (int j = 0; j < allWordInfos.Count; j++)
            {
                var wi = allWordInfos[j];

                // Sudachi tokenizes ||| as three separate | tokens
                if (wi.Text == _stopToken &&
                    j + 2 < allWordInfos.Count &&
                    allWordInfos[j + 1].Text == _stopToken &&
                    allWordInfos[j + 2].Text == _stopToken)
                {
                    batches.Add(currentBatch);
                    currentBatch = new List<WordInfo>();
                    j += 2;
                }
                else
                {
                    currentBatch.Add(wi);
                }
            }

            batches.Add(currentBatch); // Last batch
        }

        allWordInfos = null!;

        // Process each batch through normal pipeline
        var results = new List<List<SentenceInfo>>();
        for (int i = 0; i < batches.Count && i < originalTexts.Count; i++)
        {
            var wordInfos = batches[i];
            batches[i] = null!;

            if (morphemesOnly)
            {
                results.Add([new SentenceInfo("") { Words = wordInfos.Select(w => (w, 0, 0)).ToList() }]);
                continue;
            }

            ComputeTokenOffsets(originalTexts[i], wordInfos);

            // Split into sentences twice: once with raw tokens (captures Sudachi's
            // original segmentation), once after pipeline merges. The raw boundaries
            // flow to the beam so it can score against Sudachi's actual choice rather
            // than our pipeline's merged view.
            var rawSnapshot = new List<WordInfo>(wordInfos);
            var rawSentences = SplitIntoSentences(originalTexts[i], rawSnapshot);

            wordInfos = RunPipeline(wordInfos, diagnostics);
            var sentences = SplitIntoSentences(originalTexts[i], wordInfos);

            for (int si = 0; si < sentences.Count && si < rawSentences.Count; si++)
                sentences[si].RawSudachiBoundaries =
                    rawSentences[si].Words.Select(w => (w.position, w.length)).ToList();

            results.Add(sentences);
        }

        return Task.FromResult(results);
    }
}
