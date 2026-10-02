using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Utils;
using WanaKanaShaapu;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    private List<WordInfo> CombineInflections(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        List<WordInfo>? result = null;
        IReadOnlyList<DeconjugationForm> CachedDeconjugate(string hiragana) => PipelineCachedDeconjugate(hiragana);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            bool isBase = (PosMapper.IsInflectableBase(word.PartOfSpeech) ||
                           word.HasPartOfSpeechSection(PartOfSpeechSection.PossibleSuru) ||
                           word.HasPartOfSpeechSection(PartOfSpeechSection.PossibleVerbSuruNoun) ||
                           (word.PartOfSpeech == PartOfSpeech.Suffix &&
                            word.HasPartOfSpeechSection(PartOfSpeechSection.VerbLike)))
                          && word.NormalizedForm != "物"
                          && !word.HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem);

            if (!isBase)
            {
                result?.Add(word);
                continue;
            }

            var currentWord = word;
            bool isCopy = false;
            int baseIndex = i;

            var currentDictForm = currentWord.DictionaryForm;
            var currentNormForm = currentWord.NormalizedForm;
            var currentPOS = currentWord.PartOfSpeech;
            var currentDictFormHiragana = KanaNormalizer.Normalize(KanaConverter.ToHiragana(currentDictForm));

            while (i + 1 < wordInfos.Count)
            {
                var nextWord = wordInfos[i + 1];

                if (ShouldStopMerging(currentWord, nextWord, wordInfos, i, currentPOS))
                    break;

                bool isValidPart = PosMapper.IsInflectionPart(nextWord.PartOfSpeech) ||
                                   nextWord.HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem) ||
                                   nextWord.HasPartOfSpeechSection(PartOfSpeechSection.ConjunctionParticle) ||
                                   nextWord.HasPartOfSpeechSection(PartOfSpeechSection.Dependant) ||
                                   nextWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant);

                // Sudachi tags やれ as an interjection; after a て-form it is auxiliary やる's imperative.
                if (!isValidPart && nextWord is { Text: "やれ", PartOfSpeech: PartOfSpeech.Interjection } &&
                    currentWord.Text.EndsWith('て'))
                    isValidPart = true;

                // Sudachi tags colloquial ねえ (ない) as the noun 姉; after a て/で-form it is the negative.
                if (!isValidPart && nextWord is { Text: "ねえ", PartOfSpeech: PartOfSpeech.Noun } &&
                    (currentWord.Text.EndsWith('て') || currentWord.Text.EndsWith('で')))
                    isValidPart = true;

                // Steals そう from そうだ/そうか when it forms a valid inflection (新しそうだ → 新しそう + だ).
                if (!isValidPart && nextWord.Text is "そうだ" or "そうか")
                {
                    string stealCandidate = currentWord.Text + "そう";
                    string stealHiragana = KanaNormalizer.Normalize(KanaConverter.ToHiragana(stealCandidate));
                    var stealForms = CachedDeconjugate(stealHiragana);

                    string stealTarget = currentPOS == PartOfSpeech.Noun
                        ? currentDictFormHiragana + "する"
                        : currentDictFormHiragana;

                    if (ContainsText(stealForms, stealTarget))
                    {
                        if (result == null) result = CopyAccumulatorUpTo(wordInfos, baseIndex);
                        if (!isCopy) { currentWord = new WordInfo(currentWord); isCopy = true; }
                        currentWord.Text = stealCandidate;
                        currentWord.Reading += WanaKana.ToKatakana("そう");
                        if (currentPOS == PartOfSpeech.Noun)
                        {
                            currentWord.DictionaryForm = currentDictForm + "する";
                            currentPOS = PartOfSpeech.Verb;
                        }

                        currentWord.PartOfSpeech = currentPOS;
                        currentDictForm = currentWord.DictionaryForm;

                        string remainder = nextWord.Text == "そうだ" ? "だ" : "か";
                        wordInfos[i + 1] = new WordInfo
                                           {
                                               Text = remainder, DictionaryForm = remainder,
                                               PartOfSpeech = remainder == "だ" ? PartOfSpeech.Auxiliary : PartOfSpeech.Particle,
                                               Reading = remainder
                                           };
                        // i is not advanced: the remainder is processed as its own token by the outer loop.
                        break;
                    }
                }

                // Sudachi tags なさそう as NaAdjective, failing isValidPart, yet it attaches to the negative stem (食べなさそう).
                if (!isValidPart && nextWord is { DictionaryForm: "なさそう" })
                {
                    string stealCandidate = currentWord.Text + nextWord.Text;
                    string stealHiragana = KanaNormalizer.Normalize(KanaConverter.ToHiragana(stealCandidate));
                    var stealForms = CachedDeconjugate(stealHiragana);

                    string stealTarget = currentPOS == PartOfSpeech.Noun
                        ? currentDictFormHiragana + "する"
                        : currentDictFormHiragana;

                    if (ContainsText(stealForms, stealTarget))
                    {
                        if (result == null) result = CopyAccumulatorUpTo(wordInfos, baseIndex);
                        if (!isCopy) { currentWord = new WordInfo(currentWord); isCopy = true; }
                        currentWord.Text = stealCandidate;
                        currentWord.EndOffset = nextWord.EndOffset;
                        currentWord.Reading += nextWord.Reading;
                        if (currentPOS == PartOfSpeech.Noun)
                        {
                            currentWord.DictionaryForm = currentDictForm + "する";
                            currentPOS = PartOfSpeech.Verb;
                        }

                        currentWord.PartOfSpeech = currentPOS;
                        currentDictForm = currentWord.DictionaryForm;
                        i++;
                        break;
                    }
                }

                // Kansai negative せん (しない) is tagged noun/prefix by Sudachi; after a suru base it inflects (卑下せん).
                if (!isValidPart && nextWord.Text == "せん" &&
                    currentWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleSuru))
                    isValidPart = true;

                if (!isValidPart) break;

                bool merged = false;
                string? newDictForm = null;

                string targetHiragana = currentPOS == PartOfSpeech.Noun
                    ? currentDictFormHiragana + "する"
                    : currentDictFormHiragana;

                var forms = PipelineCachedDeconjugateConcat(currentWord.Text, nextWord.Text);
                bool scenarioAMatch = ContainsText(forms, targetHiragana) &&
                    (HasCompoundLookup == null || HasCompoundLookup(currentDictForm) ||
                     (currentNormForm != currentDictForm && HasCompoundLookup(currentNormForm)));

                // Noun + す before べき is する+べき (帰投 + すべき), not a short causative; godan -す verbs (愛す) still merge.
                if (scenarioAMatch && currentPOS == PartOfSpeech.Noun && nextWord.Text == "す"
                    && i + 2 < wordInfos.Count
                    && (wordInfos[i + 2].Text == "べき" || wordInfos[i + 2].DictionaryForm == "べし")
                    && !(HasCompoundLookup?.Invoke(currentWord.Text + "す") == true))
                    scenarioAMatch = false;

                if (scenarioAMatch)
                {
                    if (currentPOS == PartOfSpeech.NaAdjective)
                    {
                        var matchForm = FindByText(forms, targetHiragana)!;
                        bool hasVerbStemTag = false;
                        foreach (var t in matchForm.Tags)
                            if (t.StartsWith("stem-", StringComparison.Ordinal) && t != "stem-adj-base") { hasVerbStemTag = true; break; }

                        if (hasVerbStemTag)
                        {
                            DeconjugationForm? verbForm = null;
                            foreach (var f in forms)
                            {
                                if (f.Text != targetHiragana && f.Tags.Length > 0 &&
                                    f.Tags[^1].StartsWith('v') &&
                                    HasCompoundLookup != null && HasCompoundLookup(f.Text))
                                { verbForm = f; break; }
                            }

                            if (verbForm != null)
                            {
                                merged = true;
                                newDictForm = verbForm.Text;
                                currentPOS = PartOfSpeech.Verb;
                            }
                        }
                        else
                        {
                            merged = true;
                        }
                    }
                    else
                    {
                        merged = true;
                        if (currentPOS == PartOfSpeech.Noun)
                        {
                            newDictForm = currentDictForm + "する";
                            currentPOS = PartOfSpeech.Verb;
                        }
                        else if (currentPOS == PartOfSpeech.IAdjective &&
                                 nextWord is { PartOfSpeech: PartOfSpeech.Suffix, DictionaryForm: "さ" })
                        {
                        }
                    }
                }
                else if (currentPOS == PartOfSpeech.Noun &&
                         currentWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleVerbSuruNoun))
                {
                    string bareTarget = currentDictFormHiragana;
                    if (ContainsTextWithTag(forms, bareTarget, "stem-adj-base") &&
                        (HasCompoundLookup == null || HasCompoundLookup(currentDictForm) ||
                         (currentNormForm != currentDictForm && HasCompoundLookup(currentNormForm))))
                    {
                        merged = true;
                        currentPOS = PartOfSpeech.NaAdjective;
                    }
                }
                else if (currentPOS == PartOfSpeech.Verb &&
                         !currentWord.Text.EndsWith('て') &&
                         !currentWord.Text.EndsWith('で') &&
                         !currentWord.Text.EndsWith("たく", StringComparison.Ordinal) &&
                         !currentWord.Text.EndsWith("なく", StringComparison.Ordinal) &&
                         !currentWord.Text.EndsWith("たり", StringComparison.Ordinal) &&
                         !currentWord.Text.EndsWith("だり", StringComparison.Ordinal) &&
                         !AuxiliaryVerbs.Contains(nextWord.DictionaryForm) &&
                         (nextWord.HasPartOfSpeechSection(PartOfSpeechSection.VerbLike) ||
                          (nextWord.PartOfSpeech == PartOfSpeech.Verb &&
                           nextWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant)) ||
                          nextWord.PartOfSpeech == PartOfSpeech.Suffix))
                {
                    var suffixDict = KanaNormalizer.Normalize(KanaConverter.ToHiragana(nextWord.DictionaryForm));
                    var match = FindEndingWith(forms, suffixDict);

                    if (match == null && nextWord.PartOfSpeech == PartOfSpeech.Suffix)
                    {
                        var verbDict = TryGodanDictForm(suffixDict);
                        if (verbDict != null)
                            match = FindEndingWith(forms, verbDict);
                    }

                    if (match != null && (HasCompoundLookup == null || CompoundExistsInLookup(match.Text, CachedDeconjugate)))
                    {
                        merged = true;
                        newDictForm = match.Text;
                        currentPOS = match.Tags.Length > 0 && match.Tags[^1] == "adj-i"
                            ? PartOfSpeech.IAdjective
                            : PartOfSpeech.Verb;
                    }
                }

                if (merged)
                {
                    if (result == null) result = CopyAccumulatorUpTo(wordInfos, baseIndex);
                    if (!isCopy) { currentWord = new WordInfo(currentWord); isCopy = true; }
                    currentWord.Text = currentWord.Text + nextWord.Text;
                    currentWord.EndOffset = nextWord.EndOffset;
                    currentWord.Reading += nextWord.Reading;
                    currentWord.PartOfSpeech = currentPOS;
                    currentWord.IsMergedInflection = true;
                    if (newDictForm != null)
                        currentWord.DictionaryForm = newDictForm;
                    currentDictForm = currentWord.DictionaryForm;
                    currentDictFormHiragana = KanaNormalizer.Normalize(KanaConverter.ToHiragana(currentDictForm));
                    i++;
                }
                else
                {
                    break;
                }
            }

            result?.Add(currentWord);
        }

        return result ?? wordInfos;
    }

    private static bool ContainsText(IReadOnlyList<DeconjugationForm> forms, string target)
    {
        for (int i = 0; i < forms.Count; i++)
            if (forms[i].Text == target) return true;
        return false;
    }

    private static DeconjugationForm? FindByText(IReadOnlyList<DeconjugationForm> forms, string target)
    {
        for (int i = 0; i < forms.Count; i++)
            if (forms[i].Text == target) return forms[i];
        return null;
    }

    private static bool ContainsTextWithTag(IReadOnlyList<DeconjugationForm> forms, string target, string tag)
    {
        for (int i = 0; i < forms.Count; i++)
            if (forms[i].Text == target && forms[i].Tags.Contains(tag)) return true;
        return false;
    }

    private static DeconjugationForm? FindEndingWith(IReadOnlyList<DeconjugationForm> forms, string suffix)
    {
        for (int i = 0; i < forms.Count; i++)
            if (forms[i].Text.EndsWith(suffix, StringComparison.Ordinal) && forms[i].Text.Length > suffix.Length) return forms[i];
        return null;
    }

    private static bool ShouldStopMerging(WordInfo currentWord, WordInfo nextWord,
        List<WordInfo> wordInfos, int i, PartOfSpeech currentPOS)
    {
        // Negative stem な may merge before すぎる (わからなすぎる).
        bool isNegativeStemBeforeDependant = false;
        if (nextWord is { Text: "な", PartOfSpeech: PartOfSpeech.Auxiliary, DictionaryForm: "ない" } &&
            i + 2 < wordInfos.Count)
        {
            var afterNa = wordInfos[i + 2];
            isNegativeStemBeforeDependant =
                (afterNa.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant) ||
                 afterNa.HasPartOfSpeechSection(PartOfSpeechSection.Dependant)) &&
                afterNa.DictionaryForm is "すぎる" or "過ぎる";
        }

        if (nextWord.Text is "は" or "よ" or "し" or "を" or "が" or "か" or "ください" or "かな")
            return true;
        if (nextWord.Text == "な" && !isNegativeStemBeforeDependant)
            return true;

        // Standalone よう is always 様; volitional よう arrives inside the verb token.
        if (nextWord is { Text: "よう", DictionaryForm: "よう" })
            return true;

        // いけ after ちゃ/じゃ/きゃ/にゃ is obligation/prohibition, not a compound.
        if (nextWord.DictionaryForm == "いける" &&
            (currentWord.Text.EndsWith("ちゃ", StringComparison.Ordinal) || currentWord.Text.EndsWith("じゃ", StringComparison.Ordinal) ||
             currentWord.Text.EndsWith("きゃ", StringComparison.Ordinal) || currentWord.Text.EndsWith("にゃ", StringComparison.Ordinal)))
            return true;

        if (nextWord is { Text: "ん", DictionaryForm: "の" or "ん" })
            return true;

        // An imperative has no te-form, so a following って is quotative (来い|って).
        if (nextWord is { Text: "って", DictionaryForm: "って" } && currentWord.IsImperative)
            return true;

        // って before ん/んだ/んです is quotative, not te-form.
        if (nextWord.Text == "って" && i + 2 < wordInfos.Count &&
            wordInfos[i + 2].Text is "ん" or "んだ" or "んです")
            return true;

        // Quotative って stays split before a kanji or kana quote verb (かな+って+思ったら); つか+って+ください re-merges.
        if (nextWord is { Text: "って", DictionaryForm: "って" } && i + 2 < wordInfos.Count &&
            wordInfos[i + 2].DictionaryForm is "思う" or "おもう" or "言う" or "いう"
                or "聞く" or "きく" or "考える" or "感じる")
            return true;

        // って before a noun, punctuation or end (なくなった+って+話) is quotative; re-merging leaves an unresolvable blob.
        if (nextWord is { Text: "って", DictionaryForm: "って" } &&
            (i + 2 >= wordInfos.Count ||
             wordInfos[i + 2].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                 or PartOfSpeech.SupplementarySymbol))
            return true;

        // って before a sentence-ending particle (待つ+って+さ) is quotative; re-merging leaves an unresolvable blob.
        if (nextWord is { Text: "って", DictionaryForm: "って" } && i + 2 < wordInfos.Count
            && wordInfos[i + 2] is { PartOfSpeech: PartOfSpeech.Particle }
            && wordInfos[i + 2].Text is "さ" or "よ" or "ね" or "わ" or "ぞ" or "ぜ")
            return true;

        // Benefactives stay separate (させて|いただきます); the て+貰う deconj rules serve Dependant-path merges only.
        if ((currentWord.Text.EndsWith('て') || currentWord.Text.EndsWith('で'))
            && nextWord.DictionaryForm is "いただく" or "頂く" or "貰う")
            return true;

        // A verb after an adjective くて starts a new clause (頭が良くて + やりたい).
        if (currentPOS == PartOfSpeech.IAdjective && currentWord.Text.EndsWith('て')
            && nextWord.PartOfSpeech == PartOfSpeech.Verb)
            return true;

        if (currentWord.Text.EndsWith('ん') && nextWord.Text is "だ" or "です")
            return true;
        if (nextWord is { Text: "じゃ", DictionaryForm: "だ" })
            return true;
        if (currentPOS == PartOfSpeech.NaAdjective &&
            nextWord is { Text: "で", DictionaryForm: "だ" })
            return true;

        return false;
    }

    private static readonly HashSet<string> PrefixCombineExclusions = ["おつもり", "おいま", "おにく"];

    private static bool IsKanjiPrefix(string text) =>
        text.Length > 0 && JapaneseTextHelper.IsKanji(text[0]);

    // Prefix combine runs before noun compounding: お|母|上 must yield お + 母上, not お母; お+手+紙 → お手紙 is a word.
    private bool CompletesCompoundWithFollowing(List<WordInfo> wordInfos, int prefixIndex)
    {
        if (HasNonNameCompoundLookup == null || prefixIndex + 2 >= wordInfos.Count)
            return false;

        var head = wordInfos[prefixIndex + 1];
        var following = wordInfos[prefixIndex + 2];
        if (!PosMapper.IsNounForCompounding(following.PartOfSpeech) || following.Text.Length == 0)
            return false;

        return HasNonNameCompoundLookup(head.Text + following.Text)
               && !HasCompoundLookup!(wordInfos[prefixIndex].Text + head.Text + following.Text);
    }

    private List<WordInfo> CombinePrefixes(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2 || HasCompoundLookup == null)
            return wordInfos;

        List<WordInfo>? newList = null;
        int i = 0;

        while (i < wordInfos.Count)
        {
            var currentWord = wordInfos[i];

            // Sudachi tags emphatic ど as Adverb (truncated どう); before an i-adjective it is a prefix (ど偉い).
            bool isEmphaticDo = currentWord.Text == "ど" && currentWord.PartOfSpeech == PartOfSpeech.Adverb
                && i + 1 < wordInfos.Count && wordInfos[i + 1].PartOfSpeech == PartOfSpeech.IAdjective;

            if ((currentWord.PartOfSpeech == PartOfSpeech.Prefix || isEmphaticDo) && i + 1 < wordInfos.Count)
            {
                var nextWord = wordInfos[i + 1];
                bool isKanjiPrefix = IsKanjiPrefix(currentWord.Text);

                // Kanji prefixes (相, 再, 不) may take verbs/adjectives; kana prefixes (お, ご) only nominals.
                bool isContentWord = nextWord.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.NaAdjective
                    or PartOfSpeech.Adverb or PartOfSpeech.NominalAdjective or PartOfSpeech.CommonNoun
                    || ((isKanjiPrefix || isEmphaticDo) && nextWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective);

                if (isContentWord)
                {
                    var combinedText = currentWord.Text + nextWord.Text;

                    if (!PrefixCombineExclusions.Contains(combinedText) &&
                        HasCompoundLookup(combinedText) &&
                        !CompletesCompoundWithFollowing(wordInfos, i))
                    {
                        var prefixStart = currentWord.StartOffset;
                        currentWord = new WordInfo(nextWord);
                        currentWord.Text = combinedText;
                        currentWord.DictionaryForm = combinedText;
                        currentWord.NormalizedForm = combinedText;
                        currentWord.StartOffset = prefixStart;
                        if (nextWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective)
                            currentWord.PartOfSpeech = PartOfSpeech.Noun;
                        newList ??= CopyAccumulatorUpTo(wordInfos, i);
                        newList.Add(currentWord);
                        i += 2;
                        continue;
                    }

                    // Inflected verb: 相 + 次い → 相次ぐ; POS kept so CombineInflections and deconjugation still apply.
                    if (nextWord.PartOfSpeech == PartOfSpeech.Verb
                        && !string.IsNullOrEmpty(nextWord.DictionaryForm)
                        && nextWord.DictionaryForm != nextWord.Text)
                    {
                        var dictionaryCombined = currentWord.Text + nextWord.DictionaryForm;
                        if (!PrefixCombineExclusions.Contains(dictionaryCombined)
                            && HasVerbOrAdjectiveLookup?.Invoke(dictionaryCombined) == true)
                        {
                            var prefixStart = currentWord.StartOffset;
                            currentWord = new WordInfo(nextWord);
                            currentWord.Text = combinedText;
                            currentWord.DictionaryForm = dictionaryCombined;
                            currentWord.NormalizedForm = dictionaryCombined;
                            currentWord.StartOffset = prefixStart;
                            newList ??= CopyAccumulatorUpTo(wordInfos, i);
                            newList.Add(currentWord);
                            i += 2;
                            continue;
                        }
                    }

                    // Classical i-adjective: 故 + 無き → 故無い (2112310); IAdjective kept so deconjugation reaches adj-i.
                    if (nextWord.PartOfSpeech == PartOfSpeech.IAdjective
                        && !string.IsNullOrEmpty(nextWord.NormalizedForm)
                        && nextWord.NormalizedForm != nextWord.Text)
                    {
                        var normalizedCombined = currentWord.Text + nextWord.NormalizedForm;
                        if (!PrefixCombineExclusions.Contains(normalizedCombined)
                            && HasCompoundLookup(normalizedCombined))
                        {
                            var prefixStart = currentWord.StartOffset;
                            currentWord = new WordInfo(nextWord);
                            currentWord.Text = combinedText;
                            currentWord.DictionaryForm = normalizedCombined;
                            currentWord.NormalizedForm = normalizedCombined;
                            currentWord.StartOffset = prefixStart;
                            newList ??= CopyAccumulatorUpTo(wordInfos, i);
                            newList.Add(currentWord);
                            i += 2;
                            continue;
                        }
                    }

                    // Colloquial surfaces carry the standard form in Reading (古 + くせー reading クサイ → 古くさい).
                    if (!string.IsNullOrEmpty(nextWord.Reading))
                    {
                        var readingHira = KanaConverter.ToHiragana(nextWord.Reading);
                        if (readingHira != nextWord.Text && readingHira != combinedText
                            && !HasCompoundLookup(nextWord.Text))
                        {
                            var readingCombined = currentWord.Text + readingHira;
                            if (!PrefixCombineExclusions.Contains(readingCombined) && HasCompoundLookup(readingCombined))
                            {
                                var prefixStart = currentWord.StartOffset;
                                currentWord = new WordInfo(nextWord);
                                currentWord.Text = combinedText;
                                currentWord.DictionaryForm = readingCombined;
                                currentWord.NormalizedForm = readingCombined;
                                currentWord.StartOffset = prefixStart;
                                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                                newList.Add(currentWord);
                                i += 2;
                                continue;
                            }
                        }
                    }

                    // Re-cuts an unattested next token (相+当腹 → 相当+腹); remainder is a word or one stray kana (おに + ぃ).
                    if (nextWord.Text.Length >= 2 &&
                        !PrefixCombineExclusions.Contains(combinedText) &&
                        !HasCompoundLookup(nextWord.Text))
                    {
                        bool partialMatch = false;
                        for (int len = nextWord.Text.Length - 1; len >= 1; len--)
                        {
                            var partialText = currentWord.Text + nextWord.Text[..len];
                            if (!PrefixCombineExclusions.Contains(partialText) &&
                                HasCompoundLookup(partialText) &&
                                (HasCompoundLookup(nextWord.Text[len..])
                                 || (nextWord.Text.Length - len == 1 && JapaneseTextHelper.IsKana(nextWord.Text[len]))))
                            {
                                var combinedWord = new WordInfo(nextWord);
                                combinedWord.Text = partialText;
                                combinedWord.StartOffset = currentWord.StartOffset;
                                combinedWord.EndOffset = nextWord.StartOffset >= 0 ? nextWord.StartOffset + len : -1;
                                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                                newList.Add(combinedWord);

                                var remainder = new WordInfo(nextWord);
                                remainder.Text = nextWord.Text[len..];
                                remainder.StartOffset = nextWord.StartOffset >= 0 ? nextWord.StartOffset + len : -1;
                                newList.Add(remainder);

                                i += 2;
                                partialMatch = true;
                                break;
                            }
                        }

                        if (partialMatch)
                            continue;
                    }
                }
            }

            newList?.Add(currentWord);
            i++;
        }

        return newList ?? wordInfos;
    }

    private List<WordInfo> CombineAmounts(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        WordInfo currentWord = wordInfos[0];

        for (int i = 1; i < wordInfos.Count; i++)
        {
            var nextWord = wordInfos[i];

            if ((currentWord.HasPartOfSpeechSection(PartOfSpeechSection.Amount) ||
                 currentWord.HasPartOfSpeechSection(PartOfSpeechSection.Numeral)) &&
                AmountCombinations.Combinations.Contains((currentWord.Text, nextWord.Text)))
            {
                if (newList == null) { newList = CopyAccumulatorUpTo(wordInfos, i - 1); }
                var text = currentWord.Text + nextWord.Text;
                var startOff = currentWord.StartOffset;
                currentWord = new WordInfo(nextWord);
                currentWord.Text = text;
                currentWord.DictionaryForm = text;
                currentWord.StartOffset = startOff;
                currentWord.PartOfSpeech = PartOfSpeech.Noun;
            }
            else
            {
                newList?.Add(currentWord);
                currentWord = nextWord;
            }
        }

        if (newList == null) return wordInfos;
        newList.Add(currentWord);
        return newList;
    }

    private List<WordInfo> CombineTte(List<WordInfo> wordInfos) =>
        MergeAdjacentWhere(wordInfos, static (currentWord, nextWord) =>
            currentWord.Text.EndsWith('っ') && nextWord.Text.StartsWith('て'));

    // って + kana いう → っていう (2757880); kanji 言う is "to say" (だって|言う|人) and stays split.
    private List<WordInfo> CombineQuotativeToIu(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (i + 1 < wordInfos.Count
                && word is { Text: "って", DictionaryForm: "って", PartOfSpeech: PartOfSpeech.Particle }
                && wordInfos[i + 1] is { Text: "いう", DictionaryForm: "いう" })
            {
                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                var iu = wordInfos[i + 1];
                newList.Add(new WordInfo(word)
                {
                    Text = "っていう",
                    DictionaryForm = "っていう",
                    NormalizedForm = "っていう",
                    Reading = "ッテイウ",
                    PartOfSpeech = PartOfSpeech.Conjunction,
                    EndOffset = iu.EndOffset
                });
                i++;
                continue;
            }

            // Splits って off a volitional (しようって) for っていう; te-form stems (黙って) never end in う-row; bare うって skipped.
            if (i + 1 < wordInfos.Count
                && word.Text.Length > 3
                && word.Text.EndsWith("って", StringComparison.Ordinal)
                && wordInfos[i + 1] is { Text: "いう", DictionaryForm: "いう" }
                && IsQuotativeTteStem(word.Text[..^2]))
            {
                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                var iu = wordInfos[i + 1];
                var stem = word.Text[..^2];
                int mid = word.EndOffset >= 0 ? word.EndOffset - 2 : -1;
                newList.Add(new WordInfo(word)
                {
                    Text = stem, DictionaryForm = stem, NormalizedForm = stem, EndOffset = mid
                });
                newList.Add(new WordInfo(iu)
                {
                    Text = "っていう",
                    DictionaryForm = "っていう",
                    NormalizedForm = "っていう",
                    Reading = "ッテイウ",
                    PartOfSpeech = PartOfSpeech.Conjunction,
                    StartOffset = mid,
                    EndOffset = iu.EndOffset
                });
                i++;
                continue;
            }

            newList?.Add(word);
        }

        return newList ?? wordInfos;
    }

    // Quotative って follows a う-row kana; a te-form stem never ends in one.
    private static bool IsQuotativeTteStem(string s) =>
        s.Length > 0 && s[^1] is 'う' or 'く' or 'ぐ' or 'す' or 'つ' or 'ぬ' or 'ぶ' or 'む' or 'る';

    private List<WordInfo> CombineVerbDependant(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        wordInfos = CombineVerbDependants(wordInfos);
        wordInfos = CombineVerbPossibleDependants(wordInfos);
        wordInfos = CombineVerbDependantsSuru(wordInfos);
        wordInfos = CombineVerbDependantsTeiru(wordInfos);

        return wordInfos;
    }

    private List<WordInfo> CombineAdverbialParticle(List<WordInfo> wordInfos) =>
        MergeAdjacentWhere(wordInfos, static (currentWord, nextWord) =>
            nextWord.HasPartOfSpeechSection(PartOfSpeechSection.AdverbialParticle) &&
            (nextWord.DictionaryForm == "だり" || nextWord.DictionaryForm == "たり") &&
            currentWord.PartOfSpeech == PartOfSpeech.Verb);

    private List<WordInfo> CombineConjunctiveParticle(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo currentWord = wordInfos[i];
            WordInfo previousWord = newList != null ? newList[^1] : wordInfos[i - 1];
            bool combined = false;

            if (currentWord.HasPartOfSpeechSection(PartOfSpeechSection.ConjunctionParticle) &&
                currentWord.Text is "て" or "で" or "ちゃ" or "ば" &&
                previousWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary)
            {
                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                previousWord.Text += currentWord.Text;
                previousWord.EndOffset = currentWord.EndOffset;
                previousWord.Reading += currentWord.Reading;
                combined = true;
            }

            if (!combined)
            {
                newList?.Add(currentWord);
            }
        }

        return newList ?? wordInfos;
    }

    // Keep in sync with ShouldStopMerging's list; kana lemmas included since Sudachi tags 言う as いう freely.
    private static bool IsQuoteTakingVerb(WordInfo w) =>
        w.DictionaryForm is "思う" or "おもう" or "言う" or "いう" or "聞く" or "きく" or "考える" or "感じる";

    private List<WordInfo> CombineAuxiliary(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        var deconjugator = Deconjugator.Instance;
        IReadOnlyList<DeconjugationForm> Deconj(string h) => deconjugator.Deconjugate(h);

        List<WordInfo>? newList = null;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo currentWord = wordInfos[i];
            WordInfo previousWord = newList != null ? newList[^1] : wordInfos[i - 1];
            bool combined = false;

            if (currentWord.PartOfSpeech != PartOfSpeech.Auxiliary)
            {
                // Copula で keeps DictionaryForm だ after reclassification to Particle; で + ある → である.
                if (previousWord is { Text: "で", DictionaryForm: "だ" } &&
                    currentWord.DictionaryForm is "ある" or "有る")
                {
                    previousWord.Text = "で" + currentWord.Text;
                    previousWord.EndOffset = currentWord.EndOffset;
                    previousWord.Reading += currentWord.Reading;
                    previousWord.PartOfSpeech = currentWord.PartOfSpeech;
                    previousWord.DictionaryForm = "である";
                    newList ??= CopyAccumulatorUpTo(wordInfos, i);
                }
                else
                {
                    newList?.Add(currentWord);
                }

                continue;
            }

            if ((previousWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.NaAdjective
                     or PartOfSpeech.Auxiliary
                 || previousWord.HasPartOfSpeechSection(PartOfSpeechSection.Adjectival))
                && (HasCompoundLookup == null ||
                    previousWord.PartOfSpeech != PartOfSpeech.Verb ||
                    previousWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleSuru) ||
                    VerbDictFormExistsInLookup(previousWord.DictionaryForm, previousWord.NormalizedForm, Deconj))
                // A pinned auxiliary (した+んだ) is a repair stage's decision; absorbing it erases the word.
                && currentWord.PreMatchedWordId == null
                && currentWord.Text != "な"
                && currentWord.Text != "に"
                && (currentWord.DictionaryForm != "です" ||
                    previousWord.PartOfSpeech is PartOfSpeech.Verb && currentWord is { DictionaryForm: "です", Text: "でし" or "でした" })
                && currentWord.DictionaryForm != "らしい"
                && currentWord.Text != "なら"
                && currentWord.Text != "なる"
                && currentWord.DictionaryForm != "べし"
                && currentWord.DictionaryForm != "む"
                && currentWord.DictionaryForm is not "ごとし" and not "如し"
                && currentWord.DictionaryForm != "ようだ"
                && currentWord.DictionaryForm != "やがる"
                && currentWord.DictionaryForm != "たり"
                && currentWord.DictionaryForm != "筈"
                && currentWord.Text != "だろう"
                && currentWord.Text != "で"
                && currentWord.Text != "や"
                && currentWord.Text != "やろ"
                && currentWord.Text != "やしない"
                && currentWord.Text != "し"
                && !(currentWord.Text == "って" && previousWord.IsImperative)
                // Copula だって before a quote verb is quotative (大袈裟|だって|言いたい); ようって folds for CombineQuotativeToIu.
                && !(currentWord.DictionaryForm == "だ"
                     && currentWord.Text.EndsWith("って", StringComparison.Ordinal)
                     && i + 1 < wordInfos.Count && IsQuoteTakingVerb(wordInfos[i + 1]))
                && currentWord.Text != "なのだ"
                && !currentWord.Text.StartsWith("なん", StringComparison.Ordinal)
                && currentWord.Text != "だろ"
                && currentWord.Text != "ハズ"
                && (currentWord.Text != "だ" || currentWord.Text == "だ" && previousWord.Text[^1] == 'ん' && IsValidNdaPastTense(previousWord.Text))
                && !(currentWord is { Text: "じゃ", DictionaryForm: "だ" })
               )
            {
                var stemText = previousWord.Text;
                previousWord.Text += currentWord.Text;
                previousWord.EndOffset = currentWord.EndOffset;
                previousWord.Reading += currentWord.Reading;
                if (currentWord.DictionaryForm is "ちまう" or "じまう" or "しまう"
                    && HasCompoundLookup != null)
                {
                    var mergedDictForm = stemText + currentWord.DictionaryForm;
                    if (HasCompoundLookup(mergedDictForm))
                        previousWord.DictionaryForm = mergedDictForm;
                }
                combined = true;
            }

            if (!combined && previousWord.PartOfSpeech == PartOfSpeech.Expression
                          && currentWord.DictionaryForm == "た"
                          && (previousWord.Text[^1] is 'て' or 'で'))
            {
                previousWord.Text += currentWord.Text;
                previousWord.EndOffset = currentWord.EndOffset;
                previousWord.Reading += currentWord.Reading;
                combined = true;
            }

            if (combined) newList ??= CopyAccumulatorUpTo(wordInfos, i);

            if (!combined)
            {
                newList?.Add(currentWord);
            }
        }

        return newList ?? wordInfos;
    }

    // Merged only when JMDict attests the compound (逃げ|切った → 逃げ切る), so 紙を切った is untouched.
    private static readonly HashSet<string> CompletionAuxVerbs = ["切る"];

    private List<WordInfo> CombineCompletionAuxVerb(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2 || HasCompoundLookup == null)
            return wordInfos;

        List<WordInfo>? result = null;
        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];
            if (i + 1 < wordInfos.Count
                && word.PartOfSpeech == PartOfSpeech.Verb
                && word.Text.Length >= 2
                && !word.Text.EndsWith('て') && !word.Text.EndsWith('で')
                && wordInfos[i + 1] is { PartOfSpeech: PartOfSpeech.Verb } aux
                && CompletionAuxVerbs.Contains(aux.DictionaryForm)
                && HasCompoundLookup(word.Text + aux.DictionaryForm))
            {
                result ??= CopyAccumulatorUpTo(wordInfos, i);
                result.Add(new WordInfo(word)
                {
                    Text = word.Text + aux.Text,
                    DictionaryForm = word.Text + aux.DictionaryForm,
                    NormalizedForm = word.Text + aux.DictionaryForm,
                    Reading = word.Reading + aux.Reading,
                    PartOfSpeech = PartOfSpeech.Verb,
                    IsMergedInflection = true,
                    EndOffset = aux.EndOffset
                });
                i++;
                continue;
            }

            result?.Add(word);
        }

        return result ?? wordInfos;
    }

    private List<WordInfo> CombineAuxiliaryVerbStem(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        WordInfo currentWord = wordInfos[0];
        bool isCopy = false;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            var nextWord = wordInfos[i];

            // Adjectival suffixes (やすい, にくい) arrive as their stems (やす, にく) and also host そう.
            var isAdjectivalSuffix = wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Suffix &&
                                     wordInfos[i - 1].DictionaryForm.EndsWith('い');
            if (wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem) &&
                wordInfos[i].Text != "ように" &&
                wordInfos[i].Text != "よう" &&
                wordInfos[i].Text != "ようです" &&
                wordInfos[i].Text != "みたい" &&
                (wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb ||
                 wordInfos[i - 1].PartOfSpeech == PartOfSpeech.IAdjective ||
                 (wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Noun &&
                  (wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.PossibleNaAdjective) ||
                   wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.PossibleVerbSuruNoun))) ||
                 isAdjectivalSuffix))
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

    private List<WordInfo> CombineSuffix(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        WordInfo currentWord = wordInfos[0];
        bool isCopy = false;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            var nextWord = wordInfos[i];

            if ((wordInfos[i].PartOfSpeech == PartOfSpeech.Suffix || wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.Suffix))
                && (wordInfos[i].DictionaryForm == "っこ"
                    || wordInfos[i].DictionaryForm == "さ"
                    // 何|がって is が + って, not 何がる.
                    || (wordInfos[i].DictionaryForm == "がる" && currentWord.PartOfSpeech != PartOfSpeech.Pronoun)
                    || (wordInfos[i].DictionaryForm is "ぶり" or "振り" &&
                        currentWord.PartOfSpeech == PartOfSpeech.IAdjective &&
                        !currentWord.Text.EndsWith('い') && currentWord.DictionaryForm.EndsWith('い'))
                    || (wordInfos[i].DictionaryForm == "ら" &&
                        wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Pronoun && wordInfos[i - 1].Text != "貴様")))
            {
                newList ??= CopyAccumulatorUpTo(wordInfos, i - 1);
                if (!isCopy) { currentWord = new WordInfo(currentWord); isCopy = true; }
                currentWord.Text += nextWord.Text;
                currentWord.EndOffset = nextWord.EndOffset;
                currentWord.Reading += nextWord.Reading;
            }
            else if (wordInfos[i].DictionaryForm == "ぶる"
                     && (wordInfos[i].PartOfSpeech == PartOfSpeech.Suffix || wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.Suffix))
                     && HasCompoundLookup != null
                     && HasCompoundLookup(currentWord.DictionaryForm + "ぶる"))
            {
                newList ??= CopyAccumulatorUpTo(wordInfos, i - 1);
                if (!isCopy) { currentWord = new WordInfo(currentWord); isCopy = true; }
                currentWord.DictionaryForm += "ぶる";
                currentWord.PartOfSpeech = PartOfSpeech.Verb;
                currentWord.Text += nextWord.Text;
                currentWord.EndOffset = nextWord.EndOffset;
                currentWord.Reading += nextWord.Reading;
            }
            // Sudachi misparses がったり after an adjective stem as an adverb (怖|がったり).
            else if (nextWord is { PartOfSpeech: PartOfSpeech.Adverb, Text: "がったり" }
                     && currentWord.PartOfSpeech == PartOfSpeech.IAdjective
                     && !currentWord.Text.EndsWith('い')
                     && currentWord.DictionaryForm.EndsWith('い'))
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

    // The base must be a spelling of the surface, not merely a lemma the deconjugator reaches.
    private static bool KanjiPreserved(string surface, string baseForm)
    {
        foreach (var c in surface)
            if (JapaneseTextHelper.IsKanji(c) && baseForm.IndexOf(c) < 0)
                return false;

        return true;
    }

    private List<WordInfo> ReclassifyOrphanedSuffixes(List<WordInfo> wordInfos)
    {
        for (int i = 1; i < wordInfos.Count; i++)
        {
            if (wordInfos[i].PartOfSpeech != PartOfSpeech.Suffix)
                continue;

            // じまい follows ず-forms (わからずじまい) and honorifics follow names; both are genuine suffixes.
            if (wordInfos[i].DictionaryForm is "じまい" or "仕舞い" or "ちゃん" or "さん" or "くん" or "様" or "殿" or "氏")
                continue;

            // An unattested kanji "suffix" spelling a verb (レッテル|貼り → 貼る) is a verb; kana ones stay (ぶっ+た ≠ 打つ).
            if (HasNonNameCompoundLookup != null && !HasNonNameCompoundLookup(wordInfos[i].Text)
                && wordInfos[i].Text.Any(JapaneseTextHelper.IsKanji))
            {
                foreach (var form in Deconjugator.Instance.Deconjugate(wordInfos[i].Text))
                {
                    if (form.Text == wordInfos[i].Text || form.Text.Length < 2) continue;
                    if (!DictionaryVerbEndings.Contains(form.Text[^1])) continue;
                    if (!KanjiPreserved(wordInfos[i].Text, form.Text)) continue;
                    if (!HasNonNameCompoundLookup(form.Text)) continue;

                    wordInfos[i].PartOfSpeech = PartOfSpeech.Verb;
                    wordInfos[i].PartOfSpeechSection1 = PartOfSpeechSection.None;
                    wordInfos[i].DictionaryForm = form.Text;
                    wordInfos[i].NormalizedForm = form.Text;
                    wordInfos[i].Reading = string.Empty;
                    // A reclassified suffix must not anchor compound/expression windows.
                    wordInfos[i].WasReclassifiedFromSuffix = true;
                    break;
                }

                if (wordInfos[i].PartOfSpeech == PartOfSpeech.Verb)
                    continue;
            }

            // A predicate-tail suffix (チッチャ|い) can't host a suffix; 車 after it must reclassify to reach its noun.
            var prevInfo = wordInfos[i - 1];
            bool prevIsPredicateTailSuffix = prevInfo.PartOfSpeech == PartOfSpeech.Suffix
                                             && prevInfo.Text is "い" or "く" or "かっ";
            var prev = prevInfo.PartOfSpeech;
            if (prev is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Numeral or PartOfSpeech.Prefix or PartOfSpeech.Pronoun
                || (prev == PartOfSpeech.Suffix && !prevIsPredicateTailSuffix))
                continue;

            // 形容詞的 (っぽい) need their POS for the adj branch; 形状詞的 (気) may start expressions (気を引き締める).
            if (wordInfos[i].PartOfSpeechSection1 is PartOfSpeechSection.Adjectival or PartOfSpeechSection.NaAdjectiveLike)
                continue;

            wordInfos[i].PartOfSpeech = PartOfSpeech.CommonNoun;
            wordInfos[i].PartOfSpeechSection1 = PartOfSpeechSection.None;
            wordInfos[i].Reading = string.Empty;
            wordInfos[i].WasReclassifiedFromSuffix = true;
        }

        return wordInfos;
    }

    private List<WordInfo> CombineParticles(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        int i = 0;
        while (i < wordInfos.Count)
        {
            WordInfo currentWord = wordInfos[i];

            // か+も+しれ* is one expression; the deconjugator recovers かもしれない from contracted しんない.
            if (i + 2 < wordInfos.Count &&
                currentWord.Text == "か" &&
                wordInfos[i + 1].Text == "も" &&
                (wordInfos[i + 2].Text.StartsWith("しれ", StringComparison.Ordinal) ||
                 wordInfos[i + 2].Text.StartsWith("しんな", StringComparison.Ordinal) || wordInfos[i + 2].Text.StartsWith("しんね", StringComparison.Ordinal)))
            {
                WordInfo combinedWord = new WordInfo(currentWord);
                combinedWord.Text = currentWord.Text + wordInfos[i + 1].Text + wordInfos[i + 2].Text;
                combinedWord.EndOffset = wordInfos[i + 2].EndOffset;
                combinedWord.Reading = currentWord.Reading + wordInfos[i + 1].Reading + wordInfos[i + 2].Reading;
                combinedWord.PartOfSpeech = PartOfSpeech.Expression;
                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                newList.Add(combinedWord);
                i += 3;
                continue;
            }

            // かも already fused upstream (after the ん-repair) arrives as [かも][しれ*].
            if (i + 1 < wordInfos.Count &&
                currentWord.Text == "かも" &&
                (wordInfos[i + 1].Text.StartsWith("しれ", StringComparison.Ordinal) ||
                 wordInfos[i + 1].Text.StartsWith("しんな", StringComparison.Ordinal) ||
                 wordInfos[i + 1].Text.StartsWith("しんね", StringComparison.Ordinal)))
            {
                WordInfo combinedWord = new WordInfo(currentWord);
                combinedWord.Text = currentWord.Text + wordInfos[i + 1].Text;
                combinedWord.EndOffset = wordInfos[i + 1].EndOffset;
                combinedWord.Reading = currentWord.Reading + wordInfos[i + 1].Reading;
                combinedWord.PartOfSpeech = PartOfSpeech.Expression;
                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                newList.Add(combinedWord);
                i += 2;
                continue;
            }

            // A fused じゃない expression + か → じゃないか; a verb negative (しない か) keeps か separate.
            if (i + 1 < wordInfos.Count &&
                currentWord.PartOfSpeech == PartOfSpeech.Expression &&
                currentWord.DictionaryForm is "じゃない" or "ではない" &&
                wordInfos[i + 1].Text == "か")
            {
                WordInfo combinedWord = new WordInfo(currentWord);
                combinedWord.Text += "か";
                combinedWord.EndOffset = wordInfos[i + 1].EndOffset;
                combinedWord.Reading += wordInfos[i + 1].Reading;
                combinedWord.DictionaryForm += "か";
                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                newList.Add(combinedWord);
                i += 2;
                continue;
            }

            if (i + 1 < wordInfos.Count)
            {
                WordInfo nextWord = wordInfos[i + 1];
                string combinedText = "";

                if (currentWord.Text == "に" && nextWord.Text == "は") combinedText = "には";
                else if (currentWord.Text == "と" && nextWord.Text == "は") combinedText = "とは";
                else if (currentWord.Text == "で" && nextWord.Text == "は") combinedText = "では";
                else if (currentWord.Text == "の" && nextWord.Text == "に") combinedText = "のに";

                if (!string.IsNullOrEmpty(combinedText))
                {
                    WordInfo combinedWord = new WordInfo(currentWord);
                    combinedWord.Text = combinedText;
                    combinedWord.EndOffset = nextWord.EndOffset;
                    combinedWord.Reading = currentWord.Reading + nextWord.Reading;

                    if (combinedText == "では" && i + 2 < wordInfos.Count)
                    {
                        var lookAhead = wordInfos[i + 2];
                        if (lookAhead.DictionaryForm is "ない" or "無い")
                        {
                            combinedWord.Text += lookAhead.Text;
                            combinedWord.EndOffset = lookAhead.EndOffset;
                            combinedWord.Reading += lookAhead.Reading;
                            combinedWord.DictionaryForm = "ではない";
                            combinedWord.PartOfSpeech = PartOfSpeech.Expression;
                            int consumed = 3;

                            if (i + 3 < wordInfos.Count && wordInfos[i + 3].Text == "か")
                            {
                                combinedWord.Text += "か";
                                combinedWord.EndOffset = wordInfos[i + 3].EndOffset;
                                combinedWord.Reading += wordInfos[i + 3].Reading;
                                combinedWord.DictionaryForm = "ではないか";
                                consumed = 4;
                            }

                            newList ??= CopyAccumulatorUpTo(wordInfos, i);

                            newList.Add(combinedWord);
                            i += consumed;
                            continue;
                        }
                    }

                    newList ??= CopyAccumulatorUpTo(wordInfos, i);

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

    private static readonly Dictionary<string, List<(string Second, PartOfSpeech? Pos)>> SpecialCases2Dict = BuildSpecialCases2Dict();
    private static readonly Dictionary<string, List<(string Second, string Third, PartOfSpeech? Pos)>> SpecialCases3Dict = BuildSpecialCases3Dict();

    private static Dictionary<string, List<(string Second, PartOfSpeech? Pos)>> BuildSpecialCases2Dict()
    {
        var dict = new Dictionary<string, List<(string, PartOfSpeech?)>>(StringComparer.Ordinal);
        foreach (var sc in SpecialCases2)
        {
            if (!dict.TryGetValue(sc.Item1, out var list))
            {
                list = [];
                dict[sc.Item1] = list;
            }
            list.Add((sc.Item2, sc.Item3));
        }
        return dict;
    }

    private static Dictionary<string, List<(string Second, string Third, PartOfSpeech? Pos)>> BuildSpecialCases3Dict()
    {
        var dict = new Dictionary<string, List<(string, string, PartOfSpeech?)>>(StringComparer.Ordinal);
        foreach (var sc in SpecialCases3)
        {
            if (!dict.TryGetValue(sc.Item1, out var list))
            {
                list = [];
                dict[sc.Item1] = list;
            }
            list.Add((sc.Item2, sc.Item3, sc.Item4));
        }
        return dict;
    }

    private List<WordInfo> CombineFinal(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            if (i + 1 >= wordInfos.Count)
            {
                newList?.Add(wordInfos[i]);
                continue;
            }

            var currentWord = wordInfos[i];
            var nextWord = wordInfos[i + 1];

            if (nextWord.Text == "ば" && currentWord.PartOfSpeech == PartOfSpeech.Verb)
            {
                newList ??= CopyAccumulatorUpTo(wordInfos, i);
                var merged = new WordInfo(currentWord);
                merged.Text += nextWord.Text;
                merged.EndOffset = nextWord.EndOffset;
                merged.Reading += nextWord.Reading;
                newList.Add(merged);
                i++;
                continue;
            }

            if (SpecialCases2Dict.TryGetValue(currentWord.Text, out var sc2List))
            {
                // ところで (1343110) only sentence-initially or after た/だ; 静かなところで is locative ところ + で.
                bool tokoroDeBlocked = currentWord.Text == "ところ" && i > 0 &&
                    !(wordInfos[i - 1].Text.EndsWith("た", StringComparison.Ordinal) ||
                      wordInfos[i - 1].Text.EndsWith("だ", StringComparison.Ordinal));

                bool matched = false;
                foreach (var sc in sc2List)
                {
                    if (sc.Second == "で" && tokoroDeBlocked) continue;
                    if (nextWord.Text == sc.Second)
                    {
                        newList ??= CopyAccumulatorUpTo(wordInfos, i);
                        var merged = new WordInfo(currentWord)
                        {
                            Text = currentWord.Text + nextWord.Text,
                            EndOffset = nextWord.EndOffset,
                            Reading = currentWord.Reading + nextWord.Reading,
                            DictionaryForm = currentWord.Text + nextWord.Text
                        };
                        if (sc.Pos != null) merged.PartOfSpeech = sc.Pos.Value;
                        newList.Add(merged);
                        i++;
                        matched = true;
                        break;
                    }
                }
                if (matched) continue;
            }

            if (i + 2 < wordInfos.Count && SpecialCases3Dict.TryGetValue(currentWord.Text, out var sc3List))
            {
                var thirdWord = wordInfos[i + 2];
                bool matched = false;
                foreach (var sc in sc3List)
                {
                    bool thirdMatch = thirdWord.Text == sc.Third ||
                        (sc.Third.Length > 1 && sc.Third[^1] == 'ー' && thirdWord.Text == sc.Third[..^1]);
                    if (nextWord.Text == sc.Item2 && thirdMatch)
                    {
                        newList ??= CopyAccumulatorUpTo(wordInfos, i);
                        var merged = new WordInfo(currentWord)
                        {
                            Text = currentWord.Text + nextWord.Text + sc.Third,
                            EndOffset = thirdWord.EndOffset,
                            Reading = currentWord.Reading + nextWord.Reading + thirdWord.Reading,
                            DictionaryForm = currentWord.Text + nextWord.Text + sc.Third
                        };
                        if (sc.Pos != null) merged.PartOfSpeech = sc.Pos.Value;
                        newList.Add(merged);
                        i += 2;
                        matched = true;
                        break;
                    }
                }
                if (matched) continue;
            }

            if (newList != null) newList.Add(currentWord);
        }

        return newList ?? wordInfos;
    }

    /// <summary>Re-merges と + なる that Sudachi splits before punctuation (トラウマとなり、).</summary>
    private List<WordInfo> CombineToNaru(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        var newList = new List<WordInfo>(wordInfos.Count);
        bool changed = false;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (word is { Text: "と", PartOfSpeech: PartOfSpeech.Particle }
                && i + 1 < wordInfos.Count)
            {
                var next = wordInfos[i + 1];

                bool nextIsNaruForm = next.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary
                                     && (next.DictionaryForm is "なる" or "成る"
                                         || next.NormalizedForm is "なる" or "成る");

                if (nextIsNaruForm && next.Text.Length <= 3)
                {
                    bool prevIsNounLike = newList.Count > 0
                                         && newList[^1].PartOfSpeech is PartOfSpeech.Noun
                                             or PartOfSpeech.Pronoun
                                             or PartOfSpeech.Counter
                                             or PartOfSpeech.Numeral
                                             or PartOfSpeech.NaAdjective;

                    if (prevIsNounLike)
                    {
                        var merged = new WordInfo(next)
                        {
                            Text = word.Text + next.Text,
                            StartOffset = word.StartOffset,
                            EndOffset = next.EndOffset,
                            Reading = word.Reading + next.Reading,
                            DictionaryForm = "となる",
                            NormalizedForm = "なる"
                        };
                        newList.Add(merged);
                        i++;
                        changed = true;
                        continue;
                    }
                }
            }

            newList.Add(word);
        }

        return changed ? newList : wordInfos;
    }

}
