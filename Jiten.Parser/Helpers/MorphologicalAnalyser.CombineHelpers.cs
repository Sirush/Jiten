using Jiten.Core.Data;
using Jiten.Core.Utils;
using WanaKanaShaapu;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    private bool CompoundExistsInLookup(string compoundForm, Func<string, IReadOnlyList<DeconjugationForm>> cachedDeconjugate)
    {
        if (HasCompoundLookup!(compoundForm))
            return true;

        foreach (var form in cachedDeconjugate(compoundForm))
        {
            if (HasCompoundLookup(form.Text))
                return true;
        }

        return false;
    }

    internal static readonly HashSet<char> DictionaryVerbEndings =
        ['う', 'く', 'ぐ', 'す', 'つ', 'ぬ', 'ぶ', 'む', 'る'];

    internal static string? TryGodanDictForm(string text)
    {
        if (text.Length < 2) return null;
        var dictEnding = text[^1] switch
        {
            'い' => 'う', 'き' => 'く', 'ぎ' => 'ぐ', 'し' => 'す',
            'ち' => 'つ', 'に' => 'ぬ', 'び' => 'ぶ', 'み' => 'む', 'り' => 'る',
            _ => '\0'
        };
        return dictEnding == '\0' ? null : text[..^1] + dictEnding;
    }

    private bool VerbDictFormExistsInLookup(string dictForm, string? normalizedForm, Func<string, IReadOnlyList<DeconjugationForm>> cachedDeconjugate)
    {
        if (HasCompoundLookup!(dictForm))
            return true;

        if (normalizedForm != null && normalizedForm != dictForm && HasCompoundLookup(normalizedForm))
            return true;

        foreach (var form in cachedDeconjugate(dictForm))
        {
            if (form.Text.Length > 0 && DictionaryVerbEndings.Contains(form.Text[^1]) &&
                HasCompoundLookup(form.Text))
                return true;
        }

        return false;
    }

    private List<WordInfo> CombineVerbDependants(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        WordInfo currentWord = wordInfos[0];
        bool isCopy = false;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo nextWord = wordInfos[i];

            if (nextWord.HasPartOfSpeechSection(PartOfSpeechSection.Dependant) &&
                currentWord.PartOfSpeech == PartOfSpeech.Verb &&
                nextWord.DictionaryForm != "おる" &&
                nextWord.Text != currentWord.Text &&
                // くて is always an i-adjective te-form (verb te-forms are って/いて); dependant
                // auxiliaries attach to verb te-forms only (頭が良くて + やりたい stays split)
                !currentWord.Text.EndsWith("くて", StringComparison.Ordinal))
            {
                if (newList == null) { newList = CopyAccumulatorUpTo(wordInfos, i - 1); }
                if (!isCopy) { currentWord = new WordInfo(currentWord); isCopy = true; }
                currentWord.Text += nextWord.Text;
                currentWord.EndOffset = nextWord.EndOffset;
                currentWord.Reading += nextWord.Reading;
            }
            else
            {
                newList?.Add(currentWord);
                currentWord = nextWord;
                isCopy = false;
            }
        }

        if (newList == null) return wordInfos;
        newList.Add(currentWord);
        return newList;
    }

    private List<WordInfo> CombineVerbPossibleDependants(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        WordInfo currentWord = wordInfos[0];
        bool isCopy = false;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo nextWord = wordInfos[i];

            bool isClassicalWaRowTeForm = nextWord.DictionaryForm.EndsWith("う") &&
                                          nextWord.Text.EndsWith("いて");
            if (nextWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant) &&
                currentWord.PartOfSpeech == PartOfSpeech.Verb && !currentWord.Text.EndsWith("たり") &&
                nextWord.Text != currentWord.Text &&
                !currentWord.Text.EndsWith("くて", StringComparison.Ordinal) &&
                !isClassicalWaRowTeForm &&
                (nextWord.DictionaryForm is "しまう" or "こなす" or "いく" or "貰う" or "いる" or "ない" or "だす" ||
                 (nextWord.DictionaryForm == "得る" && HasCompoundLookup != null &&
                  HasCompoundLookup(currentWord.Text + "得る")) ||
                 (nextWord.DictionaryForm == "する" && (currentWord.Text.EndsWith("た") || currentWord.Text.EndsWith("だ"))) ||
                 (nextWord.DictionaryForm == "付く" && HasCompoundLookup != null &&
                  currentWord.DictionaryForm.Length >= 2 &&
                  HasCompoundLookup(currentWord.DictionaryForm[..^1] + "り付く"))))
            {
                if (newList == null) { newList = CopyAccumulatorUpTo(wordInfos, i - 1); }
                if (!isCopy) { currentWord = new WordInfo(currentWord); isCopy = true; }
                currentWord.Text += nextWord.Text;
                currentWord.EndOffset = nextWord.EndOffset;
                currentWord.Reading += nextWord.Reading;
            }
            else
            {
                newList?.Add(currentWord);
                currentWord = nextWord;
                isCopy = false;
            }
        }

        if (newList == null) return wordInfos;
        newList.Add(currentWord);
        return newList;
    }

    private List<WordInfo> CombineVerbDependantsSuru(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        int i = 0;
        while (i < wordInfos.Count)
        {
            WordInfo currentWord = wordInfos[i];

            if (i + 1 < wordInfos.Count)
            {
                WordInfo nextWord = wordInfos[i + 1];
                bool isModernSuru = nextWord.DictionaryForm == "する" && nextWord.Text != "する" && nextWord.Text != "しない"
                                   && !nextWord.Text.EndsWith("すぎ") && !nextWord.Text.EndsWith("過ぎ");
                bool isLiterarySuru = nextWord.DictionaryForm == "す" && nextWord.NormalizedForm == "為る";
                bool isSuruNoun = currentWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleSuru) ||
                                  currentWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleVerbSuruNoun);
                if (isSuruNoun && (isModernSuru || isLiterarySuru))
                {
                    newList ??= CopyUpTo(wordInfos, i);
                    WordInfo combinedWord = new WordInfo(currentWord);
                    combinedWord.Text += nextWord.Text;
                    combinedWord.EndOffset = nextWord.EndOffset;
                    combinedWord.Reading += nextWord.Reading;
                    newList.Add(combinedWord);
                    i += 2;
                    continue;
                }
            }

            newList?.Add(currentWord);
            i++;
        }

        return newList ?? wordInfos;
    }

    private List<WordInfo> CombineVerbDependantsTeiru(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        int i = 0;
        while (i < wordInfos.Count)
        {
            WordInfo currentWord = wordInfos[i];

            // Pattern 1: Verb + て (particle) + te-form auxiliary (3 tokens)
            if (i + 2 < wordInfos.Count)
            {
                WordInfo nextWord1 = wordInfos[i + 1];
                WordInfo nextWord2 = wordInfos[i + 2];

                if (currentWord.PartOfSpeech is PartOfSpeech.Verb &&
                    nextWord1.DictionaryForm == "て" &&
                    TeFormAuxChainVerbs.Contains(nextWord2.DictionaryForm))
                {
                    newList ??= CopyUpTo(wordInfos, i);
                    WordInfo combinedWord = new WordInfo(currentWord);
                    combinedWord.Text += nextWord1.Text + nextWord2.Text;
                    combinedWord.EndOffset = nextWord2.EndOffset;
                    combinedWord.Reading += nextWord1.Reading + nextWord2.Reading;
                    newList.Add(combinedWord);
                    i += 3;
                    continue;
                }
            }

            // Pattern 2: Word ending in て/で + subsidiary verb (2 tokens)
            if (i + 1 < wordInfos.Count)
            {
                WordInfo nextWord = wordInfos[i + 1];

                bool isClassicalWaRowTeForm = nextWord.DictionaryForm.EndsWith("う") &&
                                              nextWord.Text.EndsWith("いて");
                if ((currentWord.Text.EndsWith("て") || currentWord.Text.EndsWith("で")) &&
                    currentWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective &&
                    // くて is a genuine i-adjective te-form — subsidiary verbs attach to verb
                    // te-forms only (頭が良くて + やりたい stays split)
                    !currentWord.Text.EndsWith("くて", StringComparison.Ordinal) &&
                    !isClassicalWaRowTeForm &&
                    nextWord.PartOfSpeech != PartOfSpeech.IAdjective)
                {
                    bool isKnownSubsidiary = false;

                    if (nextWord.PartOfSpeech == PartOfSpeech.Verb &&
                        nextWord.DictionaryForm != "おる")
                    {
                        isKnownSubsidiary =
                            (nextWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant) &&
                             nextWord.DictionaryForm is "いる" or "ない") ||
                            TeFormSubsidiaryVerbs.Contains(nextWord.DictionaryForm) ||
                            TeFormSubsidiaryVerbs.Contains(nextWord.NormalizedForm);
                    }

                    if (!isKnownSubsidiary)
                    {
                        string nextHiragana = KanaNormalizer.Normalize(KanaConverter.ToHiragana(nextWord.Text));
                        var forms = PipelineCachedDeconjugate(nextHiragana);
                        foreach (var f in forms)
                        {
                            if (TeFormSubsidiaryVerbs.Contains(f.Text))
                            { isKnownSubsidiary = true; break; }
                            if (TeFormAuxChainVerbs.Contains(f.Text))
                            {
                                foreach (var t in f.Tags)
                                    if (t.StartsWith("v")) { isKnownSubsidiary = true; break; }
                                if (isKnownSubsidiary) break;
                            }
                        }
                    }

                    if (isKnownSubsidiary)
                    {
                        newList ??= CopyUpTo(wordInfos, i);
                        WordInfo combinedWord = new WordInfo(currentWord);
                        combinedWord.Text += nextWord.Text;
                        combinedWord.EndOffset = nextWord.EndOffset;
                        combinedWord.Reading += nextWord.Reading;
                        newList.Add(combinedWord);
                        i += 2;
                        continue;
                    }
                }
            }

            // Pattern 3: Verb ending in っ + dialectal とる auxiliary (2 tokens)
            if (i + 1 < wordInfos.Count)
            {
                WordInfo nextWord = wordInfos[i + 1];

                if (currentWord.PartOfSpeech == PartOfSpeech.Verb &&
                    currentWord.Text.EndsWith("っ") &&
                    nextWord.DictionaryForm == "とる")
                {
                    string nextHiragana = KanaNormalizer.Normalize(KanaConverter.ToHiragana(nextWord.Text));
                    var forms = PipelineCachedDeconjugate(nextHiragana);
                    bool isTeOruForm = false;
                    foreach (var f in forms)
                    {
                        bool hasToru = false;
                        foreach (var p in f.Process)
                            if (p.Contains("toru (teoru)")) { hasToru = true; break; }
                        if (!hasToru) continue;
                        foreach (var t in f.Tags)
                            if (t.StartsWith("v") || t.StartsWith("stem-te")) { isTeOruForm = true; break; }
                        if (isTeOruForm) break;
                    }

                    if (isTeOruForm)
                    {
                        newList ??= CopyUpTo(wordInfos, i);
                        WordInfo combinedWord = new WordInfo(currentWord);
                        combinedWord.Text += nextWord.Text;
                        combinedWord.EndOffset = nextWord.EndOffset;
                        combinedWord.Reading += nextWord.Reading;
                        newList.Add(combinedWord);
                        i += 2;
                        continue;
                    }
                }
            }

            newList?.Add(currentWord);
            i++;
        }

        return newList ?? wordInfos;
    }

    private static List<WordInfo> CopyAccumulatorUpTo(List<WordInfo> source, int upToExclusive)
    {
        var list = new List<WordInfo>(source.Count);
        for (int i = 0; i < upToExclusive; i++)
            list.Add(source[i]);
        return list;
    }
}
