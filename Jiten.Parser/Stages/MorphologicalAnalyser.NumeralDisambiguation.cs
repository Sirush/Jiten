using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Scoring;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    // Clock hours force native readings (四時=よじ, 九時=くじ) that Sudachi's per-token onyomi can't produce.
    private static readonly Dictionary<string, (int WordId, string Reading)> HourPins = new()
    {
        // 一時 is absent: numeral context can't separate いちじ/いっとき/ひととき, and scoring handles o'clock.
        ["二時"] = (2612170, "ニジ"),
        ["三時"] = (1300520, "サンジ"),
        ["四時"] = (1307230, "ヨジ"),
        ["五時"] = (2845367, "ゴジ"),
        ["六時"] = (2583620, "ロクジ"),
        ["七時"] = (2845363, "シチジ"),
        ["八時"] = (2845368, "ハチジ"),
        ["九時"] = (2845349, "クジ"),
        ["十時"] = (2845369, "ジュウジ"),
        ["十一時"] = (2845370, "ジュウイチジ"),
        ["十二時"] = (1334960, "ジュウニジ"),
        ["零時"] = (1557690, "レイジ"),
    };

    // Duration readings for numeral+分/日/月 homographs, applied only before a temporal anchor.
    private static readonly Dictionary<string, (int WordId, byte ReadingIndex)> TemporalDurationPins = new()
    {
        ["一分"] = (1166290, 1),
        ["１分"] = (1166290, 0),
        ["十分"] = (1335070, 1),
        ["１０分"] = (1335070, 0),
        ["五分"] = (2039350, 1),
        ["５分"] = (2039350, 0),
        ["二分"] = (2219180, 1),
        ["２分"] = (2219180, 0),
        ["三分"] = (1814040, 1),
        ["３分"] = (1814040, 0),
        ["四分"] = (2863218, 1),
        ["４分"] = (2863218, 0),
        ["六分"] = (2056150, 1),
        ["６分"] = (2056150, 0),
        ["七分"] = (2864067, 1),
        ["７分"] = (2864067, 0),
        ["何分"] = (1189320, 0),
        ["三十日"] = (1300670, 1),
        ["一月"] = (1162130, 0),
    };

    private static readonly HashSet<string> TemporalAnchorTexts =
        ["前", "後", "間", "ぐらい", "くらい", "ほど", "経つ", "経った", "経って", "待っ"];

    private static void ApplyCounterPins(List<WordInfo> wordInfos, int i)
    {
        var word = wordInfos[i];

        if (word is { Text: "つ", PartOfSpeech: PartOfSpeech.Suffix })
            word.PartOfSpeech = PartOfSpeech.Counter;

        if (word is { PartOfSpeech: PartOfSpeech.Suffix } &&
            word.HasPartOfSpeechSection(PartOfSpeechSection.Counter))
            word.PartOfSpeech = PartOfSpeech.Counter;

        // 人 after a numeral is にん; both じん and にん fit a Suffix tag (６５億人) and scoring picks じん.
        if (word is { Text: "人", PartOfSpeech: PartOfSpeech.Suffix } && i > 0 && IsNumeralToken(wordInfos[i - 1]))
            word.PartOfSpeech = PartOfSpeech.Counter;

        // 度 after a numeral is the counter ど (１０度), never たび.
        if (word is { Text: "度", PreMatchedWordId: null } &&
            i > 0 && AdjacentWordScorer.IsNumericSurface(wordInfos[i - 1].Text))
        {
            word.PreMatchedWordId = 1445160;
            word.PreMatchedReadingIndex = 0;
        }

        // Suffix 色 is いろ (空色) except after a numeral (三色); しょく compounds (特色) are matched whole.
        if (word is { Text: "色", PartOfSpeech: PartOfSpeech.Suffix } && !(i > 0 && IsNumeralToken(wordInfos[i - 1])))
            word.PartOfSpeech = PartOfSpeech.Noun;

        // Sudachi tags いつ as numeral 五 before a counter (いつ匹); standalone いつ is 何時, as 五 lives only in compounds (五日).
        if (word is { Text: "いつ", PartOfSpeech: PartOfSpeech.Noun }
            && word.PartOfSpeechSection1 == PartOfSpeechSection.Numeral)
            word.PartOfSpeech = PartOfSpeech.Pronoun;

        // A lone counter 重 before a noun is the prefix じゅう "heavy" (重戦車); real counters stay compounds (二重).
        // Stays code: depends on the Suffix→Counter reassignment above, which a Cleanup rewrite rule would precede.
        if (word is { Text: "重", PartOfSpeech: PartOfSpeech.Counter } &&
            i + 1 < wordInfos.Count &&
            wordInfos[i + 1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
        {
            word.PartOfSpeech = PartOfSpeech.Prefix;
            word.Reading = "ジュウ";
            word.DictionaryForm = "重";
            word.NormalizedForm = "重";
            word.PreMatchedWordId = 2108240;
        }

        // Kana め after a counted number (２回め) is ordinal 目, which the suffix filter drops; 奴/め follows plain nouns (馬鹿め).
        if (word is { Text: "め", PartOfSpeech: PartOfSpeech.Suffix } && i > 0)
        {
            static bool StartsWithNumber(string t) => t.Length > 0 && JapaneseTextHelper.IsNumeralChar(t[0]);
            static bool IsNumeral(WordInfo w) =>
                IsNumeralToken(w) || w.HasPartOfSpeechSection(PartOfSpeechSection.Amount) || StartsWithNumber(w.Text);

            var prevCtr = wordInfos[i - 1];
            bool prevIsCounterLike = prevCtr.PartOfSpeech is PartOfSpeech.Counter ||
                                     prevCtr.HasPartOfSpeechSection(PartOfSpeechSection.Counter) ||
                                     prevCtr.HasPartOfSpeechSection(PartOfSpeechSection.PossibleCounterWord);
            // CombineAmounts may fuse the number in (２回|め) and drop the counter tag, or leave it separate (３|機|め).
            bool fusedAmount = prevCtr.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Counter or PartOfSpeech.Numeral
                               && StartsWithNumber(prevCtr.Text) && prevCtr.Text.Length > 1;
            bool counterAfterNumber = prevIsCounterLike && i > 1 && IsNumeral(wordInfos[i - 2]);
            if (fusedAmount || counterAfterNumber)
                PinOrdinalMe(word);
        }

        // 目 after a 〜つ counter (三つ目) is ordinal "-th", not the compound noun "three-eyed being".
        if (word is { Text: "目", PartOfSpeech: PartOfSpeech.Suffix } && i > 0
            && wordInfos[i - 1].Text.EndsWith("つ", StringComparison.Ordinal)
            && wordInfos[i - 1].Text.Length > 1
            && TakesOrdinalMeAfterTsu(wordInfos[i - 1].Text[0]))
            PinOrdinalMe(word);
    }

    // Moves i when a numeral token is merged into or split out of the current token.
    private void FixNumeralReadings(List<WordInfo> wordInfos, ref int i)
    {
        var word = wordInfos[i];

        // 後 before a numeral or quantity adverb is アト "more" (後何年, 後少し).
        if (word is { Text: "後", Reading: "ゴ" } &&
            i + 1 < wordInfos.Count &&
            (IsNumeralToken(wordInfos[i + 1]) ||
             wordInfos[i + 1].Text.StartsWith("もう", StringComparison.Ordinal) ||
             wordInfos[i + 1].Text is "少し" or "すこし" or "ちょっと" or "わずか"))
        {
            word.Reading = "アト";
        }

        // 項 after a numeral is the counter こう (第２項), not うなじ "nape".
        if (word is { Text: "項", Reading: "コウ" } && i > 0 && IsNumeralToken(wordInfos[i - 1]))
        {
            word.PreMatchedWordId = 1282980;
        }

        // 体 between 何/a numeral and 目 is the counter たい (何体目), not からだ.
        if (word is { Text: "体", Reading: "タイ" } && i > 0 && i + 1 < wordInfos.Count
            && (wordInfos[i - 1].Text == "何" || IsNumeralToken(wordInfos[i - 1]))
            && wordInfos[i + 1].Text == "目")
        {
            word.PreMatchedWordId = 1409150;
        }

        // Numeral+分/日/月 before a temporal anchor is a duration (十分後 = じっぷん); punctuation blocks it (十分、間を置いて).
        if (word.PreMatchedWordId == null
            && (TemporalDurationPins.ContainsKey(word.Text) || word.Text is "分" or "日" or "月"))
        {
            int anchorIdx = i + 1;
            while (anchorIdx < wordInfos.Count && wordInfos[anchorIdx].PartOfSpeech == PartOfSpeech.BlankSpace)
                anchorIdx++;
            bool anchored = anchorIdx < wordInfos.Count
                && (TemporalAnchorTexts.Contains(wordInfos[anchorIdx].Text)
                    || wordInfos[anchorIdx].Text.StartsWith("待っ", StringComparison.Ordinal)
                    || wordInfos[anchorIdx].Text.StartsWith("経っ", StringComparison.Ordinal));
            if (anchored)
            {
                if (TemporalDurationPins.TryGetValue(word.Text, out var durationPin))
                {
                    word.PreMatchedWordId = durationPin.WordId;
                    word.PreMatchedReadingIndex = durationPin.ReadingIndex;
                }
                else if (word.Text is "分" or "日" or "月" && i > 0
                         && TemporalDurationPins.TryGetValue(wordInfos[i - 1].Text + word.Text, out var pairPin))
                {
                    AbsorbPreviousNumeral(wordInfos, i, wordInfos[i - 1].Reading + word.Reading, pairPin.WordId);
                    word.PreMatchedReadingIndex = pairPin.ReadingIndex;
                    i--;
                }
            }
        }

        // Numeral + 時 merges here; split, the recombiner steals digits (十二|時 → 十 + 二時) and 四+時 reads シジ.
        if (word is { Text: "時", Reading: "ジ", PreMatchedWordId: null } && i > 0
            && HourPins.TryGetValue(wordInfos[i - 1].Text + "時", out var hourPin))
        {
            AbsorbPreviousNumeral(wordInfos, i, hourPin.Reading, hourPin.WordId);
            i--;
        }

        // In N分のN (四分の一) 分 is the part-noun ぶん, not minutes; frames that are entries (三分の一) stay whole.
        if (word.PreMatchedWordId == null && word.Text.Length >= 2 && word.Text.EndsWith('分')
            && word.Text[..^1].All(c => JapaneseTextHelper.IsNumeralChar(c))
            && i + 2 < wordInfos.Count && wordInfos[i + 1].Text == "の"
            && wordInfos[i + 2].Text.Length > 0 && JapaneseTextHelper.IsNumeralChar(wordInfos[i + 2].Text[0])
            && HasNonNameCompoundLookup?.Invoke(word.Text + "の" + wordInfos[i + 2].Text) != true)
        {
            var numeralText = word.Text[..^1];
            var reading = word.Reading ?? "";
            var numeralReading = reading.EndsWith("ブン", StringComparison.Ordinal) ? reading[..^2]
                : reading.EndsWith("プン", StringComparison.Ordinal) || reading.EndsWith("フン", StringComparison.Ordinal)
                    ? reading[..^2]
                    : "";
            var numeral = new WordInfo(word)
            {
                Text = numeralText,
                DictionaryForm = numeralText,
                NormalizedForm = numeralText,
                Reading = numeralReading,
                EndOffset = word.StartOffset >= 0 ? word.StartOffset + numeralText.Length : -1,
                PartOfSpeech = PartOfSpeech.Noun,
            };
            word.Text = "分";
            word.DictionaryForm = "分";
            word.NormalizedForm = "分";
            word.StartOffset = numeral.EndOffset;
            PinFractionBun(word);
            wordInfos.Insert(i, numeral);
            i++;
        }

        // Gated on the frame, not the reading: Sudachi reads a split 分 inconsistently (フン in 七|分|の|一).
        if (word is { Text: "分", PreMatchedWordId: null } && i > 0 && i + 2 < wordInfos.Count
            && wordInfos[i - 1].Text.Length > 0
            && wordInfos[i - 1].Text.All(c => JapaneseTextHelper.IsNumeralChar(c))
            && wordInfos[i + 1].Text == "の"
            && wordInfos[i + 2].Text.Length > 0 && JapaneseTextHelper.IsNumeralChar(wordInfos[i + 2].Text[0])
            && HasNonNameCompoundLookup?.Invoke(wordInfos[i - 1].Text + "分の" + wordInfos[i + 2].Text) != true)
            PinFractionBun(word);
    }

    private static void PinOrdinalMe(WordInfo word)
    {
        word.DictionaryForm = "目";
        word.NormalizedForm = "目";
        word.PreMatchedWordId = 1604890;
        word.PreMatchedReadingIndex = 0;
        word.HardPinned = true;
    }

    private static void PinFractionBun(WordInfo word)
    {
        word.Reading = "ブン";
        word.PreMatchedWordId = 1502860;
        word.HardPinned = true;
    }

    // The caller must step i back.
    private static void AbsorbPreviousNumeral(List<WordInfo> wordInfos, int i, string reading, int wordId)
    {
        var word = wordInfos[i];
        var numeral = wordInfos[i - 1];
        word.Text = numeral.Text + word.Text;
        word.Reading = reading;
        word.StartOffset = numeral.StartOffset;
        word.PartOfSpeech = PartOfSpeech.Noun;
        word.DictionaryForm = word.Text;
        word.NormalizedForm = word.Text;
        word.PreMatchedWordId = wordId;
        wordInfos.RemoveAt(i - 1);
    }

    private static bool IsNumeralToken(WordInfo w) =>
        w.PartOfSpeech == PartOfSpeech.Numeral || w.HasPartOfSpeechSection(PartOfSpeechSection.Numeral);

    // 一つ目/二つ目 have their own ordinal entries.
    private static bool TakesOrdinalMeAfterTsu(char c) =>
        c is not ('一' or '二' or '１' or '２') && JapaneseTextHelper.IsNumeralChar(c);
}
