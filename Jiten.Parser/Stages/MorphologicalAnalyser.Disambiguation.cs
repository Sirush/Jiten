using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Scoring;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    private List<WordInfo> ApplyContextPins(List<WordInfo> wordInfos)
    {
        for (int i = wordInfos.Count - 1; i >= 0; i--)
        {
            var word = wordInfos[i];
            if (word.Text is "なん" or "フン" or "ふん")
                word.PartOfSpeech = PartOfSpeech.Prefix;

            if (word.Text == "そう")
                word.PartOfSpeech = PartOfSpeech.Adverb;

            // Kana ツバ is 唾, or 鍔 near 刀の/帽子; both pinned, as the context-blind word cache reuses whichever was cached first.
            if (word.Text is "ツバ" or "つば" && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
            {
                bool swordContext = i >= 2 && wordInfos[i - 1].Text == "の"
                    && wordInfos[i - 2].Text is "刀" or "剣" or "太刀" or "刃";
                bool hatContext = false;
                for (int k = Math.Max(0, i - 2); k < Math.Min(wordInfos.Count, i + 5) && !hatContext; k++)
                    hatContext = wordInfos[k].Text.Contains("帽子", StringComparison.Ordinal);
                word.PreMatchedWordId = swordContext || hatContext ? 1433790 : 1408410;
            }

            // Sudachi tags the noun 張り "tension" a suffix too (張りのある, 声に張りがない); a true suffix needs a noun host.
            if (word is { Text: "張り", PartOfSpeech: PartOfSpeech.Suffix or PartOfSpeech.Noun or PartOfSpeech.CommonNoun, PreMatchedWordId: null }
                && (i == 0 || (wordInfos[i - 1].PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                        or PartOfSpeech.Name or PartOfSpeech.Pronoun or PartOfSpeech.Suffix)
                    && wordInfos[i - 1].Text is not ("」" or "』" or "）" or ")"))))
            {
                word.PartOfSpeech = PartOfSpeech.Noun;
                word.PreMatchedWordId = 1427760;
            }

            // Verb 連用形 + 方 is かた "way of doing", never ほう.
            if (word.Text.Length >= 3 && word.Text[^1] == '方' && word.PreMatchedWordId == null
                && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && HasNonNameCompoundLookup?.Invoke(word.Text) != true
                && TryGetRenyoukeiVerb(word.Text[..^1]) is { } stemVerb)
            {
                var stemText = word.Text[..^1];
                int cut = word.StartOffset >= 0 ? word.StartOffset + stemText.Length : -1;
                wordInfos.Insert(i + 1, new WordInfo(word)
                {
                    Text = "方", DictionaryForm = "方", NormalizedForm = "方", Reading = "カタ",
                    PartOfSpeech = PartOfSpeech.Noun, StartOffset = cut,
                    PreMatchedWordId = 1516925, PreMatchedReadingIndex = 0,
                });
                word.Text = stemText;
                word.DictionaryForm = stemVerb;
                word.NormalizedForm = stemVerb;
                word.Reading = word.Reading is { Length: > 2 } r && r.EndsWith("カタ", StringComparison.Ordinal) ? r[..^2] : "";
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.PartOfSpeechSection1 = PartOfSpeechSection.None;
                word.EndOffset = cut;
            }
            else if (word is { Text: "方", PreMatchedWordId: null } && i > 0
                     && wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb
                     && TryGetRenyoukeiVerb(wordInfos[i - 1].Text) != null)
            {
                word.PreMatchedWordId = 1516925;
                word.PreMatchedReadingIndex = 0;
            }

            // The user-dic やしない makes Sudachi misread the ichidan stem before it (バレ[地名]); it only follows a 連用形.
            if (i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "やしない"
                && word.PartOfSpeech is not PartOfSpeech.Verb && word.PreMatchedWordId == null
                && word.Text.Length >= 2 && HasVerbOrAdjectiveLookup?.Invoke(NormalizeToHiragana(word.Text) + "る") == true)
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                ClearPosSections(word);
                word.DictionaryForm = word.Text + "る";
                word.NormalizedForm = word.Text + "る";
            }

            // Kana うら in a noun slot (舞台のうらで) is 裏; the archaic pronoun only heads a clause (うらは).
            if (word is { Text: "うら", PartOfSpeech: PartOfSpeech.Pronoun, PreMatchedWordId: null }
                && ((i > 0 && wordInfos[i - 1].Text == "の")
                    || (i + 1 < wordInfos.Count && wordInfos[i + 1].Text is "に" or "で" or "から" or "へ" or "の" or "を" or "側")))
            {
                word.PartOfSpeech = PartOfSpeech.Noun;
                word.PreMatchedWordId = 1550190;
                word.PreMatchedReadingIndex = 1;
            }

            // "Ouch" あいた stands alone; before a noun or まま it is 開く's past (あいたままの扉).
            if (word is { Text: "あいた", PartOfSpeech: PartOfSpeech.Interjection, PreMatchedWordId: null }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Adverb
                && wordInfos[i + 1].Text is { Length: > 0 } nextText
                && (JapaneseTextHelper.IsKanji(nextText[0]) || (JapaneseTextHelper.IsKana(nextText[0]) && nextText[0] is not ('っ' or 'ッ'))))
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "あく";
                word.NormalizedForm = "開く";
                word.PreMatchedWordId = 1586270;
                word.PreMatchedReadingIndex = 3;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, "あく");
            }

            // Kana さらう defaults to 攫う (さらわれた, 話題をさらう); 浚う needs a dredging object, 復習う a memory/lines one.
            if (word.DictionaryForm == "さらう" && word.PreMatchedWordId == null
                && word.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Unknown
                && word.Text.StartsWith("さら", StringComparison.Ordinal))
            {
                string? contextWord = null;
                for (int k = Math.Max(0, i - 3); k < i && contextWord == null; k++)
                    if (wordInfos[k].Text is "川" or "池" or "溝" or "ドブ" or "どぶ" or "底" or "川底" or "泥" or "井戸" or "砂" or "堀"
                        or "記憶" or "情報" or "復習" or "稽古" or "練習" or "台詞" or "セリフ" or "楽譜" or "曲" or "資料" or "内容")
                        contextWord = wordInfos[k].Text;

                (word.PreMatchedWordId, word.PreMatchedReadingIndex) = contextWord switch
                {
                    null => (1593870, (byte)3),
                    "記憶" or "情報" or "復習" or "稽古" or "練習" or "台詞" or "セリフ" or "楽譜" or "曲" or "資料" or "内容" => (1500810, (byte)2),
                    _ => (1593865, (byte)2),
                };
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, "さらう");
            }

            // Sudachi's tag settles bare や: 形状詞 is kana 嫌 (やだ), 助動詞 the Kansai copula (んやけど), never 矢.
            if (word.Text == "や" && word.PreMatchedWordId == null)
            {
                if (word.PartOfSpeech == PartOfSpeech.NaAdjective)
                {
                    word.PreMatchedWordId = 1587610;
                    word.PreMatchedReadingIndex = 3;
                }
                else if (word.PartOfSpeech == PartOfSpeech.Auxiliary)
                    word.PreMatchedWordId = 2028960;
            }

            // Kana し/した/して lemmatised as する is that verb, not 詩/舌; the conjunction して keeps its own lemma.
            if (word.Text is "し" or "した" or "して"
                && word.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Unknown
                && word.DictionaryForm is "する" or "為る" or "した" && word.PreMatchedWordId == null)
            {
                word.PreMatchedWordId = 1157170;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, "する");
            }

            // Clause-initial ようは is 要は "in short", not 様+は.
            if (word.Text == "よう" && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && i + 1 < wordInfos.Count && wordInfos[i + 1] is { Text: "は", PartOfSpeech: PartOfSpeech.Particle }
                && (i == 0 || wordInfos[i - 1].PartOfSpeech == PartOfSpeech.SupplementarySymbol))
            {
                AbsorbNext(wordInfos, i, "ヨウハ");
                word.DictionaryForm = "ようは";
                word.NormalizedForm = "ようは";
                word.PartOfSpeech = PartOfSpeech.Conjunction;
                word.PreMatchedWordId = 1914670;
            }

            // きった/きって after a 連用形 is the completion auxiliary 切る (落ちきった); Sudachi lemmatises it as 来る.
            if (word.Text is "きった" or "きって" && word.PartOfSpeech == PartOfSpeech.Verb
                && word.PreMatchedWordId == null
                && i > 0 && wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb
                && !wordInfos[i - 1].Text.EndsWith('て') && !wordInfos[i - 1].Text.EndsWith('で'))
                PinVerb(word, 1384830, "切る", word.Text, "きる");

            // くれ (or shouted くれえ) after を/て/で is くれる's imperative (許してくれえ), not 暮れ or くらい.
            if (word.Text is "くれ" or "くれえ" or "くれぇ" or "くれー" && i > 0
                && (wordInfos[i - 1].Text is "を" or "て" or "で"
                    || wordInfos[i - 1].Text.EndsWith("て", StringComparison.Ordinal)
                    || wordInfos[i - 1].Text.EndsWith("で", StringComparison.Ordinal))
                && word.PreMatchedWordId == null)
                PinVerb(word, 1269130, "くれる", "くれ");

            // Utterance-initial ねえ before a pause or name is "hey" (ねえ、悟); the negation ねえ follows content (金なんてねえ).
            if (word.Text == "ねえ" && word.PartOfSpeech == PartOfSpeech.Interjection
                && AtClauseStart(wordInfos, i)
                && (FollowedByPunctuationOrEnd(wordInfos, i)
                    || wordInfos[i + 1].PartOfSpeech is PartOfSpeech.Name or PartOfSpeech.Pronoun
                    || PosMapper.IsNameLikeSudachiNoun(wordInfos[i + 1].PartOfSpeech,
                        wordInfos[i + 1].PartOfSpeechSection1, wordInfos[i + 1].PartOfSpeechSection2,
                        wordInfos[i + 1].PartOfSpeechSection3))
                && word.PreMatchedWordId == null)
            {
                word.DictionaryForm = "ねえ";
                word.NormalizedForm = "ねえ";
                word.PreMatchedWordId = 2029080;
            }

            // Kana いった after a place/direction word is 行く "went" (どこいった), not 言う.
            if (word.Text == "いった" && i > 0
                && wordInfos[i - 1].Text is "どこ" or "どっか" or "こっち" or "あっち" or "そっち" or "どこか")
                PinVerb(word, 1578850, "行く", "いった", "いく");

            // 出られな|いって is ない + って: Sudachi lets いく steal ない's final mora.
            if (word.Text == "いって" && word.DictionaryForm is "いく" or "行く"
                && i > 0 && wordInfos[i - 1] is { Text: "な", PartOfSpeech: PartOfSpeech.Auxiliary } naAux)
            {
                naAux.Text = "ない";
                naAux.DictionaryForm = "ない";
                naAux.NormalizedForm = "ない";
                naAux.Reading = "ナイ";
                word.Text = "って";
                word.DictionaryForm = "って";
                word.NormalizedForm = "って";
                word.Reading = "ッテ";
                word.PartOfSpeech = PartOfSpeech.Particle;
                word.PreMatchedWordId = null;
            }

            // A single kanji clipped in an exclamation (痛ぇぇ！, 暗っ！) is the i-adjective, not the noun/suffix homograph.
            if (word.Text.Length == 1 && word.Text[0] is >= '一' and <= '鿿'
                && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Suffix
                && (i == 0 || wordInfos[i - 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol
                    or PartOfSpeech.Symbol or PartOfSpeech.Interjection or PartOfSpeech.BlankSpace)
                && NextNonSplitMarker(wordInfos, i) is var clipNext
                && clipNext < wordInfos.Count
                && wordInfos[clipNext].PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol
                && wordInfos[clipNext].Text.Length > 0
                && wordInfos[clipNext].Text[0] is 'ぇ' or 'ェ' or 'っ' or 'ッ' or 'ー'
                // Exclamations clip only common adjectives: 今っ！ is the noun 今, not the rare 今い.
                && GetNonNameCompoundWordId?.Invoke(word.Text + "い") is { } clippedAdjId
                && GetNonNameCompoundFrequencyRank?.Invoke(word.Text + "い") is { } clippedAdjRank
                && clippedAdjRank <= 5000)
            {
                word.PartOfSpeech = PartOfSpeech.IAdjective;
                word.DictionaryForm = word.Text + "い";
                word.NormalizedForm = word.Text + "い";
                word.PreMatchedWordId = clippedAdjId;
            }

            // 来 before やがる is the verb stem (来やがる stays split by convention), not the suffix らい.
            if (word is { Text: "来", PartOfSpeech: PartOfSpeech.Verb } && i + 1 < wordInfos.Count
                && wordInfos[i + 1].DictionaryForm == "やがる")
            {
                word.PreMatchedWordId = 1547720;
            }

            // Clause-final 行け is 行く's imperative (先に行け！), not the potential 行ける.
            if (word.Text is "行け" or "行けー" && FollowedByPunctuationOrEnd(wordInfos, i))
                PinVerb(word, 1578850, "行く", "行け");

            // Suffix 宛 is 宛て "addressed to".
            if (word is { Text: "宛", PartOfSpeech: PartOfSpeech.Suffix } && i > 0
                && word.PreMatchedWordId == null)
            {
                word.PreMatchedWordId = 1448820;
            }

            // ばっか right after a te-form or noun is ばかり (仕事ばっか), not 馬鹿 (あんたはばっか); pinned as 麦価/幕下 share the kana.
            if (word is { Text: "ばっか", PreMatchedWordId: null } && i > 0
                && ((wordInfos[i - 1] is { PartOfSpeech: PartOfSpeech.Verb } vprev
                     && (vprev.Text.EndsWith('て') || vprev.Text.EndsWith('で')))
                    || wordInfos[i - 1] is { PartOfSpeech: PartOfSpeech.Particle, Text: "て" or "で" }
                    || wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                        or PartOfSpeech.Pronoun or PartOfSpeech.Name))
            {
                word.PreMatchedWordId = 2857403;
                word.DictionaryForm = "ばっか";
                word.NormalizedForm = "ばっか";
                word.PartOfSpeech = PartOfSpeech.Particle;
            }

            // Single kanji listed with another (仁も義も智も) are concepts Sudachi name-tags (智→サトシ); a name partner (太郎と智) is not.
            if (word.PartOfSpeech is PartOfSpeech.Noun && word.Text.Length == 1
                && JapaneseTextHelper.IsKanji(word.Text[0]) && word.PreMatchedWordId == null
                && ((i >= 2
                     && wordInfos[i - 1] is { PartOfSpeech: PartOfSpeech.Particle, Text: "も" or "と" or "や" }
                     && wordInfos[i - 2].Text.Length == 1
                     && JapaneseTextHelper.IsKanji(wordInfos[i - 2].Text[0])
                     && wordInfos[i - 2].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
                    || (i + 2 < wordInfos.Count
                        && wordInfos[i + 1] is { PartOfSpeech: PartOfSpeech.Particle, Text: "も" or "と" or "や" }
                        && wordInfos[i + 2].Text.Length == 1
                        && JapaneseTextHelper.IsKanji(wordInfos[i + 2].Text[0])
                        && wordInfos[i + 2].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)))
                ClearPosSections(word);

            // A trailing kanji that is no word alone is a stolen compound tail (敵陣|営 → 敵|陣営); a standalone one stays (東京|都).
            if (word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && word.Text.Length == 2 && word.PreMatchedWordId == null
                && JapaneseTextHelper.IsKanji(word.Text[0]) && JapaneseTextHelper.IsKanji(word.Text[1])
                && i + 1 < wordInfos.Count && HasNonNameCompoundLookup != null)
            {
                var tail = wordInfos[i + 1];
                if (tail.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    && tail.Text.Length == 1 && JapaneseTextHelper.IsKanji(tail.Text[0])
                    && tail.PreMatchedWordId == null
                    && !HasNonNameCompoundLookup(tail.Text)
                    && HasNonNameCompoundLookup(word.Text[..1])
                    && HasNonNameCompoundLookup(word.Text[1..] + tail.Text))
                {
                    string newTail = word.Text[1..] + tail.Text;
                    tail.Text = newTail;
                    tail.DictionaryForm = newTail;
                    tail.NormalizedForm = newTail;
                    // The stolen cut's readings are wrong on both sides; empty lets JMDict matching supply them.
                    tail.Reading = "";
                    tail.PartOfSpeech = PartOfSpeech.Noun;
                    word.Text = word.Text[..1];
                    word.DictionaryForm = word.Text;
                    word.NormalizedForm = word.Text;
                    word.Reading = "";
                    if (word.EndOffset >= 0)
                    {
                        tail.StartOffset = word.EndOffset - 1;
                        word.EndOffset -= 1;
                    }
                }
            }

            // 方々 + を/に + movement verb is ほうぼう, after の/adnominal かたがた (その方々); Sudachi's reading is unreliable.
            if (word.Text == "方々")
            {
                bool movementFollows = i + 2 < wordInfos.Count && wordInfos[i + 1].Text is "を" or "に"
                    && (wordInfos[i + 2].DictionaryForm is "歩く" or "巡る" or "旅する" or "走る" or "駆ける"
                           or "散る" or "散らばる" or "逃げる" or "飛ぶ"
                        || wordInfos[i + 2].DictionaryForm.EndsWith("回る", StringComparison.Ordinal)
                        || wordInfos[i + 2].DictionaryForm.EndsWith("散る", StringComparison.Ordinal));
                if (movementFollows)
                    word.PreMatchedWordId = 1584105;
                else if (i > 0 && (wordInfos[i - 1].Text == "の"
                                   || wordInfos[i - 1].PartOfSpeech == PartOfSpeech.PrenounAdjectival))
                    word.PreMatchedWordId = 1584100;
            }

            // Clause-final だい (何がだい) is the question particle, not 代 (Sudachi tags it Prefix).
            if (word.Text == "だい" && word.PartOfSpeech == PartOfSpeech.Prefix
                && FollowedByPunctuationOrEnd(wordInfos, i))
            {
                word.PartOfSpeech = PartOfSpeech.Particle;
                word.DictionaryForm = "だい";
                word.PreMatchedWordId = 2097680;
            }

            // Kana reciprocal 〜あう (憎みあって) can't reach its 合う entry and drops; surfaces that resolve (つきあって) skip the pin.
            if (word.PartOfSpeech == PartOfSpeech.Verb && !word.Text.Contains('合')
                && !string.IsNullOrEmpty(word.NormalizedForm)
                && word.NormalizedForm.EndsWith("合う", StringComparison.Ordinal)
                && word.NormalizedForm.Length >= 3
                && !KanaSurfaceResolvesViaLookup(word)
                && GetNonNameCompoundWordId?.Invoke(word.NormalizedForm) is { } reciprocalAuId)
            {
                word.PreMatchedWordId = reciprocalAuId;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, word.DictionaryForm);
            }

            // あって after a verb 連用形 is reciprocal 合う (読みあって); ある follows a noun or idiom frame (実りあって, 命あっての).
            // Runs after the って re-cut; pinning earlier triggers the っ+て mora theft.
            if (word.PartOfSpeech == PartOfSpeech.Verb && word.NormalizedForm is "有る" or "在る"
                && word.Text.StartsWith("あっ", StringComparison.Ordinal)
                && !(i + 1 < wordInfos.Count && wordInfos[i + 1].Text is "の" or "も" or "こそ")
                && i > 0 && wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb
                && RenyokeiSurfaceToVerb(wordInfos[i - 1].Text) != null)
            {
                word.DictionaryForm = "あう";
                word.NormalizedForm = "合う";
                word.PreMatchedWordId = 1284430;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, word.DictionaryForm);
            }

            // CombineInflections glues quotative って onto i-adjectives (硬いって); only 〜くって (嬉しくって) is a real te-form.
            if (word.PartOfSpeech == PartOfSpeech.IAdjective && word.Text.Length >= 4
                && word.Text.EndsWith("って", StringComparison.Ordinal)
                && word.Text[^3] != 'く'
                && HasNonNameCompoundLookup?.Invoke(word.Text[..^2]) == true)
            {
                int mid = word.EndOffset >= 0 ? word.EndOffset - 2 : -1;
                var tte = new WordInfo(word)
                {
                    Text = "って",
                    DictionaryForm = "って",
                    NormalizedForm = "って",
                    Reading = "ッテ",
                    PartOfSpeech = PartOfSpeech.Particle,
                    PreMatchedWordId = 2086960,
                    StartOffset = mid,
                    EndOffset = word.EndOffset
                };
                word.Text = word.Text[..^2];
                word.EndOffset = mid;
                if (word.Reading.EndsWith("ッテ", StringComparison.Ordinal))
                    word.Reading = word.Reading[..^2];
                wordInfos.Insert(i + 1, tte);
            }

            // もんか after a predicate is rhetorical "as if" (あるもんか), not 門下 (剣の門下); ものか stays ambiguous (どうしたものか).
            bool prevIsPredicate = i > 0
                && wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary;
            if (prevIsPredicate && word.Text == "もん"
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "か")
                AbsorbNext(wordInfos, i, "モンカ");
            if (prevIsPredicate && word.Text == "もんか")
            {
                word.PartOfSpeech = PartOfSpeech.Particle;
                word.DictionaryForm = "ものか";
                word.NormalizedForm = "ものか";
                word.PreMatchedWordId = 2130440;
            }

            if (word.Text == "おい")
                word.PartOfSpeech = PartOfSpeech.Interjection;

            ApplyCounterPins(wordInfos, i);

            // 家 before a case particle is the noun いえ, not the suffix け.
            if (word is { Text: "家", PartOfSpeech: PartOfSpeech.Suffix } &&
                i + 1 < wordInfos.Count &&
                wordInfos[i + 1] is { PartOfSpeech: PartOfSpeech.Particle, Text: "から" or "を" or "が" or "に" or "で" or "へ" or "の" or "は" or "も" })
                word.PartOfSpeech = PartOfSpeech.Noun;

            if (word is { Text: "山", PartOfSpeech: PartOfSpeech.Suffix })
                word.PartOfSpeech = PartOfSpeech.Noun;

            if (word is { Text: "だろう" or "だろ", PartOfSpeech: PartOfSpeech.Auxiliary })
            {
                word.PartOfSpeech = PartOfSpeech.Expression;
                word.DictionaryForm = word.Text;
            }

            if (word.Text == "だあ")
            {
                word.Text = "だ";
                word.DictionaryForm = "です";
                word.Reading = "ダ";
                word.PartOfSpeech = PartOfSpeech.Auxiliary;
            }
            else if (word.Text == "だー")
            {
                word.DictionaryForm = "です";
                word.PartOfSpeech = PartOfSpeech.Auxiliary;
            }

            // したり after を/が is する + ～たり (キスをしたり), not the interjection; an interjection never follows a case particle.
            if (word.Text == "したり" && i > 0 && wordInfos[i - 1].Text is "を" or "が")
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "する";
                word.NormalizedForm = "為る";
                word.PreMatchedWordId = 1157170;
                word.PreMatchedReadingIndex = 1;
                word.PreMatchedConjugations = ["tari", "(unstressed infinitive)"];
            }
        }

        return wordInfos;
    }

    private string? TryGetRenyoukeiVerb(string stem)
    {
        if (stem.Length == 0) return null;
        foreach (var f in PipelineCachedDeconjugate(NormalizeToHiragana(stem)))
            if (f.Tags.Contains("stem-ren") && f.Tags.Any(t => t.StartsWith('v'))
                && HasVerbOrAdjectiveLookup?.Invoke(f.Text) == true)
                return f.Text;
        return null;
    }

    private void PinVerb(WordInfo word, int wordId, string lemma, string conjugationSurface, string? conjugationBase = null)
    {
        word.PartOfSpeech = PartOfSpeech.Verb;
        word.DictionaryForm = lemma;
        word.NormalizedForm = lemma;
        word.PreMatchedWordId = wordId;
        word.PreMatchedConjugations = PinnedConjugationProcess(conjugationSurface, conjugationBase ?? lemma);
    }

    private static void AbsorbNext(List<WordInfo> wordInfos, int i, string reading)
    {
        var word = wordInfos[i];
        var next = wordInfos[i + 1];
        word.Text += next.Text;
        word.Reading = reading;
        word.EndOffset = next.EndOffset;
        wordInfos.RemoveAt(i + 1);
    }

    private static void ClearPosSections(WordInfo word)
    {
        word.PartOfSpeechSection1 = PartOfSpeechSection.None;
        word.PartOfSpeechSection2 = PartOfSpeechSection.None;
        word.PartOfSpeechSection3 = PartOfSpeechSection.None;
    }

    private static bool AtClauseStart(List<WordInfo> wordInfos, int i) =>
        IsClauseBoundary(i > 0 ? wordInfos[i - 1] : null);

    // Unlike IsClauseBoundary, a following space does not end the clause here.
    private static bool FollowedByPunctuationOrEnd(List<WordInfo> wordInfos, int i) =>
        i + 1 >= wordInfos.Count
        || wordInfos[i + 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol;

    private static int NextNonSplitMarker(List<WordInfo> wordInfos, int i)
    {
        int j = i + 1;
        while (j < wordInfos.Count && wordInfos[j].Text is "|" or "")
            j++;
        return j;
    }

    // A surface that reaches a lookup entry resolves through normal scoring; a pin would only degrade it.
    private bool KanaSurfaceResolvesViaLookup(WordInfo word)
    {
        if (HasNonNameCompoundLookup == null)
            return false;
        if (!string.IsNullOrEmpty(word.DictionaryForm) && HasNonNameCompoundLookup(word.DictionaryForm))
            return true;

        var hira = NormalizeToHiragana(word.Text);
        if (HasNonNameCompoundLookup(hira))
            return true;
        foreach (var form in PipelineCachedDeconjugate(hira))
        {
            if (form.Text.Length >= 3 && HasNonNameCompoundLookup(form.Text))
                return true;
        }

        return false;
    }

    // A PreMatchedWordId pin skips deconjugation, so without this chain every pin emits a bare lemma.
    private List<string>? PinnedConjugationProcess(string surface, string dictionaryForm)
    {
        var hiraSurface = NormalizeToHiragana(surface);
        var hiraDict = NormalizeToHiragana(dictionaryForm);
        if (hiraSurface == hiraDict)
            return null;

        foreach (var form in PipelineCachedDeconjugate(hiraSurface))
        {
            if (form.Text == hiraDict)
                return form.Process.ToList();
        }

        return null;
    }

    /// <summary>Fixes Sudachi readings of kanji homographs from context (directional 表へ → おもて, not ひょう).</summary>
    private List<WordInfo> FixReadingAmbiguity(List<WordInfo> wordInfos)
    {
        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (word is { Text: "何", Reading: "ナン" })
            {
                var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                if (next == null || next.Text is "を" or "が" or "も")
                    word.Reading = "ナニ";
            }

            // Suffix ばり binds to an adjacent nominal (漱石張り); after a particle or clause start it is はり (声に張りがある).
            if (word is { Text: "張り", Reading: "バリ" })
            {
                // A quoted host still binds (「漱石」張り), so skip closing brackets and inserted blanks, not punctuation.
                int h = i - 1;
                while (h >= 0 && (wordInfos[h].PartOfSpeech == PartOfSpeech.BlankSpace
                                  || (wordInfos[h].Text.Length == 1 && "」』）】》〉".Contains(wordInfos[h].Text[0]))))
                    h--;
                var prevHost = h >= 0 ? wordInfos[h] : null;
                if (prevHost == null
                    || prevHost.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                        or PartOfSpeech.Name or PartOfSpeech.Pronoun or PartOfSpeech.Numeral
                        or PartOfSpeech.Suffix))
                    word.Reading = "ハリ";
            }

            // 寒気 is サムケ "chills" when felt (寒気がする, 寒気を覚える); 寒気が南下する stays カンキ "cold air".
            if (word is { Text: "寒気", Reading: "カンキ" } &&
                i + 2 < wordInfos.Count &&
                ((wordInfos[i + 1].Text == "が" && wordInfos[i + 2].DictionaryForm == "する")
                 || (wordInfos[i + 1].Text == "を"
                     && wordInfos[i + 2].DictionaryForm is "覚える" or "感じる" or "催す")))
            {
                word.Reading = "サムケ";
            }

            FixNumeralReadings(wordInfos, ref i);

            // 弾く is はじく (グラスを弾いた) unless a music word is near (ピアノが弾かれた); ヒケ* is left to the 弾ける block.
            if (word.DictionaryForm == "弾く"
                && word.Reading.StartsWith("ヒ", StringComparison.Ordinal)
                && !word.Reading.StartsWith("ヒケ", StringComparison.Ordinal))
            {
                bool musicContext = false;
                for (int k = Math.Max(0, i - 6); k < Math.Min(wordInfos.Count, i + 4) && !musicContext; k++)
                    musicContext = wordInfos[k].Text is "ピアノ" or "ギター" or "エレキギター" or "バイオリン"
                        or "ヴァイオリン" or "曲" or "演奏" or "コンサート" or "音楽" or "弦" or "楽器" or "バンド";
                if (!musicContext)
                    word.Reading = "ハジ" + word.Reading[1..];
            }

            // Sudachi reads every 素振り スブリ; "practice swing" needs a swing word nearby, else the scorer keeps そぶり.
            if (word is { Text: "素振り", Reading: "スブリ" })
            {
                bool swingContext = false;
                for (int k = Math.Max(0, i - 6); k < Math.Min(wordInfos.Count, i + 7) && !swingContext; k++)
                {
                    var t = wordInfos[k].Text;
                    swingContext = t is "竹刀" or "木刀" or "刀" or "剣" or "剣道" or "野球" or "打席" or "練習"
                                       or "稽古" or "部活" or "ゴルフ" or "テニス"
                                   || t.Contains("バット", StringComparison.Ordinal)
                                   || t.Contains("バッター", StringComparison.Ordinal)
                                   || t.Contains("ラケット", StringComparison.Ordinal)
                                   || t.Contains("スイング", StringComparison.Ordinal);
                }

                if (swingContext)
                    word.PreMatchedWordId = 1749550;
            }

            // Sudachi drops っ from some lemmas (かっこつけ→かこつける); onbin chains (言って→言う) keep their lemma.
            if (word.PartOfSpeech == PartOfSpeech.Verb && word.PreMatchedWordId == null
                && word.Text.Contains('っ')
                && word.DictionaryForm is { Length: > 0 } tsuDf && !tsuDf.Contains('っ'))
            {
                var tsuForms = PipelineCachedDeconjugate(word.Text);
                var currentLemma = word.DictionaryForm;
                var tsuBetter = tsuForms.Any(f => f.Text == currentLemma)
                    ? null
                    : tsuForms.FirstOrDefault(f => f.Text.Contains('っ')
                        && f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal))
                        && HasNonNameCompoundLookup?.Invoke(f.Text) == true);
                if (tsuBetter != null)
                {
                    word.DictionaryForm = tsuBetter.Text;
                    word.NormalizedForm = tsuBetter.Text;
                }
            }

            // 札 is ふだ only with a game word AND a card verb; money changes hands in game scenes (麻雀に負けて札を渡した).
            if (word is { Text: "札", Reading: "サツ" })
            {
                bool cardVerb = i + 2 < wordInfos.Count && wordInfos[i + 1].Text == "を"
                    && wordInfos[i + 2].DictionaryForm is "出す" or "出せる" or "引く" or "切る" or "並べる" or "配る" or "見る";
                bool gameContext = false;
                for (int k = Math.Max(0, i - 30); k < Math.Min(wordInfos.Count, i + 5) && !gameContext; k++)
                    gameContext = wordInfos[k].Text is "麻雀" or "トランプ" or "カード" or "手札" or "切り札" or "カルタ";
                if (cardVerb && gameContext)
                    word.Reading = "フダ";
            }

            // Sudachi lemmatises 弄った to archaic 弄う (いらう); modern usage is 弄る (いじる).
            if (word.DictionaryForm == "弄う")
            {
                word.DictionaryForm = "弄る";
                word.NormalizedForm = "弄る";
                word.Reading = word.Reading!.Replace("イラ", "イジ");
            }

            // Sudachi always reads 角 カド; context picks つの (鬼の角, 角が生える) or かく (三角形の角, 角が90度).
            if (word is { Text: "角", Reading: "カド" })
            {
                var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                var prev = i > 0 ? wordInfos[i - 1] : null;
                var next2 = i + 2 < wordInfos.Count ? wordInfos[i + 2] : null;
                var prev2 = i >= 2 ? wordInfos[i - 2] : null;

                bool isHornVerb = next is { Text: "が" or "を" } && next2 != null &&
                                  next2.DictionaryForm is "生える" or "生やす" or "折れる" or "折る"
                                      or "研ぐ" or "磨く";

                bool afterCreature = prev is { Text: "の" } && prev2 != null &&
                                     IsHornBearerWord(prev2.Text);

                bool afterHead = prev is { Text: "に" or "の" } && prev2 != null &&
                                 prev2.Text is "頭" or "額" or "おでこ";

                bool afterGeometry = prev is { Text: "の" } && prev2 != null &&
                                     (prev2.Text.EndsWith("角形", StringComparison.Ordinal) || prev2.Text.EndsWith("多角", StringComparison.Ordinal));

                var next3 = i + 3 < wordInfos.Count ? wordInfos[i + 3] : null;
                bool beforeDegree = next is { Text: "が" or "は" or "も" } && next2 != null &&
                                    (next2.Text.Contains('度') || next2.DictionaryForm is "等しい"
                                     || (IsNumeralToken(next2) && next3 is { Text: "度" }));

                if (isHornVerb || afterCreature || afterHead)
                    word.Reading = "ツノ";
                else if (afterGeometry || beforeDegree)
                    word.Reading = "カク";
            }

            // 額 is ヒタイ in body-contact context (額にキスをした, 額の傷).
            if (word is { Text: "額", Reading: "ガク" })
            {
                var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                var next2 = i + 2 < wordInfos.Count ? wordInfos[i + 2] : null;

                bool isBodyContext = next is { Text: "に" or "を" or "の" } && next2 != null &&
                    (next2.DictionaryForm is "キス" or "触れる" or "当てる" or "押す" or "押さえる"
                         or "叩く" or "撫でる" or "拭く"
                     || next2.Text is "手" or "キス" or "汗" or "傷" or "皺" or "シワ");

                if (isBodyContext)
                    word.Reading = "ヒタイ";
            }

            // いだく is literary; modern standalone 抱く is overwhelmingly だく.
            if (word is { DictionaryForm: "抱く", Reading: { } r } && r.StartsWith("イダ", StringComparison.Ordinal))
                word.Reading = r.Replace("イダ", "ダ");

            // Kana よう as 形状詞/助動詞語幹 is 様 "manner", not 陽 "positive".
            if (word is { Text: "よう", Reading: "ヨウ", DictionaryForm: "よう" })
                word.PreMatchedWordId = 1605840;

            // ジ only occurs inside kango compounds (仕事); an orphaned 事 is the nominaliser こと.
            if (word is { Text: "事", Reading: "ジ", WasReclassifiedFromSuffix: true })
                word.Reading = "コト";

            // たった after a time unit (三年たった) is 経つ, not 断つ/立つ.
            if (word is { Text: "たった", PartOfSpeech: PartOfSpeech.Verb or PartOfSpeech.Auxiliary or PartOfSpeech.Unknown })
            {
                var prev = i > 0 ? wordInfos[i - 1] : null;
                if (prev != null && prev.PartOfSpeech != PartOfSpeech.SupplementarySymbol)
                {
                    bool precedingIsTimeUnit = prev.Text.EndsWith('年') || prev.Text.EndsWith('月')
                                              || prev.Text.EndsWith('日') || prev.Text.EndsWith('週')
                                              || prev.Text.EndsWith('間');
                    if (precedingIsTimeUnit)
                    {
                        word.PreMatchedWordId = 1251100;
                        word.DictionaryForm = "たつ";
                        word.PreMatchedConjugations = ["past"];
                    }
                }
            }

            // A kanji noun directly before a verb is an ichidan 連用中止 stem (体を支え立ち上がった: 支える, not 支え).
            if (word.PartOfSpeech == PartOfSpeech.Noun
                && word.DictionaryForm == word.Text
                && word.PreMatchedWordId == null
                && word.Text.Length >= 2
                && KanaScoringHelpers.ContainsKanji(word.Text)
                && IsIchidanStemEnding(word.Reading)
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].PartOfSpeech == PartOfSpeech.Verb
                // A suru-noun before できる is the potential pattern (真似できない), not a verb stem.
                && !(wordInfos[i + 1].DictionaryForm is "できる" or "出来る"
                     && HasSuruVerbCompoundLookup?.Invoke(word.Text) == true))
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = word.Text + "る";
            }

            // 露 before になる is あらわ "exposed" (服が露になった); Sudachi's ロ reading drags scoring to dew/Russia.
            if (word is { Text: "露", PartOfSpeech: PartOfSpeech.Noun }
                && i + 2 < wordInfos.Count
                && wordInfos[i + 1].Text == "に"
                && wordInfos[i + 2].DictionaryForm is "なる" or "成る")
            {
                word.Reading = "アラワ";
                word.NormalizedForm = "露わ";
            }

            // Clause-final あり after に/と is classical ある (貴女と共にあり――); the ant 蟻 takes が/は/を.
            if (word is { Text: "あり", PartOfSpeech: PartOfSpeech.Noun }
                && i > 0 && (wordInfos[i - 1].Text.EndsWith('に') || wordInfos[i - 1].Text.EndsWith('と'))
                && IsClauseBoundary(i + 1 < wordInfos.Count ? wordInfos[i + 1] : null))
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "ある";
            }

            // Standalone 捩る is ねじる "twist"; もじる "parody" is rare and usually kana.
            if (word.DictionaryForm == "捩る" && word.Reading.StartsWith("モジ", StringComparison.Ordinal))
                word.Reading = word.Reading.Replace("モジ", "ネジ");

            // 大勢 is オオゼイ "many people"; タイセイ "trend" only in set phrases (大勢に影響がない).
            if (word is { Text: "大勢", Reading: "タイセイ" })
            {
                var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                var next2 = i + 2 < wordInfos.Count ? wordInfos[i + 2] : null;
                bool isTaiseiContext = next is { Text: "に" } && next2 is { DictionaryForm: "影響" };
                if (!isTaiseiContext)
                    word.Reading = "オオゼイ";
            }

            // Sudachi maps katakana イキ to 生きる, which is never written イキ; it is slang 行く (イキました).
            if (word.DictionaryForm == "イキる" && word.Text.StartsWith("イキ", StringComparison.Ordinal))
            {
                word.DictionaryForm = "行く";
                word.NormalizedForm = "行く";
            }

            // Kana いける is 行ける "go well" unless a flower object precedes (花をいける → 生ける).
            if (word is { Text: "いける", DictionaryForm: "いける" })
            {
                var prevTok = i > 0 ? wordInfos[i - 1] : null;
                var prev2Tok = i >= 2 ? wordInfos[i - 2] : null;
                bool flowerContext =
                    (prevTok != null && (prevTok.Text.Contains('花') || prevTok.Text.Contains('華')))
                    || (prev2Tok != null && (prev2Tok.Text.Contains('花') || prev2Tok.Text.Contains('華')));
                // DictionaryForm splits the context-blind DeckWord cache key; else the first sense parsed wins every いける.
                word.PreMatchedWordId = flowerContext ? 1587190 : 1631370;
                word.DictionaryForm = flowerContext ? "生ける" : "行ける";
            }

            // Katakana ツイてる is 付いてる "lucky"; ついて "about" is never written in katakana.
            if (word.Text.StartsWith("ツイて", StringComparison.Ordinal))
            {
                word.PreMatchedWordId = 1894260;
                word.DictionaryForm = "ツイてる";
                // The pin skips deconjugation; without an explicit chain ツイてた renders as the bare lemma.
                word.PreMatchedConjugations = word.Text["ツイて".Length..] switch
                {
                    "なかった" => ["negative", "past"],
                    "ない" => ["negative"],
                    "た" => ["past"],
                    _ => word.PreMatchedConjugations
                };
            }

            // Sudachi lemmatises 弾く's potential as 弾ける too; reading ヒケ* marks the potential.
            if (word.DictionaryForm == "弾ける" && word.Reading.StartsWith("ヒケ", StringComparison.Ordinal))
            {
                word.DictionaryForm = "弾く";
                word.NormalizedForm = "弾く";
            }

            // A キタ reading on 来 (来た, or Sudachi's 文語 来たる) makes ReadingMatchScore pick きたる; 来り is genuinely きたる.
            if (word.NormalizedForm == "来たる" && word.Text is "来る" or "来")
            {
                word.Reading = word.Reading.Replace("キタ", "ク");
                word.NormalizedForm = "来る";
                word.DictionaryForm = "来る";
            }
            else if (word.Text.Length >= 2 && word.Text[0] == '来' && word.Text[1] != 'り'
                     && word.DictionaryForm == "来る" && word.Reading.StartsWith("キタ", StringComparison.Ordinal))
            {
                word.Reading = "ク" + word.Reading[2..];
            }

            // Clause-initial よって、 is "therefore"; 場合によって keeps the verb. DictionaryForm splits the context-blind cache key.
            if (word is { Text: "よって" } && word.DictionaryForm is "依る" or "因る" or "よる"
                && AtClauseStart(wordInfos, i)
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text is "、" or "，")
            {
                word.PreMatchedWordId = 1605970;
                word.DictionaryForm = "よって";
            }
        }

        return wordInfos;
    }

    private static bool IsIchidanStemEnding(string reading)
    {
        if (string.IsNullOrEmpty(reading)) return false;
        char last = reading[^1];
        return last is 'エ' or 'ケ' or 'セ' or 'テ' or 'ネ' or 'ベ' or 'メ' or 'レ' or 'ゲ' or 'ペ' or 'ヘ' or 'ゼ'
            or 'イ' or 'キ' or 'シ' or 'チ' or 'ニ' or 'ビ' or 'ミ' or 'リ' or 'ギ' or 'ピ' or 'ヒ' or 'ジ';
    }

    private static bool IsHornBearerWord(string text) => text is
        "鬼" or "牛" or "鹿" or "羊" or "山羊" or "馬" or "竜" or "龍"
        or "悪魔" or "怪物" or "獣" or "魔物" or "魔族" or "動物"
        or "トナカイ" or "ドラゴン" or "モンスター" or "ユニコーン" or "サイ"
        or "カブトムシ" or "クワガタ" or "虫" or "デーモン";
}
