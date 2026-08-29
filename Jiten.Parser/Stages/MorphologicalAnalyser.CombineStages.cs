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

            // Iteratively try to merge subsequent tokens
            while (i + 1 < wordInfos.Count)
            {
                var nextWord = wordInfos[i + 1];

                if (ShouldStopMerging(currentWord, nextWord, wordInfos, i, currentPOS))
                    break;

                // Check if valid inflection part
                bool isValidPart = PosMapper.IsInflectionPart(nextWord.PartOfSpeech) ||
                                   nextWord.HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem) ||
                                   nextWord.HasPartOfSpeechSection(PartOfSpeechSection.ConjunctionParticle) ||
                                   nextWord.HasPartOfSpeechSection(PartOfSpeechSection.Dependant) ||
                                   nextWord.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant);

                // Sudachi tags やれ as interjection, but after て-form it's the imperative of auxiliary やる
                if (!isValidPart && nextWord is { Text: "やれ", PartOfSpeech: PartOfSpeech.Interjection } &&
                    currentWord.Text.EndsWith("て"))
                    isValidPart = true;

                // Sudachi sometimes tags colloquial ねえ (= ない negative) as noun (姉)
                // After te/de-form, ねえ is the negative auxiliary, not the word for sister
                if (!isValidPart && nextWord is { Text: "ねえ", PartOfSpeech: PartOfSpeech.Noun } &&
                    (currentWord.Text.EndsWith("て") || currentWord.Text.EndsWith("で")))
                    isValidPart = true;

                // Greedy steal: handle そうだ/そうか by taking just そう if it forms valid inflection
                // e.g., 新しそうだ → 新しそう + だ, 話そうか → 話そう + か
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

                        // Modify the original token to be just だ or か for subsequent processing
                        string remainder = nextWord.Text == "そうだ" ? "だ" : "か";
                        wordInfos[i + 1] = new WordInfo
                                           {
                                               Text = remainder, DictionaryForm = remainder,
                                               PartOfSpeech = remainder == "だ" ? PartOfSpeech.Auxiliary : PartOfSpeech.Particle,
                                               Reading = remainder
                                           };
                        // Don't increment i - let the remainder be processed as a new token in the main loop
                        break;
                    }
                }

                // Handle なさそう: negative-appearance suffix (e.g., 食べなさそう = seems like one can't eat)
                // なさそう is tagged NaAdjective by Sudachi so doesn't pass isValidPart, but it attaches to
                // the negative stem (mizenkei) which is the same as the masu-stem for ichidan verbs
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

                // Kansai-ben negative せん (= しない): Sudachi tags this as a plain noun/prefix,
                // but after a PossibleSuru base it's a valid inflection (e.g. 卑下せん → 卑下する neg.)
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

                // A vs-only noun + す directly before べき/べし is the classical する+べき "should" expression
                // (帰投すべき → 帰投 + すべき), NOT a verb: merging it yields a bogus short-causative reading
                // (帰投す "make return"). Keep す standalone so it merges rightward into すべき. Real godan -す
                // verbs (愛す, 訳す) keep merging here, since [stem]す is itself a dictionary verb.
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
                            if (t.StartsWith("stem-") && t != "stem-adj-base") { hasVerbStemTag = true; break; }

                        if (hasVerbStemTag)
                        {
                            DeconjugationForm? verbForm = null;
                            foreach (var f in forms)
                            {
                                if (f.Text != targetHiragana && f.Tags.Count > 0 &&
                                    f.Tags[^1].StartsWith("v") &&
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
                         !currentWord.Text.EndsWith("て") &&
                         !currentWord.Text.EndsWith("で") &&
                         !currentWord.Text.EndsWith("たく") &&
                         !currentWord.Text.EndsWith("なく") &&
                         !currentWord.Text.EndsWith("たり") &&
                         !currentWord.Text.EndsWith("だり") &&
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
                        currentPOS = match.Tags.Count > 0 && match.Tags[^1] == "adj-i"
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
            if (forms[i].Text.EndsWith(suffix) && forms[i].Text.Length > suffix.Length) return forms[i];
        return null;
    }

    private static bool ShouldStopMerging(WordInfo currentWord, WordInfo nextWord,
        List<WordInfo> wordInfos, int i, PartOfSpeech currentPOS)
    {
        // Allow negative stem な when followed by すぎる (e.g., わからなすぎる)
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

        // Standalone よう is always noun 様, not a volitional suffix (those are single tokens)
        if (nextWord is { Text: "よう", DictionaryForm: "よう" })
            return true;

        // いけ after ちゃ/じゃ/きゃ/にゃ is obligation/prohibition, not compound
        if (nextWord.DictionaryForm == "いける" &&
            (currentWord.Text.EndsWith("ちゃ") || currentWord.Text.EndsWith("じゃ") ||
             currentWord.Text.EndsWith("きゃ") || currentWord.Text.EndsWith("にゃ")))
            return true;

        if (nextWord is { Text: "ん", DictionaryForm: "の" or "ん" })
            return true;

        // って before ん/んだ/んです is quotative, not te-form
        if (nextWord.Text == "って" && i + 2 < wordInfos.Count &&
            wordInfos[i + 2].Text is "ん" or "んだ" or "んです")
            return true;

        // って re-cut as quotative (DictionaryForm って, vs て for a real te-particle) stays split
        // when followed by a quote-taking verb (かな+って+思ったら). Otherwise allow the re-merge —
        // an auxiliary continuation (つか+って+ください, かな+って+いる) proves the re-cut wrong.
        // Both kanji and kana dictionary forms are listed: Sudachi tags 言う as the kana いう just as
        // often (寄ってくる+って+いう → keep くる|って split, never くる+って glued into a blob).
        if (nextWord is { Text: "って", DictionaryForm: "って" } && i + 2 < wordInfos.Count &&
            wordInfos[i + 2].DictionaryForm is "思う" or "おもう" or "言う" or "いう"
                or "聞く" or "きく" or "考える" or "感じる")
            return true;

        // Re-cut って before a noun (なくなった+って+話), punctuation, or sentence end is the
        // quotative/たって particle — a te-form continuation needs a verb/auxiliary after it.
        // Re-merging makes an unresolvable blob (なくなったって has no deconjugation path) that
        // would be dropped from output entirely.
        if (nextWord is { Text: "って", DictionaryForm: "って" } &&
            (i + 2 >= wordInfos.Count ||
             wordInfos[i + 2].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                 or PartOfSpeech.SupplementarySymbol))
            return true;

        // Re-cut って before a sentence-ending particle (待つ+って+さ, 行く+って+よ/ね/わ) is the
        // quotative — a te-form continuation needs a verb/auxiliary after it, never a final particle.
        // Without this the verb re-absorbs って into an unresolvable blob (待つって).
        if (nextWord is { Text: "って", DictionaryForm: "って" } && i + 2 < wordInfos.Count
            && wordInfos[i + 2] is { PartOfSpeech: PartOfSpeech.Particle }
            && wordInfos[i + 2].Text is "さ" or "よ" or "ね" or "わ" or "ぞ" or "ぜ")
            return true;

        // Benefactive auxiliaries after a te-form stay separate tokens (堪能させて|いただきます,
        // 繕って|貰いて). The て+貰う/いただく deconjugator rules exist for chain display on tokens
        // merged by the Dependant path (して貰いたい) — they must not widen this stage's merges.
        if ((currentWord.Text.EndsWith("て") || currentWord.Text.EndsWith("で"))
            && nextWord.DictionaryForm is "いただく" or "頂く" or "貰う")
            return true;

        // Te-form auxiliaries attach to VERB te-forms only; after an adjective くて the next
        // verb starts its own clause (頭が良くて + やりたい, never 良い + [do-for-someone]).
        if (currentPOS == PartOfSpeech.IAdjective && currentWord.Text.EndsWith("て")
            && nextWord.PartOfSpeech == PartOfSpeech.Verb)
            return true;

        if (currentWord.Text.EndsWith("ん") && nextWord.Text is "だ" or "です")
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

    // The prefix combine runs long before noun compounding, so an honorific that is merely the
    // outermost layer can consume the head of the compound underneath it (お|母|上 → お母, stranding
    // 上). The head belongs to the longer attested compound; the prefix then stands alone, which is
    // the correct reading (お + 母上). Only diverts when the three-token whole is NOT itself a word,
    // so お+手+紙 → お手紙 is untouched.
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

        List<WordInfo> newList = new List<WordInfo>(wordInfos.Count);
        int i = 0;

        while (i < wordInfos.Count)
        {
            var currentWord = new WordInfo(wordInfos[i]);

            // The emphatic prefix ど is tagged Adverb (truncated どう) by Sudachi; before an
            // i-adjective it is the intensifier (ど偉い, どでかい) — the attested-compound guards
            // below decide whether a real compound exists.
            bool isEmphaticDo = currentWord.Text == "ど" && currentWord.PartOfSpeech == PartOfSpeech.Adverb
                && i + 1 < wordInfos.Count && wordInfos[i + 1].PartOfSpeech == PartOfSpeech.IAdjective;

            if ((currentWord.PartOfSpeech == PartOfSpeech.Prefix || isEmphaticDo) && i + 1 < wordInfos.Count)
            {
                var nextWord = wordInfos[i + 1];
                bool isKanjiPrefix = IsKanjiPrefix(currentWord.Text);

                // Kanji prefixes (相, 再, 不, etc.) can combine with verbs/adjectives to form compound nouns
                // Kana prefixes (お, ご) should only combine with nouns/NaAdjectives
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
                        newList.Add(currentWord);
                        i += 2;
                        continue;
                    }

                    // Classical/inflected i-adjective: the conjugated surface isn't a dictionary
                    // entry but the modern dictionary form of the compound is (故 + 無き → 故無い
                    // 2112310). Keep IAdjective so the deconjugator reaches the 連体形 → adj-i.
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
                            newList.Add(currentWord);
                            i += 2;
                            continue;
                        }
                    }

                    // Reading-based compound: Sudachi's reading may differ from the surface for
                    // colloquial/contracted forms (e.g., 古 + くせー reading=クサイ → 古くさい).
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
                                newList.Add(currentWord);
                                i += 2;
                                continue;
                            }
                        }
                    }

                    // Try partial combination: prefix + beginning of next token
                    // Only when the next token itself is NOT a valid word (Sudachi drew wrong boundaries)
                    // e.g. 相+当腹 → 相当+腹 (当腹 is not a valid word, so Sudachi mis-segmented)
                    // The remainder must be a word too: a re-cut that strands a multi-char unattested
                    // blob is not a boundary repair (おバ[小母] + junk). A single stray kana is fine —
                    // the stutter filter cleans it (お+にぃ → おに + ぃ).
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

            newList.Add(currentWord);
            i++;
        }

        return newList;
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

    private List<WordInfo> CombineTte(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        WordInfo currentWord = wordInfos[0];
        bool isCopy = false;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo nextWord = wordInfos[i];

            if (currentWord.Text.EndsWith("っ") && nextWord.Text.StartsWith("て"))
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

    // Quotative って + the kana verb いう fuse into the single relativiser っていう (= という,
    // JMDict 2757880), matching how ってのは/たって already surface as one cluster. Restricted to the
    // kana dictionary form いう: the kanji 言う is the lexical verb "to say" (だって|言う|人) and stays
    // split. A conjugated いう (いって/いった) is a real verb form and is likewise left alone.
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
                newList ??= [..wordInfos[..i]];
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

            // A quotative って Sudachi glues onto a volitional predicate (しようって, 来ようって) instead of
            // splitting also fails to form っていう before kana いう. Split って back off and cluster.
            // Gated so the stem before って ends in a う-row kana (the volitional う) and is at least
            // 2 chars — the length guard skips the bare うって Sudachi strands off some volitionals
            // (やろ|うって), which this can't reattach. A te-form って leaves a bare stem ending in a
            // kanji or あ/い-row char (黙って, 言って, 買って), never う-row, so a te-form is never
            // mis-split; the copula だって (stem だ) is excluded for the same reason.
            if (i + 1 < wordInfos.Count
                && word.Text.Length > 3
                && word.Text.EndsWith("って", StringComparison.Ordinal)
                && wordInfos[i + 1] is { Text: "いう", DictionaryForm: "いう" }
                && IsQuotativeTteStem(word.Text[..^2]))
            {
                newList ??= [..wordInfos[..i]];
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

    // A stem that a quotative って attaches to ends in a う-row kana (terminal verb / volitional).
    // A te-form って leaves a bare stem ending elsewhere, so this never matches one.
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

    private List<WordInfo> CombineAdverbialParticle(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo>? newList = null;
        WordInfo currentWord = wordInfos[0];
        bool isCopy = false;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo nextWord = wordInfos[i];

            if (nextWord.HasPartOfSpeechSection(PartOfSpeechSection.AdverbialParticle) &&
                (nextWord.DictionaryForm == "だり" || nextWord.DictionaryForm == "たり") &&
                currentWord.PartOfSpeech == PartOfSpeech.Verb)
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

    private List<WordInfo> CombineConjunctiveParticle(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo> newList = [wordInfos[0]];

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo currentWord = wordInfos[i];
            WordInfo previousWord = newList[^1];
            bool combined = false;

            if (currentWord.HasPartOfSpeechSection(PartOfSpeechSection.ConjunctionParticle) &&
                currentWord.Text is "て" or "で" or "ちゃ" or "ば" &&
                previousWord.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary)
            {
                previousWord.Text += currentWord.Text;
                previousWord.EndOffset = currentWord.EndOffset;
                previousWord.Reading += currentWord.Reading;
                combined = true;
            }

            if (!combined)
            {
                newList.Add(currentWord);
            }
        }

        return newList;
    }

    // The quote-taking verbs that mark a preceding って as quotative — the same set the
    // ShouldStopMerging re-cut uses (kanji and kana lemmas both: Sudachi tags 言う as いう freely).
    private static bool IsQuoteTakingVerb(WordInfo w) =>
        w.DictionaryForm is "思う" or "おもう" or "言う" or "いう" or "聞く" or "きく" or "考える" or "感じる";

    private List<WordInfo> CombineAuxiliary(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        var deconjugator = Deconjugator.Instance;
        IReadOnlyList<DeconjugationForm> Deconj(string h) => deconjugator.Deconjugate(h);

        List<WordInfo> newList =
        [
            wordInfos[0]
        ];
        bool changed = false;

        for (int i = 1; i < wordInfos.Count; i++)
        {
            WordInfo currentWord = wordInfos[i];
            WordInfo previousWord = newList[^1];
            bool combined = false;

            if (currentWord.PartOfSpeech != PartOfSpeech.Auxiliary)
            {
                // Copula である: merge copula で (reclassified to Particle but dictForm stays だ) with following ある form
                if (previousWord is { Text: "で", DictionaryForm: "だ" } &&
                    currentWord.DictionaryForm is "ある" or "有る")
                {
                    previousWord.Text = "で" + currentWord.Text;
                    previousWord.EndOffset = currentWord.EndOffset;
                    previousWord.Reading += currentWord.Reading;
                    previousWord.PartOfSpeech = currentWord.PartOfSpeech;
                    previousWord.DictionaryForm = "である";
                    changed = true;
                }
                else
                {
                    newList.Add(currentWord);
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
                // A pinned auxiliary is a repair stage's explicit decision that this token is its own
                // vocabulary item (した+んだ) — absorbing it would erase that word from the output.
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
                // A って-final copula before a quote-taking verb carries the quotative, not an
                // inflection: 大袈裟|だって|言いたい is 大袈裟だ + って + 言いたい, never the
                // 大袈裟だった-style fold. Only the copula だ — a volitional ようって must fold
                // (来ようって|いう) and gets re-cut by CombineQuotativeToIu afterwards.
                && !(currentWord.DictionaryForm == "だ"
                     && currentWord.Text.EndsWith("って", StringComparison.Ordinal)
                     && i + 1 < wordInfos.Count && IsQuoteTakingVerb(wordInfos[i + 1]))
                && currentWord.Text != "なのだ"
                && !currentWord.Text.StartsWith("なん")
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

            if (combined) changed = true;

            if (!combined)
            {
                newList.Add(currentWord);
            }
        }

        return changed ? newList : wordInfos;
    }

    // Completion auxiliaries that Sudachi tokenises as a bare verb after a 連用形 stem when the
    // compound is absent from its own lexicon (逃げ|切った). Merged only when JMDict attests the
    // compound (逃げ切る), so an ordinary main-verb use (紙を切った) is never touched.
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
                result ??= [..wordInfos[..i]];
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

        List<WordInfo> newList = new List<WordInfo>();
        WordInfo currentWord = new WordInfo(wordInfos[0]);

        for (int i = 1; i < wordInfos.Count; i++)
        {
            var nextWord = wordInfos[i];

            // Combine AuxiliaryVerbStem (そう, etc.) with preceding verb/adjective
            // Also handle adjectival suffixes like やすい, にくい, づらい (their stem forms: やす, にく, づら)
            var isAdjectivalSuffix = wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Suffix &&
                                     wordInfos[i - 1].DictionaryForm.EndsWith("い");
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
                currentWord.Text += nextWord.Text;
                currentWord.EndOffset = nextWord.EndOffset;
                currentWord.Reading += nextWord.Reading;
            }
            else
            {
                newList.Add(currentWord);
                currentWord = new WordInfo(nextWord);
            }
        }

        newList.Add(currentWord);

        return newList;
    }

    private List<WordInfo> CombineSuffix(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2)
            return wordInfos;

        List<WordInfo> newList = new List<WordInfo>();
        WordInfo currentWord = new WordInfo(wordInfos[0]);

        for (int i = 1; i < wordInfos.Count; i++)
        {
            var nextWord = wordInfos[i];

            if ((wordInfos[i].PartOfSpeech == PartOfSpeech.Suffix || wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.Suffix))
                && (wordInfos[i].DictionaryForm == "っこ"
                    || wordInfos[i].DictionaryForm == "さ"
                    // がる only attaches to adjective stems (怖がる) — never to a pronoun host
                    // (何|がって is case-particle が + quotative って, not 何がる)
                    || (wordInfos[i].DictionaryForm == "がる" && currentWord.PartOfSpeech != PartOfSpeech.Pronoun)
                    || (wordInfos[i].DictionaryForm is "ぶり" or "振り" &&
                        currentWord.PartOfSpeech == PartOfSpeech.IAdjective &&
                        !currentWord.Text.EndsWith("い") && currentWord.DictionaryForm.EndsWith("い"))
                    || (wordInfos[i].DictionaryForm == "ら" &&
                        wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Pronoun && wordInfos[i - 1].Text != "貴様")))
            {
                currentWord.Text += nextWord.Text;
                currentWord.EndOffset = nextWord.EndOffset;
                currentWord.Reading += nextWord.Reading;
            }
            else if (wordInfos[i].DictionaryForm == "ぶる"
                     && (wordInfos[i].PartOfSpeech == PartOfSpeech.Suffix || wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.Suffix))
                     && HasCompoundLookup != null
                     && HasCompoundLookup(currentWord.DictionaryForm + "ぶる"))
            {
                currentWord.DictionaryForm += "ぶる";
                currentWord.PartOfSpeech = PartOfSpeech.Verb;
                currentWord.Text += nextWord.Text;
                currentWord.EndOffset = nextWord.EndOffset;
                currentWord.Reading += nextWord.Reading;
            }
            // Handle がったり misparsed as adverb after adjective stem (e.g., 怖がったり, 悲しがったり)
            // Sudachi sometimes parses these as: adj-stem + がったり (adverb) instead of correctly splitting
            else if (nextWord is { PartOfSpeech: PartOfSpeech.Adverb, Text: "がったり" }
                     && currentWord.PartOfSpeech == PartOfSpeech.IAdjective
                     && !currentWord.Text.EndsWith("い")
                     && currentWord.DictionaryForm.EndsWith("い"))
            {
                currentWord.Text += nextWord.Text;
                currentWord.EndOffset = nextWord.EndOffset;
                currentWord.Reading += nextWord.Reading;
            }
            else
            {
                newList.Add(currentWord);
                currentWord = new WordInfo(nextWord);
            }
        }

        newList.Add(currentWord);
        return newList;
    }

    // Every kanji of the surface survives into the candidate base — the base is a spelling of
    // this surface, not merely a lemma the deconjugator can reach from it.
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

            // じまい (仕舞い) is a genuine suffix that attaches to verb ず-forms (e.g., わからずじまい)
            // Honorific suffixes (さん/くん/ちゃん/様/殿/氏) are always person-title suffixes, never reclassified
            if (wordInfos[i].DictionaryForm is "じまい" or "仕舞い" or "ちゃん" or "さん" or "くん" or "様" or "殿" or "氏")
                continue;

            // A suffix is a lexical item in its own right, so its surface is attested (たち, ども,
            // 的, 長, っぱなし). A Suffix-tagged surface that is NOT attested but deconjugates to an
            // attested verb is a conjugated verb Sudachi bound to the preceding noun
            // (レッテル|貼り, フライヤー|貼り) — its own spelling has no suffix entry, and the fold to
            // one (貼り → 張り's ばり) is a different word. Route it to the verb path.
            // Scoped to kanji-bearing surfaces whose base keeps those kanji (貼り → 貼る): that is
            // what makes the token a spelling of the verb rather than a lemma the deconjugator
            // merely reaches. Kana suffixes are excluded by construction — a conjugating suffix
            // (ぶっ+た → ぶった) deconjugates to unrelated verbs (打つ) and must stay a suffix.
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
                    // Same contract as the noun exit: a Sudachi-bound suffix must not anchor
                    // compound/expression windows after reclassification.
                    wordInfos[i].WasReclassifiedFromSuffix = true;
                    break;
                }

                if (wordInfos[i].PartOfSpeech == PartOfSpeech.Verb)
                    continue;
            }

            // Sudachi shreds an OOV katakana adjective into 名詞 + 接尾辞 (チッチャ|い) and then tags
            // the following content word 接尾辞 too — a "suffix chain" anchored on an adjective
            // tail, not a nominal host. A bare predicate ending cannot host a suffix, so it does
            // not count as one here (車 after チッチャい must reclassify to reach its noun entry).
            var prevInfo = wordInfos[i - 1];
            bool prevIsPredicateTailSuffix = prevInfo.PartOfSpeech == PartOfSpeech.Suffix
                                             && prevInfo.Text is "い" or "く" or "かっ";
            var prev = prevInfo.PartOfSpeech;
            if (prev is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Numeral or PartOfSpeech.Prefix or PartOfSpeech.Pronoun
                || (prev == PartOfSpeech.Suffix && !prevIsPredicateTailSuffix))
                continue;

            // Adjectival suffixes (形容詞的) like くさい, らしい, っぽい should keep their POS
            // so the parser's Adjectival section check routes them through the verb/adj branch.
            // NaAdjectiveLike (形状詞的) like 気 can start compound expressions (e.g. 気を引き締める)
            // so don't mark them as reclassified — that would block the compound detection window.
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

        List<WordInfo> newList = new List<WordInfo>();
        bool changed = false;
        int i = 0;
        while (i < wordInfos.Count)
        {
            WordInfo currentWord = wordInfos[i];

            // Combine かもしれ* (kamoshirenai, kamoshiremasen, etc.) into single expression.
            // The ん-contracted しんない/しんねえ is the same expression — the deconjugator's
            // しんない ending-rule recovers かもしれない from the fused tail.
            if (i + 2 < wordInfos.Count &&
                currentWord.Text == "か" &&
                wordInfos[i + 1].Text == "も" &&
                (wordInfos[i + 2].Text.StartsWith("しれ") ||
                 wordInfos[i + 2].Text.StartsWith("しんな") || wordInfos[i + 2].Text.StartsWith("しんね")))
            {
                WordInfo combinedWord = new WordInfo(currentWord);
                combinedWord.Text = currentWord.Text + wordInfos[i + 1].Text + wordInfos[i + 2].Text;
                combinedWord.EndOffset = wordInfos[i + 2].EndOffset;
                combinedWord.Reading = currentWord.Reading + wordInfos[i + 1].Reading + wordInfos[i + 2].Reading;
                combinedWord.PartOfSpeech = PartOfSpeech.Expression;
                newList.Add(combinedWord);
                i += 3;
                changed = true;
                continue;
            }

            // Same expression when か+も has already been fused into かも upstream (the polite
            // かもしれません and the plain かもしれない both reach here as [かも][しれ*] once the
            // ん-repair has glued the しれ* tail): join the remaining しれ* token.
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
                newList.Add(combinedWord);
                i += 2;
                changed = true;
                continue;
            }

            // A fused じゃない / ではない token directly followed by か → じゃないか expression. Sudachi usually
            // splits では|ない|か (joined below), but when ない is already glued into a single じゃない token
            // the か is left stranded; rejoin it so じゃないか stays one unit. Only the copula-negative
            // expression merges — a verb's negative (しない か, 飲む か) keeps the question particle separate.
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
                newList.Add(combinedWord);
                i += 2;
                changed = true;
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

                    // では + conjugated form of ない (+ optional か) → ではない(か) expression
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

                            newList.Add(combinedWord);
                            i += consumed;
                            changed = true;
                            continue;
                        }
                    }

                    newList.Add(combinedWord);
                    i += 2;
                    changed = true;
                    continue;
                }
            }

            newList.Add(new WordInfo(currentWord));
            i++;
        }

        return changed ? newList : wordInfos;
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
                newList ??= CopyUpTo(wordInfos, i);
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
                // ところ+で → ところで (1343110: sentence-initial "by the way", or 〜たところで "even if").
                // Mid-sentence after a non-past stem it is the locative ところ + で (静かなところで, 今のところで).
                bool tokoroDeBlocked = currentWord.Text == "ところ" && i > 0 &&
                    !(wordInfos[i - 1].Text.EndsWith("た", StringComparison.Ordinal) ||
                      wordInfos[i - 1].Text.EndsWith("だ", StringComparison.Ordinal));

                bool matched = false;
                foreach (var sc in sc2List)
                {
                    if (sc.Second == "で" && tokoroDeBlocked) continue;
                    if (nextWord.Text == sc.Second)
                    {
                        newList ??= CopyUpTo(wordInfos, i);
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
                        newList ??= CopyUpTo(wordInfos, i);
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

    private static List<WordInfo> CopyUpTo(List<WordInfo> source, int count)
    {
        var list = new List<WordInfo>(source.Count);
        for (int i = 0; i < count; i++)
            list.Add(source[i]);
        return list;
    }

    /// <summary>
    /// Re-merges と (particle) + conjugated なる that Sudachi splits when punctuation follows.
    /// E.g. トラウマとなり、 → Sudachi: と + なり + 、; should be: となり + 、
    /// </summary>
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
