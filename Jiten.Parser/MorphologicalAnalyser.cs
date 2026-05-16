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
                                                ParserDiagnostics? diagnostics = null,
                                                BenchmarkTimings? timings = null,
                                                byte[]? userDictCsv = null)
    {
        var results = await ParseBatch([text], morphemesOnly, preserveStopToken, diagnostics, timings, userDictCsv);
        return results.Count > 0 ? results[0] : [];
    }

    public async Task<(List<SentenceInfo> Sentences, string CleanedOriginal)> ParseWithCleanedOriginal(
        string text, bool morphemesOnly = false, bool preserveStopToken = false,
        ParserDiagnostics? diagnostics = null, BenchmarkTimings? timings = null,
        byte[]? userDictCsv = null)
    {
        var cleanedOriginals = new List<string>();
        var results = await ParseBatch([text], morphemesOnly, preserveStopToken, diagnostics, timings, userDictCsv, cleanedOriginals);
        return (results.Count > 0 ? results[0] : [], cleanedOriginals.Count > 0 ? cleanedOriginals[0] : "");
    }

    public Task<List<List<SentenceInfo>>> ParseBatch(List<string> texts, bool morphemesOnly = false, bool preserveStopToken = false,
                                                     ParserDiagnostics? diagnostics = null,
                                                     BenchmarkTimings? timings = null,
                                                     byte[]? userDictCsv = null,
                                                     List<string>? cleanedOriginals = null,
                                                     List<int>? rawContentCharCounts = null)
    {
        if (texts.Count == 0) return Task.FromResult<List<List<SentenceInfo>>>([]);

        diagnostics?.TokenStages.Clear();

        var runtimeSettings = ParserRuntimeSettings.Current;
        var dic = runtimeSettings.DictionaryPath;

        var sw = timings != null ? Stopwatch.StartNew() : null;

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
                cleanedOriginals?.Add("");
                rawContentCharCounts?.Add(0);
                continue;
            }

            PreprocessText(ref copy, preserveStopToken, out int rawCharCount);
            rawContentCharCounts?.Add(rawCharCount);
            processedTexts.Add(copy);

            var cleanedOriginal = copy.Replace(" ", "");
            if (!preserveStopToken)
                cleanedOriginal = cleanedOriginal.Replace(_stopToken, "");
            originalTexts.Add(cleanedOriginal);
            cleanedOriginals?.Add(cleanedOriginal);
        }

        // Join with batch delimiter (only if multiple texts)
        var combinedText = texts.Count == 1
            ? processedTexts[0]
            : string.Join($" {_batchDelimiter} ", processedTexts);

        if (sw != null) { timings!.TextPreprocessMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

        // Single Sudachi call
        var configPath = morphemesOnly
            ? runtimeSettings.SudachiNoUserDicConfigPath
            : runtimeSettings.SudachiConfigPath;

        var sudachiStopwatch = diagnostics != null ? Stopwatch.StartNew() : null;
        var mode = morphemesOnly ? 'A' : 'B';

        List<WordInfo> allWordInfos;

        bool useStreaming = SudachiInterop.StreamingAvailable && (diagnostics == null || userDictCsv != null);
        if (useStreaming)
        {
            allWordInfos = SudachiInterop.ProcessTextStreaming(configPath, combinedText, dic, mode: mode, userDictCsv: userDictCsv);
            sudachiStopwatch?.Stop();

            if (sw != null) { timings!.SudachiFFIMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            if (diagnostics != null)
            {
                diagnostics.Sudachi = new SudachiDiagnostics
                                      {
                                          ElapsedMs = sudachiStopwatch!.Elapsed.TotalMilliseconds,
                                          Tokens = allWordInfos.Select(w => new SudachiToken
                                          {
                                              Surface = w.Text,
                                              PartOfSpeech = w.PartOfSpeech.ToString(),
                                              DictionaryForm = w.DictionaryForm,
                                              Reading = w.Reading,
                                              NormalizedForm = w.NormalizedForm
                                          }).ToList()
                                      };
            }
        }
        else if (diagnostics != null)
        {
            var rawOutput = SudachiInterop.ProcessText(configPath, combinedText, dic, mode: mode);
            sudachiStopwatch?.Stop();

            if (sw != null) { timings!.SudachiFFIMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            diagnostics.Sudachi = new SudachiDiagnostics
                                  {
                                      ElapsedMs = sudachiStopwatch!.Elapsed.TotalMilliseconds, RawOutput = rawOutput,
                                      Tokens = ParseSudachiOutputToDiagnosticTokens(rawOutput)
                                  };

            var output = rawOutput.Split("\n");
            allWordInfos = new List<WordInfo>();
            foreach (var line in output)
            {
                if (line == "EOS") continue;
                var wi = new WordInfo(line);
                if (!wi.IsInvalid) allWordInfos.Add(wi);
            }
        }
        else
        {
            allWordInfos = SudachiInterop.ProcessTextStreaming(configPath, combinedText, dic, mode: mode, userDictCsv: userDictCsv);
            if (sw != null) { timings!.SudachiFFIMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }
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

        if (sw != null) { timings!.TokenParsingMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

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

            if (sw != null) { timings!.OffsetRecoveryMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            wordInfos = RunPipeline(wordInfos, diagnostics);

            if (sw != null) { timings!.PipelineMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }

            results.Add(SplitIntoSentences(originalTexts[i], wordInfos));

            if (sw != null) { timings!.SentenceSplitMs += sw.Elapsed.TotalMilliseconds; sw.Restart(); }
        }

        return Task.FromResult(results);
    }
}
