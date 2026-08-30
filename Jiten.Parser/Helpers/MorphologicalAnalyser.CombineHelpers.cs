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

    private List<WordInfo> CombineVerbDependants(List<WordInfo> wordInfos) =>
        MergeAdjacentWhere(wordInfos, static (currentWord, nextWord) =>
            nextWord.HasPartOfSpeechSection(PartOfSpeechSection.Dependant) &&
            currentWord.PartOfSpeech == PartOfSpeech.Verb &&
            nextWord.DictionaryForm != "おる" &&
            nextWord.Text != currentWord.Text &&
            // A bare interjection is never a verb-dependent auxiliary: 持って + あ (mis-split 当たれ)
            // must not fuse into 持ってあ.
            nextWord.PartOfSpeech != PartOfSpeech.Interjection &&
            // くて is always an i-adjective te-form (verb te-forms are って/いて); dependant
            // auxiliaries attach to verb te-forms only (頭が良くて + やりたい stays split)
            !currentWord.Text.EndsWith("くて", StringComparison.Ordinal));

    private List<WordInfo> CombineVerbPossibleDependants(List<WordInfo> wordInfos) =>
        MergeAdjacentWhere(wordInfos, (currentWord, nextWord) =>
        {
            bool isClassicalWaRowTeForm = nextWord.DictionaryForm.EndsWith("う") &&
                                          nextWord.Text.EndsWith("いて");
            return nextWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant) &&
                   currentWord.PartOfSpeech == PartOfSpeech.Verb && !currentWord.Text.EndsWith("たり") &&
                   nextWord.Text != currentWord.Text &&
                   !currentWord.Text.EndsWith("くて", StringComparison.Ordinal) &&
                   !isClassicalWaRowTeForm &&
                   (nextWord.DictionaryForm is "しまう" or "こなす" or "いく" or "貰う" or "いる" or "ない" ||
                    // Aspectual だす only re-attaches when the compound verb is real (走り出す):
                    // an unattested pair (混じり+だした) stays split so the aspectual verb remains
                    // a visible word instead of vanishing into an unmatchable merged surface.
                    (nextWord.DictionaryForm == "だす" && HasCompoundLookup != null &&
                     (HasCompoundLookup(currentWord.Text + "だす") || HasCompoundLookup(currentWord.Text + "出す"))) ||
                    (nextWord.DictionaryForm == "得る" && HasCompoundLookup != null &&
                     HasCompoundLookup(currentWord.Text + "得る")) ||
                    (nextWord.DictionaryForm == "する" && (currentWord.Text.EndsWith("た") || currentWord.Text.EndsWith("だ"))) ||
                    (nextWord.DictionaryForm == "付く" && HasCompoundLookup != null &&
                     currentWord.DictionaryForm.Length >= 2 &&
                     HasCompoundLookup(currentWord.DictionaryForm[..^1] + "り付く")));
        });

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
                    newList ??= CopyAccumulatorUpTo(wordInfos, i);
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
                    newList ??= CopyAccumulatorUpTo(wordInfos, i);
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
                // A demonstrative is never a te-form subsidiary verb: 夢見て + これ must not fuse
                // into 夢見てこれ (これ spuriously deconjugates to a subsidiary-verb form). Gate on the
                // demonstrative dict-form, not POS Pronoun — こん (来ん) is also POS Pronoun but is a
                // genuine 来る negative that must still attach (出て+こん → 出てこん). Exception: これ
                // followed by an inflection continuation (ない/た/ます/ん/ず) is the ら抜き potential stem
                // of 来る (戻って|これ|なかった → 戻ってこれなかった), not the pronoun, and must still attach.
                bool isDemonstrative = nextWord.DictionaryForm is "これ" or "それ" or "あれ" or "どれ";
                bool koreIsPotentialStem = nextWord.DictionaryForm == "これ" && i + 2 < wordInfos.Count
                    && wordInfos[i + 2].DictionaryForm is "ない" or "無い" or "た" or "ます" or "ん" or "ぬ" or "ず" or "る";
                if ((currentWord.Text.EndsWith("て") || currentWord.Text.EndsWith("で")) &&
                    currentWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective &&
                    // くて is a genuine i-adjective te-form — subsidiary verbs attach to verb
                    // te-forms only (頭が良くて + やりたい stays split)
                    !currentWord.Text.EndsWith("くて", StringComparison.Ordinal) &&
                    !isClassicalWaRowTeForm &&
                    nextWord.PartOfSpeech != PartOfSpeech.IAdjective &&
                    (!isDemonstrative || koreIsPotentialStem))
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
                        newList ??= CopyAccumulatorUpTo(wordInfos, i);
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
                        newList ??= CopyAccumulatorUpTo(wordInfos, i);
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

    // Copy-on-write adjacent merge: folds nextWord into a growing accumulator whenever shouldMerge
    // accepts the pair; the accumulator handed to the predicate already carries earlier merges.
    // Returns the original list reference when nothing merges.
    private static List<WordInfo> MergeAdjacentWhere(List<WordInfo> wordInfos, Func<WordInfo, WordInfo, bool> shouldMerge)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        WordInfo currentWord = wordInfos[0];
        bool isCopy = false;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo nextWord = wordInfos[i];

            if (shouldMerge(currentWord, nextWord))
            {
                newList ??= CopyAccumulatorUpTo(wordInfos, i - 1);
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

    // On a match at tokens[i]: append replacement tokens via output() and return how many source
    // tokens were consumed; return 0 for no match. result is the accumulator built so far (null
    // while the scan is still all pass-through) — read-only peek at already-emitted tokens.
    private delegate int TryRewriteAt(List<WordInfo> tokens, int i, List<WordInfo>? result, Func<List<WordInfo>> output);

    // Copy-on-write scan-rewrite shell: returns the original list reference when no rewrite fires.
    private static List<WordInfo> ScanRewrite(List<WordInfo> wordInfos, TryRewriteAt tryRewrite)
    {
        List<WordInfo>? result = null;
        int i = 0;
        Func<List<WordInfo>> output = () => result ??= CopyAccumulatorUpTo(wordInfos, i);
        for (; i < wordInfos.Count; i++)
        {
            int consumed = tryRewrite(wordInfos, i, result, output);
            if (consumed > 0)
                i += consumed - 1;
            else
                result?.Add(wordInfos[i]);
        }

        return result ?? wordInfos;
    }
}
