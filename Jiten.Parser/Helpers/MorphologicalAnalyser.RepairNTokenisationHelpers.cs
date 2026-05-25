using System.Text;
using Jiten.Core.Data;
using Jiten.Core.Utils;
using WanaKanaShaapu;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    private static string NormalizeToHiragana(string text) =>
        KanaConverter.ToNormalizedHiragana(text);

    private static bool IsNdaVerbForm(IReadOnlyList<DeconjugationForm> forms) =>
        forms.Any(f => f.Tags.Count > 0 &&
                       ((f.Tags.Any(t => t == "v5m") && f.Text.EndsWith("む")) ||
                        (f.Tags.Any(t => t == "v5n") && f.Text.EndsWith("ぬ")) ||
                        (f.Tags.Any(t => t == "v5b") && f.Text.EndsWith("ぶ")) ||
                        (f.Tags.Any(t => t == "v5g") && f.Text.EndsWith("ぐ"))));

    /// <summary>
    /// Checks if a verb ending in ん + だ is a valid past tense form (from む/ぬ/ぶ/ぐ verbs).
    /// Used to prevent combining negative ん (from ない/ぬ contraction) + copula だ (e.g., 知らん + だ).
    /// Returns false if the ん is from a slurred negative form.
    /// </summary>
    private bool IsValidNdaPastTense(string verbText)
    {
        if (!verbText.EndsWith("ん")) return false;
        var verbHiragana = NormalizeToHiragana(verbText);
        var verbForms = PipelineCachedDeconjugate(verbHiragana);
        if (verbForms.Any(f => f.Process.Any(p => p.Contains("slurred negative") || p.Contains("colloquial negative"))))
            return false;
        var candidate = NormalizeToHiragana(verbText + "だ");
        var forms = PipelineCachedDeconjugate(candidate);
        return IsNdaVerbForm(forms);
    }

    private static bool IsAnyVerbForm(IReadOnlyList<DeconjugationForm> forms) =>
        forms.Any(f => f.Tags.Count > 0 && f.Tags.Any(t => t.StartsWith("v")));

    private static bool IsMasenVerbForm(IReadOnlyList<DeconjugationForm> forms) =>
        forms.Any(f => f.Tags.Count > 0 && f.Tags.Any(t => t.StartsWith("v")) && !f.Text.EndsWith("ます"));

    private static WordInfo CreateNToken() => new()
                                              {
                                                  Text = "ん", DictionaryForm = "", NormalizedForm = "ん", Reading = "ん",
                                                  PartOfSpeech = PartOfSpeech.Auxiliary, PartOfSpeechSection1 = PartOfSpeechSection.None
                                              };

    private static WordInfo CreateDaToken() => new()
                                               {
                                                   Text = "だ", DictionaryForm = "だ", NormalizedForm = "だ", Reading = "だ",
                                                   PartOfSpeech = PartOfSpeech.Auxiliary, PartOfSpeechSection1 = PartOfSpeechSection.None
                                               };

    private static string BuildCandidateText(List<WordInfo> words, int lookback, string suffix)
    {
        var sb = new StringBuilder();
        for (int j = words.Count - lookback; j < words.Count; j++)
            sb.Append(words[j].Text);
        sb.Append(suffix);
        return sb.ToString();
    }

    private static string BuildCandidateReading(List<WordInfo> words, int lookback, string suffixReading)
    {
        var sb = new StringBuilder();
        for (int j = words.Count - lookback; j < words.Count; j++)
            sb.Append(words[j].Reading);
        sb.Append(suffixReading);
        return sb.ToString();
    }

    private static void RemoveLastN(List<WordInfo> list, int n)
    {
        for (int j = 0; j < n; j++)
            list.RemoveAt(list.Count - 1);
    }

    private static bool TryCombineWithLookback(
        List<WordInfo> result,
        string suffix,
        string suffixReading,
        Deconjugator deconj,
        Func<IReadOnlyList<DeconjugationForm>, bool> validator,
        out WordInfo? combined)
    {
        combined = null;

        // Skip na-adjective + な pattern entirely - the な belongs to the adjective, not to んだ
        // e.g., 好きなんだ should be 好きな + んだ, not 好き + なんだ or 好きなんだ
        if (result.Count >= 2)
        {
            var lastToken = result[^1];
            if (lastToken is { Text: "な", DictionaryForm: "だ" } &&
                IsNaAdjectiveToken(result[^2]))
            {
                return false;
            }
        }

        // Skip i-adjective + んだ pattern - the んだ is explanatory, not verb conjugation
        // e.g., いいんだ should be いい + んだ, not combined as verb form
        if (result.Count >= 1 && suffix.StartsWith("ん"))
        {
            var lastToken = result[^1];
            if (lastToken.PartOfSpeech == PartOfSpeech.IAdjective)
            {
                return false;
            }
        }

        for (int lookback = 1; lookback <= Math.Min(3, result.Count); lookback++)
        {
            bool hasBlankSpace = false;
            for (int j = result.Count - lookback; j < result.Count; j++)
            {
                if (result[j].PartOfSpeech != PartOfSpeech.BlankSpace)
                    continue;

                hasBlankSpace = true;
                break;
            }

            // Skip blank spaces since they will deconjugate to a correct form and break the parser
            if (hasBlankSpace) continue;

            // Skip when lookback includes て/で particle — ん after te-form is a contraction of いる (ている → てん), not past tense
            bool hasTeParticle = false;
            for (int j = result.Count - lookback; j < result.Count; j++)
            {
                if (result[j] is { Text: "て" or "で", PartOfSpeech: PartOfSpeech.Particle })
                {
                    hasTeParticle = true;
                    break;
                }
            }

            if (hasTeParticle) continue;

            var candidateText = BuildCandidateText(result, lookback, suffix);
            var forms = deconj.Deconjugate(NormalizeToHiragana(candidateText));

            if (validator(forms))
            {
                var baseWord = result[^lookback];
                var candidateReading = BuildCandidateReading(result, lookback, suffixReading);
                RemoveLastN(result, lookback);
                combined = new WordInfo(baseWord)
                {
                    Text = candidateText, PartOfSpeech = PartOfSpeech.Verb,
                    NormalizedForm = candidateText, Reading = KanaConverter.ToHiragana(candidateReading),
                    PartOfSpeechSection1 = PartOfSpeechSection.None,
                    PartOfSpeechSection2 = PartOfSpeechSection.None,
                    PartOfSpeechSection3 = PartOfSpeechSection.None
                };
                return true;
            }
        }

        return false;
    }

    private static bool IsNaAdjectiveToken(WordInfo word) =>
        word.PartOfSpeech == PartOfSpeech.NaAdjective ||
        word.HasPartOfSpeechSection(PartOfSpeechSection.PossibleNaAdjective) ||
        word.HasPartOfSpeechSection(PartOfSpeechSection.NaAdjectiveLike);
}
