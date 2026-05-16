using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core.Data;
using Jiten.Core.Utils;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    [GeneratedRegex(@"[^\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005\u3001-\u3003\u3008-\u3011\u3014-\u301F\uFF01-\uFF0F\uFF1A-\uFF1F\uFF3B-\uFF3F\uFF5B-\uFF60\uFF62-\uFF65．\n…\u3000―\u2500()。！？「」）|]")]
    private static partial Regex NonJapaneseCharRegex();

    [GeneratedRegex(@"(?<=[\u3040-\u309F\u30A0-\u30FF])[～〜]+")]
    private static partial Regex TildeAfterKanaRegex();

    [GeneratedRegex(@"ー{2,}")]
    private static partial Regex MultipleLongVowelRegex();

    [GeneratedRegex(@"(?<=[一-龯])ー(?=[぀-ゟ])")]
    private static partial Regex EmphLongVowelKanjiHiraganaRegex();

    [GeneratedRegex(@"(?<!を)はやめ")]
    private static partial Regex HayameWithoutWoRegex();

    [GeneratedRegex(@"(?<!が)はやる")]
    private static partial Regex HayaruWithoutGaRegex();

    [GeneratedRegex(@"(外|家)出(ない|なかった|なく)")]
    private static partial Regex DeNaiCompoundRegex();

    [GeneratedRegex(@"(?<=.[\p{IsHiragana}\p{IsCJKUnifiedIdeographs}])(?<!うわ)([っッ])(?![かきくけこがぎぐげござじずぜぞさしすせそたちつてとだぢづでどぱぴぷぺぽばびぶべぼカキクケコガギグゲゴザジズゼゾサシスセソタチツテトダヂヅデドパピプペポバビブベボ\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex EmphaticTsuRegex();

    [GeneratedRegex(@"ホント(バカ|ダメ|マジ|クソ|アホ)")]
    private static partial Regex HontoKatakanaRegex();

    [GeneratedRegex(@"(?<!い)っしょ[ーう]?(?=[\s\n]|$)")]
    private static partial Regex ColloquialSshoRegex();

    [GeneratedRegex(@"(?<=[\u4E00-\u9FAF])番っ")]
    private static partial Regex BanCompoundTsuRegex();

    [GeneratedRegex(@"(?<=(?:どー|どう|そー|そう|こー|こう|ああ|あー))ゆう")]
    private static partial Regex ColloquialYuuRegex();

    [GeneratedRegex(@"(?<=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]{2})…+(?=[^\r\n…])")]
    private static partial Regex MidSentenceEllipsisRegex();

    [GeneratedRegex(@"([ァ-ヴ]ンッ)(?=[ァ-ヴぁ-ゔ\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex KatakanaInterjectionTsuRegex();

    [GeneratedRegex(@"どし(?=[たてよ])")]
    private static partial Regex ColloquialDoshiRegex();

    // ー followed by っ/ッ after hiragana is always emphatic/expressive, never semantically meaningful.
    // e.g., けどーっ → けど, 写るーっ → 写る
    [GeneratedRegex(@"(?<=[぀-ゟ])ー+[っッ]+")]
    private static partial Regex EmphLongVowelSokuonRegex();


    // 3+ identical kana with optional break chars (っ/ッ/、/comma/space) between reps.
    // No Japanese word has 3+ identical kana — this is always stuttering or sound effects.
    // Range ぁ-んァ-ヶ excludes ー (U+30FC) which is handled by MultipleLongVowelRegex.
    [GeneratedRegex(@"([ぁ-んァ-ヶ])([\s、,，っッ]*\1){2,}")]
    private static partial Regex StutteringRunRegex();

    // 3+ identical digraph mora (じょじょじょ, ちゅちゅちゅ, しょしょしょ, etc.)
    // Small kana (ぁぃぅぇぉっゃゅょゎ / ァィゥェォッャュョヮ) cannot start a mora,
    // so (normal kana + small kana) captures exactly one digraph mora.
    [GeneratedRegex(@"([ぁ-んァ-ヶ][ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮ])([\s、,，っッ]*\1){2,}")]
    private static partial Regex StutteringDigraphRunRegex();

    private void PreprocessText(ref string text, bool preserveStopToken, out int rawContentCharCount)
    {
        text = text.Replace("<", " ").Replace(">", " ").Replace("〝", " ").Replace("〟", " ");
        text = text.ToFullWidthDigits();
        text = NonJapaneseCharRegex().Replace(text, "");

        rawContentCharCount = CountContentChars(text);

        if (!preserveStopToken)
            text = text.Replace(_stopToken, "");

        text = text
            .Replace("「", "\n「 ")
            .Replace("」", " 」\n")
            .Replace("〈", " \n〈 ")
            .Replace("〉", " 〉\n")
            .Replace("\n（", " （")
            .Replace("）", " ）\n")
            .Replace("《", " \n《 ")
            .Replace("》", " 》\n")
            .Replace("\u201C", " \n\u201C ")
            .Replace("\u201D", " \u201D\n")
            .Replace("―", " ― ")
            .Replace("。", "\n。\n")
            .Replace("！", "\n！\n")
            .Replace("？", "\n？\n");

        text = TildeAfterKanaRegex().Replace(text, "ー");
        text = MultipleLongVowelRegex().Replace(text, "ー");
        text = EmphLongVowelKanjiHiraganaRegex().Replace(text, "");
        text = EmphLongVowelSokuonRegex().Replace(text, "");

        text = StutteringDigraphRunRegex().Replace(text, "");
        text = StutteringRunRegex().Replace(text, "");

        text = text
            .Replace("垣間見", $"垣間{_stopToken}見")
            .Replace("今手", $"今{_stopToken}手");
        text = HayameWithoutWoRegex().Replace(text, $"は{_stopToken}やめ");
        text = text.Replace("もやる", $"も{_stopToken}やる");
        text = HayaruWithoutGaRegex().Replace(text, $"は{_stopToken}やる");
        text = text
            .Replace("ええんや", $"ええ{_stopToken}んや")
            .Replace("べや", $"べ{_stopToken}や")
            .Replace("はいい", $"は{_stopToken}いい")
            .Replace("元国王", $"元{_stopToken}国王")
            .Replace("なんだろう", $"なん{_stopToken}だろう")
            .Replace("一人静かに", $"一人{_stopToken}静かに")
            .Replace("いやあんま", $"いや{_stopToken}あんま")
            .Replace("この手紙", $"この{_stopToken}手紙")
            .Replace("少女の手", $"少女{_stopToken}の手")
            .Replace("はたまたま", $"は{_stopToken}たまたま")
            .Replace("悶え苦しむ", $"悶え{_stopToken}苦しむ")
            .Replace("悶え苦しん", $"悶え{_stopToken}苦しん")
            ;

        text = text.Replace('頚', '頸');

        text = text.Replace("前出すぎ", $"前{_stopToken}出すぎ");

        text = DeNaiCompoundRegex().Replace(text, $"$1{_stopToken}出$2");
        text = text.Replace("届出さ", $"届{_stopToken}出さ");
        text = text.Replace("ぶっち切", "ぶち切");
        text = text.Replace("ぶっ壊れ", $"ぶっ{_stopToken}壊れ");
        text = EmphaticTsuRegex().Replace(text, $"{_stopToken}$1");
        text = BanCompoundTsuRegex().Replace(text, $"番{_stopToken}っ");

        text = text
            .Replace("水魔法", $"水{_stopToken}魔法")
            .Replace("不適応", $"不{_stopToken}適応")
            .Replace("首落と", $"首{_stopToken}落と")
            .Replace("面の皮", $"面{_stopToken}の皮")
            .Replace("たっけ", $"た{_stopToken}っけ");

        text = HontoKatakanaRegex().Replace(text, $"ホント{_stopToken}$1");
        text = KatakanaInterjectionTsuRegex().Replace(text, $"$1{_stopToken}");

        text = text
            .Replace("バカバカ", $"バカ{_stopToken}バカ")
            .Replace("事大", $"事{_stopToken}大")
            .Replace("日間", $"日{_stopToken}間")
            .Replace("何本", $"何{_stopToken}本")
            .Replace("年未公開", $"年{_stopToken}未公開")
            .Replace("足元気", $"足元{_stopToken}気");

        text = text
            .Replace("来イ", "来い")
            .Replace("にちがいねえ", "にちがいない")
            .Replace("せぇ", "さい")
            .Replace("くせー", "くさい")
            .Replace("ですぅ", "です")
            .Replace("ごめんなさいっ", "ごめんなさい");

        text = text.Replace("でもちょっと", $"でも{_stopToken}ちょっと");
        text = text.Replace("できんよう", $"できん{_stopToken}よう");
        text = ColloquialSshoRegex().Replace(text, $"{_stopToken}っしょ");

        text = ColloquialDoshiRegex().Replace(text, "どうし");
        text = ColloquialYuuRegex().Replace(text, "いう");

        text = MidSentenceEllipsisRegex().Replace(text, "");
        text = text.Replace("…\r", "。\r").Replace("…\n", "。\n");
    }

    private static int CountContentChars(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c is >= '぀' and <= 'ゟ'   // hiragana
                  or >= '゠' and <= 'ヿ'    // katakana (incl. ー)
                  or >= '一' and <= '龯'    // CJK
                  or '々'                       // 々
                  or >= 'Ａ' and <= 'Ｚ'    // fullwidth A-Z
                  or >= 'ａ' and <= 'ｚ'    // fullwidth a-z
                  or >= '０' and <= '９')   // fullwidth 0-9
                count++;
        }
        return count;
    }

    private static void ComputeTokenOffsets(string originalText, List<WordInfo> wordInfos)
    {
        var text = originalText.Replace("\r", "").Replace("\n", "");
        int pos = 0;
        foreach (var word in wordInfos)
        {
            if (string.IsNullOrEmpty(word.Text) || word.PartOfSpeech == PartOfSpeech.BlankSpace)
                continue;

            int found = text.IndexOf(word.Text, pos, StringComparison.Ordinal);
            if (found >= 0)
            {
                word.StartOffset = found;
                word.EndOffset = found + word.Text.Length;
                pos = word.EndOffset;
            }
        }
    }

    private List<SentenceInfo> SplitIntoSentences(string text, List<WordInfo> wordInfos)
    {
        // Normalise text - remove line breaks for consistent sentence boundaries
        text = text.Replace("\r", "").Replace("\n", "");

        // Phase 1: Build sentences AND track their start positions in the normalised text
        // This allows O(1) sentence lookup by position instead of repeated IndexOf calls
        var sentenceData = new List<(SentenceInfo info, int startPos)>();
        var sb = new StringBuilder();
        bool seenEnder = false;
        int sentenceStartPos = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];
            sb.Append(current);

            if (_sentenceEnders.Contains(current))
            {
                seenEnder = true;
                continue;
            }

            if (seenEnder)
            {
                if (_sentenceEnders.Contains(current))
                    continue;

                // Flush sentence (without the last character which belongs to next)
                var sentenceText = sb.ToString(0, sb.Length - 1);
                sentenceData.Add((new SentenceInfo(sentenceText), sentenceStartPos));

                // Next sentence starts at current character position
                sentenceStartPos = i;
                sb.Clear();
                sb.Append(current);
                seenEnder = false;
            }
        }

        if (sb.Length > 0)
        {
            sentenceData.Add((new SentenceInfo(sb.ToString()), sentenceStartPos));
        }

        if (sentenceData.Count == 0)
            return [];

        // Phase 2: Assign words using precomputed offsets
        // Token offsets were computed once from raw Sudachi output (before pipeline stages),
        // then propagated through all merge/split stages. This avoids fragile IndexOf matching
        // that breaks when stages modify token Text (e.g., RepairVowelElongation strips ー).
        int sentenceIdx = 0;

        foreach (var word in wordInfos)
        {
            if (string.IsNullOrEmpty(word.Text) || word.PartOfSpeech == PartOfSpeech.BlankSpace)
                continue;

            if (word.StartOffset < 0 || word.EndOffset < 0)
                continue;

            int wordPos = word.StartOffset;
            int wordEnd = word.EndOffset;

            // Advance to the correct sentence based on word position
            while (sentenceIdx < sentenceData.Count - 1)
            {
                int nextSentenceStart = sentenceData[sentenceIdx + 1].startPos;
                if (wordPos < nextSentenceStart)
                    break;
                sentenceIdx++;
            }

            var (sentence, sentenceStart) = sentenceData[sentenceIdx];
            int sentenceEnd = sentenceStart + sentence.Text.Length;

            // Handle words that span sentence boundaries - merge sentences
            while (wordEnd > sentenceEnd && sentenceIdx + 1 < sentenceData.Count)
            {
                var nextSentence = sentenceData[sentenceIdx + 1].info;
                sentence.Text += nextSentence.Text;
                sentenceData.RemoveAt(sentenceIdx + 1);
                sentenceEnd = sentenceStart + sentence.Text.Length;
            }

            // Calculate position within the sentence and add word
            int posInSentence = wordPos - sentenceStart;
            int spanLength = wordEnd - wordPos;
            sentence.Words.Add((word, posInSentence, spanLength));
        }

        return sentenceData.Select(s => s.info).ToList();
    }
}
