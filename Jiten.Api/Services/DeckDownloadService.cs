using System.Text;
using System.Text.RegularExpressions;
using AnkiNet;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using WanaKanaShaapu;

namespace Jiten.Api.Services;

public class DeckDownloadService(JitenDbContext context) : IDeckDownloadService
{
    public async Task<byte[]?> GenerateDownload(DeckDownloadRequest request, List<long> wordIds,
        string deckTitle, List<(int WordId, byte ReadingIndex, int Occurrences)> deckWords,
        List<int>? sentenceDeckIds)
    {
        var jmdictWords = await context.JMDictWords.AsNoTracking()
                                       .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                       .Where(w => wordIds.Contains(w.WordId))
                                       .ToDictionaryAsync(w => w.WordId);
        var intWordIds = wordIds.Select(wid => (int)wid).ToList();
        var exportForms = await WordFormHelper.LoadWordForms(context, intWordIds);
        var exportFormFreqs = await WordFormHelper.LoadWordFormFrequencies(context, intWordIds, request.FrequencySource);

        var wordToSentencesMap = new Dictionary<(int WordId, byte ReadingIndex), List<(string Text, byte Position, byte Length)>>();

        if (sentenceDeckIds is { Count: > 0 })
        {
            var exampleSentences = await context.ExampleSentences
                                                .AsNoTracking()
                                                .Where(es => sentenceDeckIds.Contains(es.DeckId))
                                                .Include(es => es.Words.Where(w => wordIds.Contains(w.WordId)))
                                                .ToListAsync();

            foreach (var sentence in exampleSentences)
            {
                foreach (var word in sentence.Words.Where(w => wordIds.Contains(w.WordId)))
                {
                    var key = (word.WordId, word.ReadingIndex);
                    if (!wordToSentencesMap.ContainsKey(key))
                        wordToSentencesMap[key] = new List<(string, byte, byte)>();

                    if (wordToSentencesMap[key].Count > 0)
                        continue;

                    wordToSentencesMap[key].Add((sentence.Text, word.Position, word.Length));
                }
            }
        }

        switch (request.Format)
        {
            case DeckFormat.Anki:

                var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "lapis.apkg");
                var template = await AnkiFileReader.ReadFromFileAsync(templatePath);
                var noteTypeTemplate = template.NoteTypes.First();

                var collection = new AnkiCollection();
                var noteTypeId = collection.CreateNoteType(noteTypeTemplate);
                var ankiDeckId = collection.CreateDeck(deckTitle);

                foreach (var word in deckWords)
                {
                    var ankiForm = exportForms.GetValueOrDefault((word.WordId, (short)word.ReadingIndex));
                    if (ankiForm == null) continue;
                    string expression = ankiForm.Text;

                    if (request.ExcludeKana && WanaKana.IsKana(expression))
                        continue;

                    string kanjiPatternPart = @"\p{IsCJKUnifiedIdeographs}";
                    string lookaheadPattern = $@"(?=(?:{kanjiPatternPart})*\[.*?\])";
                    string precedingKanjiLookbehind = $@"\p{{IsCJKUnifiedIdeographs}}{lookaheadPattern}";
                    string pattern = $"(?<!\\])(?<!{precedingKanjiLookbehind})({kanjiPatternPart}){lookaheadPattern}";
                    string expressionFurigana = Regex.Replace(ankiForm.RubyText, pattern, " $1");
                    string expressionReading = string.Join("", ankiForm.RubyText
                                                                       .Where(c => WanaKana.IsKana(c.ToString()))
                                                                       .Select(c => c.ToString()));
                    string expressionAudio = "";
                    string selectionText = "";

                    var definitions = jmdictWords[word.WordId].Definitions;
                    var definitionBuilder = new StringBuilder();
                    List<string>? previousPos = null;

                    for (var i = 0; i < definitions.Count; i++)
                    {
                        JmDictDefinition? definition = definitions[i];
                        bool isDifferentPartOfSpeech = previousPos == null || !previousPos.SequenceEqual(definition.PartsOfSpeech);
                        if (isDifferentPartOfSpeech)
                        {
                            if (i != 0)
                                definitionBuilder.Append("</ul>");
                            definitionBuilder.Append("<ul>");

                            previousPos = definition.PartsOfSpeech?.ToList() ?? [];

                            if (previousPos.Count > 0)
                            {
                                definitionBuilder.Append("<div class=\"def-pos\">");
                                definitionBuilder.Append(string.Join(" ",
                                                                     previousPos.Select(p =>
                                                                                            $"<span class=\"pos\" title=\"{JmDictHelper.ToHumanReadablePartsOfSpeech([p])[0]}\">{System.Net.WebUtility.HtmlEncode(p)}</span>")));
                                definitionBuilder.Append("</div>");
                            }
                        }

                        for (var j = 0; j < definition.EnglishMeanings.Count; j++)
                        {
                            string? meaning = definition.EnglishMeanings[j];
                            if (j == 0)
                                definitionBuilder.Append("<li>");
                            if (j != 0)
                                definitionBuilder.Append(" ; ");
                            definitionBuilder.Append(System.Net.WebUtility.HtmlEncode(meaning));
                            if (j == definition.EnglishMeanings.Count - 1)
                                definitionBuilder.Append("</li>");
                        }
                    }

                    definitionBuilder.Append("</ul>");

                    string mainDefinition = definitionBuilder.ToString();

                    string definitionPicture = "";
                    string sentence = "";

                    if (!request.ExcludeExampleSentences &&
                        wordToSentencesMap.TryGetValue((word.WordId, word.ReadingIndex), out var sentences) && sentences.Count > 0)
                    {
                        var exampleSentence = sentences.First();
                        int position = exampleSentence.Position;
                        int length = exampleSentence.Length;

                        string originalText = exampleSentence.Text;
                        if (position >= 0 && position + length <= originalText.Length)
                        {
                            sentence = originalText.Substring(0, position) +
                                       "<b>" +
                                       originalText.Substring(position, length) + "</b>" +
                                       originalText.Substring(position + length);
                        }
                    }

                    string sentenceFurigana = "";
                    string sentenceAudio = "";
                    string picture = "";
                    string glossary = "";
                    string hint = "";
                    string isWordAndSentenceCard = "";
                    string isClickCard = "";
                    string isSentenceCard = "";
                    string pitchPosition = "";
                    string pitchCategories = "";
                    var ankiFormFreq = exportFormFreqs.GetValueOrDefault((word.WordId, (short)word.ReadingIndex));
                    int ankiFreqRank = ankiFormFreq?.FrequencyRank ?? 0;
                    string frequency =
                        $"<ul><li>Jiten: {word.Occurrences} occurrences ; #{ankiFreqRank} global rank</li></ul>";
                    string freqSort = $"{ankiFreqRank}";
                    string isAudioCard = "";
                    string occurrences = $"{word.Occurrences}";
                    string miscInfo = $"From {deckTitle} - generated by Jiten.moe";

                    if (jmdictWords[word.WordId].PitchAccents != null)
                        pitchPosition = string.Join(",", jmdictWords[word.WordId].PitchAccents!.Select(p => p.ToString()));

                    collection.CreateNote(ankiDeckId, noteTypeId,
                                          expression, expressionFurigana,
                                          expressionReading, expressionAudio, selectionText, mainDefinition, definitionPicture,
                                          sentence, sentenceFurigana,
                                          sentenceAudio, picture, glossary, hint,
                                          isWordAndSentenceCard, isClickCard, isSentenceCard,
                                          pitchPosition, pitchCategories,
                                          frequency, freqSort, miscInfo,
                                          isAudioCard, occurrences
                                         );
                }

                var stream = new MemoryStream();

                await AnkiFileWriter.WriteToStreamAsync(stream, collection);
                var bytes = stream.ToArray();

                return bytes;

            case DeckFormat.Csv:
                StringBuilder sb = new StringBuilder();

                sb.AppendLine($"\"Word\",\"ReadingFurigana\",\"ReadingKana\",\"Occurences\",\"ReadingFrequency\",\"PitchPositions\",\"Definitions\",\"ExampleSentence\",\"JmDictWordId\"");

                foreach (var word in deckWords)
                {
                    var csvForm = exportForms.GetValueOrDefault((word.WordId, (short)word.ReadingIndex));
                    if (csvForm == null) continue;
                    string reading = csvForm.Text;

                    if (request.ExcludeKana && WanaKana.IsKana(reading))
                        continue;

                    string readingFurigana = csvForm.RubyText;
                    string pitchPositions = "";

                    if (jmdictWords[word.WordId].PitchAccents != null)
                        pitchPositions = string.Join(",", jmdictWords[word.WordId].PitchAccents!.Select(p => p.ToString()));

                    string readingKana = string.Join("", csvForm.RubyText
                                                                .Where(c => WanaKana.IsKana(c.ToString()))
                                                                .Select(c => c.ToString()));
                    string csvDefinitions = string.Join(",", jmdictWords[word.WordId].Definitions
                                                                                  .SelectMany(d => d.EnglishMeanings)
                                                                                  .Select(m => m.Replace("\"", "\"\"")));
                    var csvOccurrences = word.Occurrences;
                    var csvFormFreq = exportFormFreqs.GetValueOrDefault((word.WordId, (short)word.ReadingIndex));
                    var readingFrequency = csvFormFreq?.FrequencyRank ?? 0;

                    string exampleSentence = "";
                    if (!request.ExcludeExampleSentences &&
                        wordToSentencesMap.TryGetValue((word.WordId, word.ReadingIndex), out var csvSentences) && csvSentences.Count > 0)
                    {
                        var csvSentence = csvSentences.First();
                        int position = csvSentence.Position;
                        int length = csvSentence.Length;

                        string originalText = csvSentence.Text;
                        if (position >= 0 && position + length <= originalText.Length)
                        {
                            exampleSentence = originalText.Substring(0, position) +
                                              "**" +
                                              originalText.Substring(position, length) + "**" +
                                              originalText.Substring(position + length);
                        }
                    }

                    sb.AppendLine($"\"{reading}\",\"{readingFurigana}\",\"{readingKana}\",\"{csvOccurrences}\",\"{readingFrequency}\",\"{pitchPositions}\",\"{csvDefinitions}\",\"{exampleSentence}\",\"{word.WordId}\"");
                }

                return Encoding.UTF8.GetBytes(sb.ToString());
            case DeckFormat.Txt:
                StringBuilder txtSb = new StringBuilder();
                foreach (var word in deckWords)
                {
                    var txtForm = exportForms.GetValueOrDefault((word.WordId, (short)word.ReadingIndex));
                    if (txtForm == null) continue;
                    string reading = txtForm.Text;
                    if (request.ExcludeKana && WanaKana.IsKana(reading))
                        continue;

                    txtSb.AppendLine(reading);
                }

                return Encoding.UTF8.GetBytes(txtSb.ToString());

            case DeckFormat.TxtRepeated:
                StringBuilder txtRepeatedSb = new StringBuilder();
                foreach (var word in deckWords)
                {
                    var txtRepForm = exportForms.GetValueOrDefault((word.WordId, (short)word.ReadingIndex));
                    if (txtRepForm == null) continue;
                    string reading = txtRepForm.Text;
                    if (request.ExcludeKana && WanaKana.IsKana(reading))
                        continue;

                    for (int i = 0; i < Math.Max(1, word.Occurrences); i++)
                        txtRepeatedSb.AppendLine(reading);
                }

                return Encoding.UTF8.GetBytes(txtRepeatedSb.ToString());

            default:
                return null;
        }
    }
}
