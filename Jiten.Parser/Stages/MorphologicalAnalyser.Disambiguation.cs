using Jiten.Core.Data;
using Jiten.Parser.Scoring;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    private List<WordInfo> FilterMisparse(List<WordInfo> wordInfos)
    {
        for (int i = wordInfos.Count - 1; i >= 0; i--)
        {
            var word = wordInfos[i];
            if (word.Text is "なん" or "フン" or "ふん")
                word.PartOfSpeech = PartOfSpeech.Prefix;

            if (word.Text == "そう")
                word.PartOfSpeech = PartOfSpeech.Adverb;

            // なんなん is overwhelmingly the colloquial "what the hell?" exp (2871194), not the rare
            // 喃々 "chatteringly" taru-adverb (2840433), which appears as 喃々と. Remap unless followed by と.
            if (word.Text == "なんなん" && !(i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "と"))
            {
                word.PartOfSpeech = PartOfSpeech.Expression;
                word.DictionaryForm = "なんなん";
                word.PreMatchedWordId = 2871194;
            }

            // Katakana クズ is overwhelmingly 屑 "scum/trash" (1246510), not 葛 "arrowroot" (1208770);
            // the kanji-frequency prior otherwise flips this kana surface to 葛.
            if (word.Text == "クズ" && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
                word.PreMatchedWordId = 1246510;

            // Reduplicated あるある as a single token is the colloquial "I can relate / that's so true"
            // expression (2150380), not the existence verb ある (1296400) it otherwise resolves to as
            // a doubled stem. (Two separate ある tokens never reach here as one あるある surface.)
            if (word.Text is "あるある" or "アルアル")
            {
                word.PartOfSpeech = PartOfSpeech.Interjection;
                word.DictionaryForm = "あるある";
                word.NormalizedForm = "あるある";
                word.PreMatchedWordId = 2150380;
            }

            // Katakana ツバ is overwhelmingly 唾 "saliva" (1408410, ツバを飲む = to swallow saliva),
            // not 鍔 "sword guard / hat brim" (1433790) that the kanji-frequency prior otherwise picks.
            // In an explicit sword context (刀/剣/太刀/刃 + の + ツバ) or near 帽子 (帽子のツバ, ツバの広い帽子)
            // pin 鍔 instead — both directions are pinned because the word cache keys on (surface, POS,
            // dict form) without context, so an unpinned branch would inherit whichever direction was
            // cached first.
            if (word.Text == "ツバ" && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
            {
                bool swordContext = i >= 2 && wordInfos[i - 1].Text == "の"
                    && wordInfos[i - 2].Text is "刀" or "剣" or "太刀" or "刃";
                bool hatContext = false;
                for (int k = Math.Max(0, i - 2); k < Math.Min(wordInfos.Count, i + 5) && !hatContext; k++)
                    hatContext = wordInfos[k].Text.Contains("帽子", StringComparison.Ordinal);
                word.PreMatchedWordId = swordContext || hatContext ? 1433790 : 1408410;
            }

            // 事 that Sudachi reads ゴト after a noun or verb stem is the suffix ごと "matter of"
            // (2613010, お祝い事/頼まれ事), not the standalone noun こと (1313580) — whose JMDict priority
            // otherwise outweighs the reading evidence via the suffix-vs-noun POS penalty.
            if (word is { Text: "事", Reading: "ゴト" } && i > 0
                && wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Verb)
                word.PreMatchedWordId = 2613010;

            // Katakana ナシ in casual writing is the negation なし (1529560), not the pear 梨 the
            // kana surface otherwise matches; the fruit is written 梨. Sudachi tags the negation
            // use as a noun suffix (記憶ナシ). Coordination with と (リンゴとナシ) is produce-list
            // territory — leave the pear.
            if (word.Text == "ナシ" && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    or PartOfSpeech.NounSuffix or PartOfSpeech.Suffix
                && !(i > 0 && wordInfos[i - 1] is { Text: "と", PartOfSpeech: PartOfSpeech.Particle }))
            {
                word.DictionaryForm = "なし";
                word.NormalizedForm = "なし";
                word.PreMatchedWordId = 1529560;
            }

            // カンパン is the food 乾パン (1209690); the homographs 肝斑/甲板/乾板 are technical terms
            // spelled in kanji.
            if (word.Text == "カンパン" && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
            {
                word.DictionaryForm = "乾パン";
                word.NormalizedForm = "乾パン";
                word.PreMatchedWordId = 1209690;
            }

            // A bare kana し/した that Sudachi lemmatises as する is that verb; the kana surface must
            // not fall to a noun homograph (詩/舌) through surface-match priority. Inflection merges
            // can leave the token with a surface dictionary form and Unknown POS — the kanji homographs
            // always carry their own dictionary form, so those stay untouched.
            if (word.Text is "し" or "した"
                && word.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Unknown
                && word.DictionaryForm is "する" or "為る" or "した" && word.PreMatchedWordId == null)
            {
                word.PreMatchedWordId = 1157170;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, "する");
            }

            // Kana した as a noun after genitive の is the locational 下 (机のした), never the
            // homograph 舌 — the body part is written in kanji, or as べろ when in kana. The verbal
            // した after の (彼のしたこと) arrives with verb POS and is handled above.
            if (word.Text == "した"
                && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && word.PreMatchedWordId == null
                && i > 0 && wordInfos[i - 1] is { Text: "の", PartOfSpeech: PartOfSpeech.Particle })
            {
                word.DictionaryForm = "下";
                word.NormalizedForm = "下";
                word.PreMatchedWordId = 1184140;
            }

            // Clause-initial ようは is the discourse marker 要は "in short" (1914670), not 様+は;
            // merge the topic particle in so the expression entry resolves.
            if (word.Text == "よう" && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && i + 1 < wordInfos.Count && wordInfos[i + 1] is { Text: "は", PartOfSpeech: PartOfSpeech.Particle }
                && (i == 0 || wordInfos[i - 1].PartOfSpeech == PartOfSpeech.SupplementarySymbol))
            {
                word.Text = "ようは";
                word.DictionaryForm = "ようは";
                word.NormalizedForm = "ようは";
                word.PartOfSpeech = PartOfSpeech.Conjunction;
                word.EndOffset = wordInfos[i + 1].EndOffset;
                word.PreMatchedWordId = 1914670;
                wordInfos.RemoveAt(i + 1);
            }

            // Hiragana きった/きって after a 連用形 is the completion auxiliary 切る (落ちきった);
            // Sudachi lemmatises the stranded きっ as 来る.
            if (word.Text is "きった" or "きって" && word.PartOfSpeech == PartOfSpeech.Verb
                && word.PreMatchedWordId == null
                && i > 0 && wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb
                && !wordInfos[i - 1].Text.EndsWith('て') && !wordInfos[i - 1].Text.EndsWith('で'))
            {
                word.DictionaryForm = "切る";
                word.NormalizedForm = "切る";
                word.PreMatchedWordId = 1384830;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, "きる");
            }

            // Bare くれ after を/て/で — including its shouted stretches くれえ/くれぇ/くれー
            // (許してくれえ; longer runs are collapsed by elongation preprocessing first) — is the
            // imperative of くれる "give me" (1269130), not the nightfall noun 暮れ or the
            // approximation suffix くらい.
            if (word.Text is "くれ" or "くれえ" or "くれぇ" or "くれー" && i > 0
                && (wordInfos[i - 1].Text is "を" or "て" or "で"
                    || wordInfos[i - 1].Text.EndsWith("て", StringComparison.Ordinal)
                    || wordInfos[i - 1].Text.EndsWith("で", StringComparison.Ordinal))
                && word.PreMatchedWordId == null)
            {
                word.DictionaryForm = "くれる";
                word.NormalizedForm = "くれる";
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.PreMatchedWordId = 1269130;
                word.PreMatchedConjugations = PinnedConjugationProcess("くれ", "くれる");
            }

            // 何かって is 何か + quotative って, not the adverb かつて "formerly".
            if (word.Text == "かって" && i > 0 && wordInfos[i - 1].Text is "何" or "誰" or "だれ" or "なん")
            {
                int kMid = word.EndOffset >= 0 ? word.EndOffset - 2 : -1;
                word.Text = "か";
                word.DictionaryForm = "か";
                word.NormalizedForm = "か";
                word.PartOfSpeech = PartOfSpeech.Particle;
                word.PreMatchedWordId = null;
                var tte = new WordInfo(word)
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って",
                    Reading = "ッテ", PartOfSpeech = PartOfSpeech.Particle,
                    StartOffset = kMid
                };
                word.EndOffset = kMid;
                wordInfos.Insert(i + 1, tte);
            }

            // Elongated だろー is だろう (1928670); without a pin, the matcher resolves the stripped
            // だろ surface to the literary auxiliary たる, whose paradigm also contains だろ.
            if (word.Text is "だろー" or "だろぉ" or "だろぉー")
            {
                word.DictionaryForm = "だろう";
                word.NormalizedForm = "だろう";
                word.PreMatchedWordId = 1928670;
            }

            // つー before a nominaliser/question is the という contraction (つーのは, つーか), not two.
            if (word.Text == "つー" && i + 1 < wordInfos.Count
                && wordInfos[i + 1].Text is "の" or "か" or "こと" or "わけ")
            {
                word.DictionaryForm = "という";
                word.NormalizedForm = "という";
                word.PartOfSpeech = PartOfSpeech.Particle;
                word.PreMatchedWordId = 1922760;
            }

            // ならば as one token is the conditional conjunction (1009470), not a form of なる
            // (the merged なら+ば carries だ as its dictionary form).
            if (word.Text == "ならば" && word.DictionaryForm is "なる" or "成る" or "ならば" or "だ")
            {
                word.DictionaryForm = "ならば";
                word.NormalizedForm = "ならば";
                word.PartOfSpeech = PartOfSpeech.Conjunction;
                word.PreMatchedWordId = 1009470;
            }

            // Kana からだ directly after a predicate is から + だ "because it is", not the body 体
            // (which is written in kanji in that position).
            if (word.Text == "からだ" && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && i > 0 && wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective
                    or PartOfSpeech.Auxiliary)
            {
                int kdMid = word.EndOffset >= 0 ? word.EndOffset - 1 : -1;
                word.Text = "から";
                word.DictionaryForm = "から";
                word.NormalizedForm = "から";
                word.PartOfSpeech = PartOfSpeech.Particle;
                word.PreMatchedWordId = null;
                var da = new WordInfo(word)
                {
                    Text = "だ", DictionaryForm = "だ", NormalizedForm = "だ",
                    Reading = "ダ", PartOfSpeech = PartOfSpeech.Auxiliary,
                    StartOffset = kdMid
                };
                word.EndOffset = kdMid;
                wordInfos.Insert(i + 1, da);
            }

            // Utterance-initial ねえ followed by a pause or a vocative name is "hey" (2029080), not
            // the dialectal negation ない — the negation is a predicate and comes after content
            // (金なんてねえ), while the attention-getter opens the utterance (ねえ、/ねえ？/ねえ悟).
            if (word.Text == "ねえ" && word.PartOfSpeech == PartOfSpeech.Interjection
                && (i == 0 || wordInfos[i - 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol
                    or PartOfSpeech.Symbol or PartOfSpeech.BlankSpace)
                && (i + 1 >= wordInfos.Count
                    || wordInfos[i + 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol
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
            {
                word.DictionaryForm = "行く";
                word.NormalizedForm = "行く";
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.PreMatchedWordId = 1578850;
                word.PreMatchedConjugations = PinnedConjugationProcess("いった", "いく");
            }

            // The negative-quotative cut 出られな|いって is ない + って: Sudachi lets いく steal
            // ない's final mora. Restore ない and turn the remainder back into the quotative particle.
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

            // A single kanji that spells an i-adjective stem, clipped in an exclamation
            // (痛ぇぇ！, 暗っ！, 寒っ！), is that adjective — not a noun/suffix homograph
            // (-algia 痛, darkness 暗). Forced-split markers (|) are transparent.
            int clipNext = i + 1;
            while (clipNext < wordInfos.Count && wordInfos[clipNext].Text is "|" or "")
                clipNext++;
            if (word.Text.Length == 1 && word.Text[0] is >= '一' and <= '鿿'
                && word.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Suffix
                && (i == 0 || wordInfos[i - 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol
                    or PartOfSpeech.Symbol or PartOfSpeech.Interjection or PartOfSpeech.BlankSpace)
                && clipNext < wordInfos.Count
                && wordInfos[clipNext].PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol
                && wordInfos[clipNext].Text.Length > 0
                && wordInfos[clipNext].Text[0] is 'ぇ' or 'ェ' or 'っ' or 'ッ' or 'ー'
                // The adjective must be a genuinely common word: exclamations clip everyday
                // adjectives (痛い, 暗い, 寒い), never dictionary rarities — 今っ！ is the noun 今
                // clipped, not the obscure 今い "dated".
                && GetNonNameCompoundWordId?.Invoke(word.Text + "い") is { } clippedAdjId
                && GetNonNameCompoundFrequencyRank?.Invoke(word.Text + "い") is { } clippedAdjRank
                && clippedAdjRank <= 5000)
            {
                word.PartOfSpeech = PartOfSpeech.IAdjective;
                word.DictionaryForm = word.Text + "い";
                word.NormalizedForm = word.Text + "い";
                word.PreMatchedWordId = clippedAdjId;
            }

            // 飛んだ after a case particle is 飛ぶ past (意識が飛んだ, 自力で飛んだ); the 連体詞
            // とんだ "unexpected" only opens a noun phrase and lists the same surface with
            // priority, which no scoring nudge can overcome.
            if (word.Text == "飛んだ" && i > 0
                && wordInfos[i - 1].Text is "が" or "は" or "も" or "に" or "を" or "で" or "へ" or "から" or "まで"
                && word.PreMatchedWordId == null)
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "飛ぶ";
                word.NormalizedForm = "飛ぶ";
                word.PreMatchedWordId = 1429700;
                word.PreMatchedConjugations = PinnedConjugationProcess("飛んだ", "飛ぶ");
            }

            // 連用形 置き continuing a clause (皿を横に置き、…) is 置く (1421850), not the interval
            // suffix おき (三日置き), which follows a quantity instead of a case particle.
            if (word.Text == "置き" && i > 0 && wordInfos[i - 1].Text is "に" or "を" or "へ"
                && word.PreMatchedWordId == null)
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "置く";
                word.NormalizedForm = "置く";
                word.PreMatchedWordId = 1421850;
                word.PreMatchedConjugations = PinnedConjugationProcess("置き", "置く");
            }

            // Sudachi shreds 強がって into 強/がっ/て; the recombined token is 強がる (1928800).
            // Conjugated forms only: bare 強がり is legitimately the noun "bluff/bravado".
            if (word.Text is "強がって" or "強がった" && word.PreMatchedWordId == null
                && word.DictionaryForm != "強がる")
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "強がる";
                word.NormalizedForm = "強がる";
                word.PreMatchedWordId = 1928800;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, "強がる");
            }

            // 強がり before a case/topic particle is the noun 強がり "bluff" (1236110) — the がる
            // deconjugation chain otherwise resolves it all the way down to 強い. Before ながら/つつ
            // it is the verb's 連用形 and is left to normal matching.
            if (word.Text == "強がり" && word.PreMatchedWordId == null
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text is "を" or "が" or "は" or "の" or "で")
            {
                word.PartOfSpeech = PartOfSpeech.Noun;
                word.DictionaryForm = "強がり";
                word.NormalizedForm = "強がり";
                word.PreMatchedWordId = 1236110;
            }

            // 来 before やがる is the verb stem (来やがる stays split by convention), not the
            // suffix らい.
            if (word is { Text: "来", PartOfSpeech: PartOfSpeech.Verb } && i + 1 < wordInfos.Count
                && wordInfos[i + 1].DictionaryForm == "やがる")
            {
                word.PreMatchedWordId = 1547720;
            }

            // Clause-final 行け (also shouted 行けー) is the imperative of 行く (先に行け！), not
            // the potential 行ける.
            if (word.Text is "行け" or "行けー"
                && (i + 1 >= wordInfos.Count
                    || wordInfos[i + 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol))
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "行く";
                word.NormalizedForm = "行く";
                word.PreMatchedWordId = 1578850;
                word.PreMatchedConjugations = PinnedConjugationProcess("行け", "行く");
            }

            // 思いで after an adjective is 思い + で (いじらしい思いで励ます), not the memory noun
            // 思い出 — the memory takes の before a noun, not an adjective directly.
            if (word.Text == "思いで" && i > 0
                && wordInfos[i - 1].PartOfSpeech is PartOfSpeech.IAdjective or PartOfSpeech.Adnominal)
            {
                int odMid = word.EndOffset >= 0 ? word.EndOffset - 1 : -1;
                word.Text = "思い";
                word.DictionaryForm = "思い";
                word.NormalizedForm = "思い";
                word.PartOfSpeech = PartOfSpeech.Noun;
                word.PreMatchedWordId = null;
                var deP = new WordInfo(word)
                {
                    Text = "で", DictionaryForm = "で", NormalizedForm = "で",
                    Reading = "デ", PartOfSpeech = PartOfSpeech.Particle,
                    StartOffset = odMid
                };
                word.EndOffset = odMid;
                wordInfos.Insert(i + 1, deP);
            }

            // 連用形 仕舞い before 直す is 仕舞う (仕舞い直す), not the noun 仕舞い "the end".
            if (word.Text == "仕舞い" && i + 1 < wordInfos.Count
                && wordInfos[i + 1].DictionaryForm == "直す" && word.PreMatchedWordId == null)
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "仕舞う";
                word.NormalizedForm = "仕舞う";
                word.PreMatchedWordId = 1305380;
                word.PreMatchedConjugations = PinnedConjugationProcess("仕舞い", "仕舞う");
            }

            // 放ってお* is the expression 放っておく (1907980), not bare 放る.
            if (word.Text.StartsWith("放ってお", StringComparison.Ordinal) && word.Text.Length >= 5
                && word.PreMatchedWordId == null)
            {
                word.DictionaryForm = "放っておく";
                word.NormalizedForm = "放っておく";
                word.PreMatchedWordId = 1907980;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, "放っておく");
            }

            // 向い* after を with a direction word is 向く "to face" (前を向いて), not 剥く "to peel".
            if (word.Text.StartsWith("向い", StringComparison.Ordinal) && i >= 2
                && wordInfos[i - 1].Text == "を"
                && wordInfos[i - 2].Text is "前" or "後ろ" or "横" or "上" or "下" or "こっち" or "そっち"
                    or "あっち" or "未来" or "正面"
                && word.PreMatchedWordId == null)
            {
                word.DictionaryForm = "向く";
                word.NormalizedForm = "向く";
                word.PreMatchedWordId = 1277080;
                word.PreMatchedConjugations = PinnedConjugationProcess(word.Text, "向く");
            }

            // 宛 as a suffix after a name/noun is 宛て "addressed to" (1448820).
            if (word is { Text: "宛", PartOfSpeech: PartOfSpeech.Suffix } && i > 0
                && word.PreMatchedWordId == null)
            {
                word.PreMatchedWordId = 1448820;
            }

            // 雪の下 in prose is 雪 + の + 下 (buried under snow); the plant ユキノシタ is a
            // botanical term.
            if (word.Text == "雪の下")
            {
                int ykEnd = word.EndOffset;
                word.Text = "雪";
                word.DictionaryForm = "雪";
                word.NormalizedForm = "雪";
                word.PartOfSpeech = PartOfSpeech.Noun;
                word.PreMatchedWordId = null;
                var noP = new WordInfo(word)
                {
                    Text = "の", DictionaryForm = "の", NormalizedForm = "の",
                    Reading = "ノ", PartOfSpeech = PartOfSpeech.Particle,
                    StartOffset = ykEnd >= 0 ? ykEnd - 2 : -1,
                    EndOffset = ykEnd >= 0 ? ykEnd - 1 : -1
                };
                var shita = new WordInfo(word)
                {
                    Text = "下", DictionaryForm = "下", NormalizedForm = "下",
                    Reading = "シタ", PartOfSpeech = PartOfSpeech.Noun,
                    StartOffset = ykEnd >= 0 ? ykEnd - 1 : -1
                };
                word.EndOffset = ykEnd >= 0 ? ykEnd - 2 : ykEnd;
                wordInfos.Insert(i + 1, noP);
                wordInfos.Insert(i + 2, shita);
            }

            // 右手首/左手首 is 右+手首 "right wrist": the wrist is the head noun, the side its
            // modifier — the lattice otherwise cuts 右手+首 "right hand's neck".
            if (word.Text is "右手首" or "左手首")
            {
                int tkMid = word.EndOffset >= 0 ? word.EndOffset - 2 : -1;
                string side = word.Text[..1];
                word.Text = side;
                word.DictionaryForm = side;
                word.NormalizedForm = side;
                word.PartOfSpeech = PartOfSpeech.Noun;
                word.PreMatchedWordId = null;
                var tekubi = new WordInfo(word)
                {
                    Text = "手首", DictionaryForm = "手首", NormalizedForm = "手首",
                    Reading = "テクビ", PartOfSpeech = PartOfSpeech.Noun,
                    PreMatchedWordId = 1327770,
                    StartOffset = tkMid
                };
                word.EndOffset = tkMid;
                wordInfos.Insert(i + 1, tekubi);
            }

            // ずにおこう: Sudachi strands お as an interjection before こう — it is the volitional
            // of the aspectual おく (〜ずにおく "leave it un-done").
            if (word is { Text: "お", PartOfSpeech: PartOfSpeech.Interjection }
                && i > 0 && (wordInfos[i - 1].Text == "に"
                             || wordInfos[i - 1].Text.EndsWith("ずに", StringComparison.Ordinal))
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "こう")
            {
                word.Text = "おこう";
                word.DictionaryForm = "おく";
                word.NormalizedForm = "おく";
                word.Reading = "オコウ";
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.PreMatchedWordId = 1421850;
                word.PreMatchedConjugations = PinnedConjugationProcess("おこう", "おく");
                word.EndOffset = wordInfos[i + 1].EndOffset;
                wordInfos.RemoveAt(i + 1);
            }

            // 主人格 is 主 + 人格 "primary personality"; the lattice cuts 主人+格 "master's rank".
            if (word.Text == "主人" && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "格")
            {
                word.Text = "主";
                word.DictionaryForm = "主";
                word.NormalizedForm = "主";
                word.Reading = "シュ";
                word.PreMatchedWordId = null;
                var jinkaku = wordInfos[i + 1];
                jinkaku.Text = "人格";
                jinkaku.DictionaryForm = "人格";
                jinkaku.NormalizedForm = "人格";
                jinkaku.Reading = "ジンカク";
                jinkaku.PartOfSpeech = PartOfSpeech.Noun;
                jinkaku.PreMatchedWordId = 1366730;
                if (word.EndOffset >= 0)
                {
                    jinkaku.StartOffset = word.EndOffset - 1;
                    word.EndOffset -= 1;
                }
            }

            // 存 + 在す is a shredded 存在する (存在すべく); 在す alone is the archaic honorific います.
            if (word.Text == "在す" && i > 0 && wordInfos[i - 1].Text == "存")
            {
                wordInfos[i - 1].Text = "存在";
                wordInfos[i - 1].DictionaryForm = "存在";
                wordInfos[i - 1].NormalizedForm = "存在";
                wordInfos[i - 1].Reading = "ソンザイ";
                wordInfos[i - 1].PartOfSpeech = PartOfSpeech.Noun;
                word.Text = "す";
                word.DictionaryForm = "する";
                word.NormalizedForm = "する";
                word.Reading = "ス";
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.PreMatchedWordId = 1157170;
                if (word.StartOffset >= 0)
                {
                    wordInfos[i - 1].EndOffset = word.StartOffset + 1;
                    word.StartOffset += 1;
                }
            }

            // うっす is the colloquial greeting/affirmative (2262630, おいっす family). Sudachi normalises
            // its dictionary form to うす, so candidate generation reaches 臼/薄 through the うす key and
            // never sees the greeting entry that attests the surface directly.
            if (word.Text == "うっす")
            {
                word.PartOfSpeech = PartOfSpeech.Interjection;
                word.DictionaryForm = "うっす";
                word.NormalizedForm = "うっす";
                word.PreMatchedWordId = 2262630;
            }

            // あんた is the colloquial pronoun "you" (1979920); Sudachi sometimes tags it as the past of
            // 編む (あんた+って → 編む). The pronoun never collides with a real あんた verb form (編む past is
            // あんだ), so always pin the pronoun.
            if (word.Text == "あんた")
            {
                word.PartOfSpeech = PartOfSpeech.Pronoun;
                word.DictionaryForm = "あんた";
                word.NormalizedForm = "あんた";
                word.PreMatchedWordId = 1979920;
            }

            // 方々 with を/に + a movement verb (町の方々を歩き回った, 方々に散らばった) is the adverb ほうぼう
            // "here and there" (1584105) — places are moved through. Sudachi reads bare 方々 as カタガタ, so
            // skipping the pin is not enough: the reading-match bonus makes かたがた win anyway; pin ほうぼう
            // explicitly. Otherwise 方々 after の is the honorific "people" かたがた (1584100, 軍の方々,
            // あの方々) — people are addressed or spoken to.
            bool movementFollows = i + 2 < wordInfos.Count && wordInfos[i + 1].Text is "を" or "に"
                && (wordInfos[i + 2].DictionaryForm is "歩く" or "巡る" or "旅する" or "走る" or "駆ける"
                       or "散る" or "散らばる" or "逃げる" or "飛ぶ"
                    || wordInfos[i + 2].DictionaryForm.EndsWith("回る", StringComparison.Ordinal)
                    || wordInfos[i + 2].DictionaryForm.EndsWith("散る", StringComparison.Ordinal));
            if (word.Text == "方々" && movementFollows)
                word.PreMatchedWordId = 1584105;
            else if (word.Text == "方々" && i > 0 && wordInfos[i - 1].Text == "の")
                word.PreMatchedWordId = 1584100;

            // Clause-final だい (何がだい, そうだい) is the familiar question particle "is it?" (2097680),
            // not the noun 代 "charge/price" (1982860); Sudachi tags it Prefix. Remap only at clause end.
            if (word.Text == "だい" && word.PartOfSpeech == PartOfSpeech.Prefix
                && (i + 1 >= wordInfos.Count
                    || wordInfos[i + 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol))
            {
                word.PartOfSpeech = PartOfSpeech.Particle;
                word.DictionaryForm = "だい";
                word.PreMatchedWordId = 2097680;
            }

            // A kana-spelled reciprocal 〜合う verb (憎みあって, normalized 憎み合う) can't match its kanji-合
            // entry via the normal deconjugation path and drops. Pin it to the 〜合う compound so it resolves
            // as one token instead of vanishing (Sudachi keeps the reciprocal whole: 憎みあっ|て). Skip when
            // any deconjugation of the surface reaches a lookup entry (つきあって→つきあう, わかりあえる→
            // わかりあう) — those resolve through the normal path, which picks the right form (a potential
            // keeps its potential chain) and preserves scoring margins; the pin is only for would-drop
            // surfaces.
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

            // あって/あっていた directly after a verb 連用形 (読み/取り), tagged as ある (有る/在る), is the
            // reciprocal 合う (1284430, 読み合って) — ある only follows a て-form, never a bare 連用形. Re-pin here
            // (after the って re-cut has already run) so the reciprocal reading wins without triggering the
            // っ+て mora-theft that mangles it if done early. 読みがあって keeps ある (the が breaks adjacency).
            // The previous token must be Sudachi-tagged Verb: a deverbal noun before あって (Sudachi tags 実り
            // in 実りあって合格した as Noun) is the existential "thanks to X" frame, not a 連用形 mid-compound.
            // あって followed by の/も/こそ is excluded for the same reason — those are the existential idiom
            // frames Xあっての/Xあっても/Xあってこそ (命あっての物種, 望みあっても).
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

            // An i-adjective that swallowed a quotative って (硬い+って → 硬いって, mis-deconjugated by
            // CombineInflections as a bogus "te form") must be re-split: the only adjective inflection ending
            // in って is the geminated emphatic te-form 〜くって (嬉しくって, よくって) — excluded via the く check —
            // so everywhere else the って is the quotative particle (2086960). The adjective itself stays whole.
            if (word.PartOfSpeech == PartOfSpeech.IAdjective && word.Text.Length >= 4
                && word.Text.EndsWith("って", StringComparison.Ordinal)
                && word.Text[^3] != 'く'
                && HasNonNameCompoundLookup?.Invoke(word.Text[..^2]) == true)
            {
                int mid = word.EndOffset >= 0 ? word.EndOffset - 2 : -1;
                var tte = new WordInfo(word)
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って", Reading = "ッテ",
                    PartOfSpeech = PartOfSpeech.Particle, PreMatchedWordId = 2086960,
                    StartOffset = mid, EndOffset = word.EndOffset
                };
                word.Text = word.Text[..^2];
                word.EndOffset = mid;
                if (word.Reading.EndsWith("ッテ", StringComparison.Ordinal))
                    word.Reading = word.Reading[..^2];
                wordInfos.Insert(i + 1, tte);
            }

            // The contracted もんか after a predicate is the rhetorical "like hell / as if" particle (2130440),
            // not the noun 門下 "disciple" (1724650, read もんか) — which follows の (剣の門下). Sudachi may keep
            // もんか whole (あるもんか) or split it もん+か (踊らされる|もん|か, which JMDict would otherwise compound
            // to 門下); fold the split shape first, then remap. Only the contracted もんか is touched — the
            // uncontracted ものか/もの+か is genuinely ambiguous with the deliberative もの + か (どうしたものか =
            // "what to do", 信用していいものか = "is it OK to…"), so that is left to normal scoring.
            bool prevIsPredicate = i > 0
                && wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary;
            if (prevIsPredicate && word.Text == "もん"
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "か")
            {
                word.Text = "もんか";
                word.EndOffset = wordInfos[i + 1].EndOffset;
                wordInfos.RemoveAt(i + 1);
            }
            if (prevIsPredicate && word.Text == "もんか")
            {
                word.PartOfSpeech = PartOfSpeech.Particle;
                word.DictionaryForm = "ものか";
                word.NormalizedForm = "ものか";
                word.PreMatchedWordId = 2130440;
            }

            if (word.Text == "おい")
                word.PartOfSpeech = PartOfSpeech.Interjection;

            if (word is { Text: "つ", PartOfSpeech: PartOfSpeech.Suffix })
                word.PartOfSpeech = PartOfSpeech.Counter;

            // Sudachi tags counter suffixes (e.g. 頭/とう, 匹, 本) with 助数詞 in POS detail
            if (word is { PartOfSpeech: PartOfSpeech.Suffix } &&
                word.HasPartOfSpeechSection(PartOfSpeechSection.Counter))
                word.PartOfSpeech = PartOfSpeech.Counter;

            // 人 after a numeral should be the counter にん, not the suffix じん
            if (word is { Text: "人", PartOfSpeech: PartOfSpeech.Suffix } &&
                i > 0 && (wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Numeral ||
                          wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.Numeral)))
                word.PartOfSpeech = PartOfSpeech.Counter;

            // 家 followed by a case particle should be the noun いえ, not the suffix け
            if (word is { Text: "家", PartOfSpeech: PartOfSpeech.Suffix } &&
                i + 1 < wordInfos.Count &&
                wordInfos[i + 1] is { PartOfSpeech: PartOfSpeech.Particle, Text: "から" or "を" or "が" or "に" or "で" or "へ" or "の" or "は" or "も" })
                word.PartOfSpeech = PartOfSpeech.Noun;

            if (word is { Text: "山", PartOfSpeech: PartOfSpeech.Suffix })
                word.PartOfSpeech = PartOfSpeech.Noun;

            // 色 tagged as a suffix is the standalone noun いろ ("X-coloured": 敵色, 空色) unless it follows
            // a numeral, where it is the counter しょく (三色). Compounds where 色 is read しょく (特色, 景色,
            // 原色) are lexicalised and matched whole, so a lone suffix-色 outside counter context is いろ.
            if (word is { Text: "色", PartOfSpeech: PartOfSpeech.Suffix } &&
                !(i > 0 && (wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Numeral ||
                            wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.Numeral))))
                word.PartOfSpeech = PartOfSpeech.Noun;

            // Sudachi tags いつ as 名詞,数詞 (the rare 五/いつ "five" reading) when a counter-like token
            // follows (いつ重…, いつ匹…), but as 代名詞 elsewhere. Standalone いつ is virtually always the
            // pronoun 何時 "when" (1188760) — the numeral reading only lives in fixed compounds (五日/いつか,
            // 五つ/いつつ), which Sudachi tokenises whole. Reclassify so it resolves to 何時 instead of the
            // numeral 五, which is otherwise picked and then dropped as a short-kana misparse.
            if (word is { Text: "いつ", PartOfSpeech: PartOfSpeech.Noun }
                && word.PartOfSpeechSection1 == PartOfSpeechSection.Numeral)
                word.PartOfSpeech = PartOfSpeech.Pronoun;

            // 重 that Sudachi tags as a standalone 助数詞 counter (え, "-fold") directly before a noun is
            // really the じゅう "heavy" n-prefix (重光線 "heavy beam", 重工業, 重戦車). Real え/じゅう counter
            // uses (二重, 八重桜, 三重県, 五重の塔) never reach here: Sudachi keeps them as one compound token
            // or reads 重 as the noun じゅう — only the heavy-prefix-before-noun pattern surfaces as a lone
            // Counter-え 重. Pin to 2108240 (重/じゅう, n-pref + ctr), which the え reading never reaches.
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

            // Ordinal 目 written in kana め (３機め, ２回め, ５つめ) is the ordinal suffix 目 (1604890,
            // "-th"), not the derogatory/humble 奴/め (2089650). Sudachi tags kana め as 接尾辞,名詞的,
            // routing it through the pure-suffix candidate filter that drops noun/suffix hybrids like 目,
            // and the bare-kana め→目 form (RI2) is otherwise an ExcludedMisparses entry. Pin it to the
            // canonical 目 form (RI0) when a counter/counter-possible token preceded by a number sits
            // directly before it. The derogatory 奴/め (守銭奴め, 馬鹿め) follows plain nouns, never a counter.
            if (word is { Text: "め", PartOfSpeech: PartOfSpeech.Suffix } && i > 0)
            {
                static bool StartsWithNumber(string t) =>
                    t.Length > 0 && (char.IsDigit(t[0]) || "一二三四五六七八九十百千万〇零".Contains(t[0]));
                static bool IsNumeral(WordInfo w) =>
                    w.PartOfSpeech == PartOfSpeech.Numeral ||
                    w.HasPartOfSpeechSection(PartOfSpeechSection.Numeral) ||
                    w.HasPartOfSpeechSection(PartOfSpeechSection.Amount) ||
                    StartsWithNumber(w.Text);

                var prevCtr = wordInfos[i - 1];
                bool prevIsCounterLike = prevCtr.PartOfSpeech is PartOfSpeech.Counter ||
                                         prevCtr.HasPartOfSpeechSection(PartOfSpeechSection.Counter) ||
                                         prevCtr.HasPartOfSpeechSection(PartOfSpeechSection.PossibleCounterWord);
                // The number can be fused into the counter by CombineAmounts (２回 め, ５人 め, 三つ め),
                // where Sudachi often drops the counter POS tag, or sit as a separate preceding token
                // (３ 機 め). A noun-ish token that begins with a number is the fused amount; otherwise
                // require an explicit counter token preceded by a number. Names (一郎め) keep their Name
                // POS and plain nouns (馬鹿め, 守銭奴め) carry no number, so the derogatory 奴/め is safe.
                bool fusedAmount = prevCtr.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Counter or PartOfSpeech.Numeral
                                   && StartsWithNumber(prevCtr.Text) && prevCtr.Text.Length > 1;
                bool counterAfterNumber = prevIsCounterLike && i > 1 && IsNumeral(wordInfos[i - 2]);
                if (fusedAmount || counterAfterNumber)
                {
                    word.DictionaryForm = "目";
                    word.NormalizedForm = "目";
                    word.PreMatchedWordId = 1604890;
                    word.PreMatchedReadingIndex = 0;
                }
            }

            if (word is { Text: "だろう" or "だろ", PartOfSpeech: PartOfSpeech.Auxiliary })
            {
                word.PartOfSpeech = PartOfSpeech.Expression;
                word.DictionaryForm = word.Text;
            }

            // だっけ (recollection "was it?") collapses to だけ "only" (1007340) via Sudachi's dictform;
            // pin the surface to the recollection ending だっけ (2131200).
            if (word.Text == "だっけ")
            {
                word.PartOfSpeech = PartOfSpeech.Expression;
                word.DictionaryForm = "だっけ";
                word.PreMatchedWordId = 2131200;
            }

            if (word.Text == "だあ")
            {
                word.Text = "だ";
                word.DictionaryForm = "です";
                word.PartOfSpeech = PartOfSpeech.Auxiliary;
            }
            else if (word.Text == "だー")
            {
                word.DictionaryForm = "です";
                word.PartOfSpeech = PartOfSpeech.Auxiliary;
            }

            // いかんせん (如何せん): prevent resegmentation into いかん + せん
            if (word.Text == "いかんせん")
                word.PreMatchedWordId = 1919420;

            // Standalone prefix-tagged せん that wasn't combined by CombinePrefixes
            // is the Kansai-ben negative of する (= しない), not the numeral prefix 千
            if (word is { Text: "せん", PartOfSpeech: PartOfSpeech.Prefix })
            {
                word.PartOfSpeech = PartOfSpeech.Expression;
                word.PreMatchedWordId = 2844926;
            }

            // セン in katakana not preceded by a numeral → 線 (line), not 千 (thousand)
            if (word.Text == "セン")
            {
                var prev = i > 0 ? wordInfos[i - 1] : null;
                if (prev is not { PartOfSpeech: PartOfSpeech.Numeral })
                    word.PreMatchedWordId = 1391780;
            }

            // ノリ in katakana → 乗り (riding/enthusiasm/vibe, nf07), not 海苔 (seaweed, nf38)
            if (word.Text == "ノリ")
                word.PreMatchedWordId = 1354720;

            // 頚木 is a kanji variant of 頸木 (くびき/yoke, 1831840) — not in JMDict lookups,
            // so resegmentation would split it into 頚+木
            if (word.Text == "頚木")
                word.PreMatchedWordId = 1831840;

            // かあ is the drawn-out question particle か (Sudachi tags it 助詞 and normalises to か),
            // not the noun カア "cawing of a crow" (2076470) that the surface match otherwise wins.
            if (word is { Text: "かあ", PartOfSpeech: PartOfSpeech.Particle })
            {
                word.DictionaryForm = "か";
                word.NormalizedForm = "か";
                word.PreMatchedWordId = 2028970;
            }

            // したり after a case particle (を/が) is する + the listing ～たり (キスをしたり), not the
            // triumphant interjection したり "bless me!" (1631980) — an interjection never follows a
            // case particle. The whole-surface interjection wins outside the form scorer, so pin する;
            // PreMatchedConjugations carries the ～たり chain the pin would otherwise drop.
            if (word.Text == "したり" && i > 0 && wordInfos[i - 1].Text is "を" or "が")
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "する";
                word.NormalizedForm = "為る";
                word.PreMatchedWordId = 1157170;
                word.PreMatchedReadingIndex = 1;
                word.PreMatchedConjugations = ["tari", "(unstressed infinitive)"];
            }

            // アリアリ in katakana → ありあり "vividly/plainly" (2007200, the adverb Sudachi normalises
            // to), not the gairaigo currency ariary (2868726). Mirrors the ノリ/セン katakana rules.
            if (word.Text == "アリアリ")
                word.PreMatchedWordId = 2007200;

            // Kana そうそう → the interjection/adverb "that's right; indeed" (1006640), not 錚々
            // "eminent" (1845890). Exception: before たる/たり it is the taru-adjective 錚々たる
            // (錚々たる顔ぶれ "distinguished lineup"), so leave that to normal scoring.
            if (word.Text == "そうそう"
                && !(i + 1 < wordInfos.Count && wordInfos[i + 1].Text is "たる" or "たり"))
                word.PreMatchedWordId = 1006640;

            // いとおしい (kana) is the adj-i 愛おしい "beloved" (2007340) — the only entry for this kana
            // surface. Sudachi instead tags it as the archaic verb 射通す/いとおす "to pierce" (1846380).
            if (word.Text == "いとおしい" && word.DictionaryForm is "いとおす" or "射通す")
            {
                word.PartOfSpeech = PartOfSpeech.IAdjective;
                word.DictionaryForm = "いとおしい";
                word.NormalizedForm = "愛おしい";
                word.PreMatchedWordId = 2007340;
            }

            // なかれ is the classical negative imperative 勿れ "do not" (1535750), not the adj-i 無い/なし
            // (1529520) that Sudachi's dictform routes it to (恐れることなかれ).
            if (word is { Text: "なかれ" } && word.DictionaryForm is "ない" or "なし" or "無い")
            {
                word.PartOfSpeech = PartOfSpeech.Suffix;
                word.DictionaryForm = "なかれ";
                word.NormalizedForm = "なかれ";
                word.PreMatchedWordId = 1535750;
            }

            // Ordinal 目 (kanji) after a numeric 〜つ counter (三つ目) is the ordinal suffix 目 "-th"
            // (1604890), not the noun 三つ目 "three-eyed being" (2871573) that resolution would otherwise
            // compound. Scoped to the つ-counter; 番目/回目/個目 resolve correctly already.
            if (word is { Text: "目", PartOfSpeech: PartOfSpeech.Suffix } && i > 0
                && wordInfos[i - 1].Text.EndsWith("つ", StringComparison.Ordinal)
                && wordInfos[i - 1].Text.Length > 1
                && TakesOrdinalMeAfterTsu(wordInfos[i - 1].Text[0]))
            {
                word.DictionaryForm = "目";
                word.NormalizedForm = "目";
                word.PreMatchedWordId = 1604890;
                word.PreMatchedReadingIndex = 0;
            }
        }

        return wordInfos;
    }

    // First char of a 〜つ counter whose 目 is the ordinal suffix "-th": a numeral other than 一/二 —
    // 一つ目/二つ目 have their own ordinal entries ("first/second (in a series)", 1160910/1625070).
    private static bool TakesOrdinalMeAfterTsu(char c) =>
        c is not ('一' or '二' or '１' or '２')
        && (char.IsDigit(c) || "一二三四五六七八九十百千".Contains(c));

    // True when the token's surface can reach a JMDict lookup entry without a pin: its dictionary
    // form, its kana surface, or any deconjugated form of the surface is a lookup key. Such tokens
    // resolve through the normal scoring path, so a pin would only degrade them.
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

    // Recovers the conjugation chain for a pinned inflected surface — PreMatchedWordId bypasses the
    // deconjugation-based resolution that normally fills Conjugations, so without this every pin emits
    // a bare lemma. Null when the surface already is the dictionary form or no deconjugation reaches it.
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

    /// <summary>
    /// Fixes Sudachi reading disambiguations for kanji homographs using contextual cues.
    /// E.g. 表 before へ/に (directional) when not preceded by a noun → おもて not ひょう.
    /// </summary>
    private List<WordInfo> FixReadingAmbiguity(List<WordInfo> wordInfos)
    {
        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            // 表 (ヒョウ) → オモテ when followed by directional particle and not preceded by a noun
            // e.g. 表へ出る (go outside) vs メニュー表 (menu chart)
            if (word is { Text: "表", Reading: "ヒョウ" } &&
                i + 1 < wordInfos.Count && wordInfos[i + 1].Text is "へ" or "に" &&
                (i == 0 || wordInfos[i - 1].PartOfSpeech != PartOfSpeech.Noun))
            {
                word.Reading = "オモテ";
            }

            // 何 (ナン) → ナニ before を/が/も or at end of sentence
            if (word is { Text: "何", Reading: "ナン" })
            {
                var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                if (next == null || next.Text is "を" or "が" or "も")
                    word.Reading = "ナニ";
            }

            // 一日/１日 → イチニチ unless preceded by a month (X月一日 = date → keep ツイタチ)
            if (word is { Reading: "ツイタチ", Text: "一日" or "１日" or "1日" })
            {
                var prev = i > 0 ? wordInfos[i - 1] : null;
                if (prev == null || !prev.Text.EndsWith('月'))
                    word.Reading = "イチニチ";
            }

            // 禍 (カ) → ワザワイ when standalone — カ reading only used in compounds (コロナ禍, 戦禍, 禍根)
            if (word is { Text: "禍", Reading: "カ" })
                word.Reading = "ワザワイ";

            // 全機 (マサキ given-name reading) → ゼンキ "all aircraft/all units" — the common-noun reading.
            // Sudachi picks the rare まさき name reading after a leading dash/symbol; the name then wins on
            // ReadingMatchScore. Correcting the reading (cache key includes Reading) flips it back to 全機 ぜんき.
            if (word is { Text: "全機", Reading: "マサキ" })
                word.Reading = "ゼンキ";

            // 私 (シ) → ワタシ when standalone — シ reading only in compounds (私的, 私立, 私用)
            if (word is { Text: "私", Reading: "シ" })
            {
                word.Reading = "ワタシ";
                word.PartOfSpeech = PartOfSpeech.Pronoun;
            }

            // 寒気 (カンキ cold air) → サムケ (chills) when felt: followed by が+する or
            // を+覚える/感じる/催す (寒気がする, 寒気を覚える) vs 寒気が南下する (cold air moves south)
            if (word is { Text: "寒気", Reading: "カンキ" } &&
                i + 2 < wordInfos.Count &&
                ((wordInfos[i + 1].Text == "が" && wordInfos[i + 2].DictionaryForm == "する")
                 || (wordInfos[i + 1].Text == "を"
                     && wordInfos[i + 2].DictionaryForm is "覚える" or "感じる" or "催す")))
            {
                word.Reading = "サムケ";
            }

            // 後 (ゴ) → アト when followed by a numeral/何 or a quantity adverb — adverbial
            // "more/remaining": 後何年 (how many more years), 後少し / 後もう少し (a little more)
            if (word is { Text: "後", Reading: "ゴ" } &&
                i + 1 < wordInfos.Count &&
                (wordInfos[i + 1].PartOfSpeech == PartOfSpeech.Numeral ||
                 wordInfos[i + 1].HasPartOfSpeechSection(PartOfSpeechSection.Numeral) ||
                 wordInfos[i + 1].Text.StartsWith("もう", StringComparison.Ordinal) ||
                 wordInfos[i + 1].Text is "少し" or "すこし" or "ちょっと" or "わずか"))
            {
                word.Reading = "アト";
            }

            // The passive 弾かれ* is はじく "to be repelled/deflected" (bullets, blades, backboards,
            // and the startle idiom 弾かれたように) — the ひく "to be played" passive occurs almost
            // exclusively around instruments/music, so an instrument in the window keeps the ヒカレ
            // reading (コンサートでピアノが弾かれた). Passive only: negatives and causatives
            // (弾かない, 弾かせてやる) are overwhelmingly the play-verb and stay untouched.
            if (word.DictionaryForm == "弾く" && word.Reading.StartsWith("ヒカレ", StringComparison.Ordinal))
            {
                bool musicContext = false;
                for (int k = Math.Max(0, i - 6); k < Math.Min(wordInfos.Count, i + 4) && !musicContext; k++)
                    musicContext = wordInfos[k].Text is "ピアノ" or "ギター" or "エレキギター" or "バイオリン"
                        or "ヴァイオリン" or "曲" or "演奏" or "コンサート" or "音楽" or "弦" or "楽器" or "バンド";
                if (!musicContext)
                    word.Reading = "ハジカレ" + word.Reading[3..];
            }

            // 生 after の with a life-cycle determiner (次の生を迎える) is せい "life/incarnation"
            // (2088240), not なま "raw" — なま lists the bare 生 with priority, so a reading nudge
            // is not enough.
            if (word is { Text: "生", Reading: "ナマ" } && i >= 2
                && wordInfos[i - 1].Text == "の"
                && wordInfos[i - 2].Text is "次" or "前" or "今" or "来" or "この" or "別")
            {
                word.Reading = "セイ";
                word.PreMatchedWordId = 2088240;
            }

            // 一分/１分 before a quantity approximator is the duration いっぷん (1166290); the
            // fullwidth surface is only listed on the いちぶ "one tenth" entry, so the forward-anchor
            // boost cannot close the surface-match gap.
            if (word.Text is "一分" or "１分" && i + 1 < wordInfos.Count
                && wordInfos[i + 1].Text is "ぐらい" or "くらい" or "ほど"
                && word.PreMatchedWordId == null)
            {
                word.PreMatchedWordId = 1166290;
            }

            // Sudachi reads batting-practice 素振り as スブリ (1749550); the scorer otherwise
            // overrides it with the higher-priority そぶり "demeanour" (1397260).
            if (word is { Text: "素振り", Reading: "スブリ" })
            {
                word.PreMatchedWordId = 1749550;
            }

            // 項 after a numeral is the clause/paragraph counter こう (１項, 第２項; 1282980),
            // not うなじ "nape".
            if (word is { Text: "項", Reading: "コウ" } && i > 0
                && (wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Numeral ||
                    wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.Numeral)))
            {
                word.PreMatchedWordId = 1282980;
            }

            // 体 between 何/a numeral and 目 is the counter たい (何体目; 1409150), not からだ.
            if (word is { Text: "体", Reading: "タイ" } && i > 0 && i + 1 < wordInfos.Count
                && (wordInfos[i - 1].Text == "何"
                    || wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Numeral
                    || wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.Numeral))
                && wordInfos[i + 1].Text == "目")
            {
                word.PreMatchedWordId = 1409150;
            }

            // 縁 after の with a rimmed-object noun is ふち "rim/edge" (浴槽の縁), not えん "fate".
            if (word is { Text: "縁", Reading: "エン" } && i >= 2
                && wordInfos[i - 1].Text == "の"
                && wordInfos[i - 2].Text is "浴槽" or "風呂" or "プール" or "池" or "井戸" or "窓"
                    or "テーブル" or "机" or "帽子" or "コップ" or "グラス" or "崖" or "屋根" or "ベッド")
            {
                word.Reading = "フチ";
            }

            // 通り directly after a noun (no particle) is the suffix どおり "as per" (思惑通り,
            // 予定通り; 1432930), not the standalone street/way noun.
            if (word is { Text: "通り", Reading: "トオリ" } && i > 0
                && wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
            {
                word.PreMatchedWordId = 1432930;
            }

            // 札 read サツ in a card/tile game frame is ふだ — both a game word in the window AND a
            // card-handling verb are required, since money changes hands in the same scenes
            // (麻雀に負けて札を渡した stays banknote).
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

            // 次 (ジ) standalone prefix → ツギ noun — ジ reading only in compounds (次回, 次期, 次男)
            if (word is { Text: "次", Reading: "ジ", PartOfSpeech: PartOfSpeech.Prefix })
            {
                word.Reading = "ツギ";
                word.PartOfSpeech = PartOfSpeech.CommonNoun;
            }

            // 何時 (ナンドキ) → ナンジ — ナンドキ is archaic; modern usage is ナンジ (what time) or いつ (when)
            if (word is { Text: "何時", Reading: "ナンドキ" })
                word.Reading = "ナンジ";

            // 長 as suffix (チョウ) means "chief/head" — JMDict only has this as n (1429740), not suf.
            // Reclassify so the parser matches ちょう instead of なが (2647210, pref/suf "long").
            if (word is { Text: "長", Reading: "チョウ", PartOfSpeech: PartOfSpeech.Suffix })
                word.PartOfSpeech = PartOfSpeech.Noun;

            // 隙 (ヒマ) → スキ — ヒマ reading is obsolete; modern standalone 隙 is always すき
            if (word is { Text: "隙", Reading: "ヒマ" })
                word.Reading = "スキ";

            // 弄* (イラ*) → イジ* — いらう is archaic; modern 弄る is always いじる
            if (word.DictionaryForm == "弄う")
            {
                word.DictionaryForm = "弄る";
                word.NormalizedForm = "弄る";
                word.Reading = word.Reading!.Replace("イラ", "イジ");
            }

            // 角 (カド) — Sudachi always gives カド but standalone 角 has three common readings:
            //   かど (corner): 角を曲がる, 建物の角
            //   つの (horn):   鬼の角, 角が生えている
            //   かく (angle):  三角形の角, 角が90度
            if (word is { Text: "角", Reading: "カド" })
            {
                var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                var prev = i > 0 ? wordInfos[i - 1] : null;
                var next2 = i + 2 < wordInfos.Count ? wordInfos[i + 2] : null;
                var prev2 = i >= 2 ? wordInfos[i - 2] : null;

                // つの: 角が生え… / 角が折れ… (only horns grow/break off)
                bool isHornVerb = next is { Text: "が" or "を" } && next2 != null &&
                                  next2.DictionaryForm is "生える" or "生やす" or "折れる" or "折る"
                                      or "研ぐ" or "磨く";

                // つの: creature/demon + の + 角
                bool afterCreature = prev is { Text: "の" } && prev2 != null &&
                                     IsHornBearerWord(prev2.Text);

                // つの: 頭/額/おでこ + に/の + 角
                bool afterHead = prev is { Text: "に" or "の" } && prev2 != null &&
                                 prev2.Text is "頭" or "額" or "おでこ";

                // かく: geometry word + の + 角 (三角形の角, 多角形の角)
                bool afterGeometry = prev is { Text: "の" } && prev2 != null &&
                                     (prev2.Text.EndsWith("角形") || prev2.Text.EndsWith("多角"));

                // かく: 角 + が/は/も + degree/equality (角が90度, 角は等しい)
                var next3 = i + 3 < wordInfos.Count ? wordInfos[i + 3] : null;
                bool beforeDegree = next is { Text: "が" or "は" or "も" } && next2 != null &&
                                    (next2.Text.Contains('度') || next2.DictionaryForm is "等しい"
                                     || ((next2.PartOfSpeech == PartOfSpeech.Numeral
                                         || next2.HasPartOfSpeechSection(PartOfSpeechSection.Numeral))
                                        && next3 is { Text: "度" }));

                if (isHornVerb || afterCreature || afterHead)
                    word.Reading = "ツノ";
                else if (afterGeometry || beforeDegree)
                    word.Reading = "カク";
            }

            // 額 (ガク) → ヒタイ in body-contact context:
            // 額にキスをした, 額に手を当てる, 額を叩く, 額の傷, etc.
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

            // 皆 (ミナ) → ミンナ — standalone 皆 is almost always みんな in modern Japanese;
            // みな is literary/formal and typically written in kana.
            // if (word is { Text: "皆", Reading: "ミナ" })
            //     word.Reading = "ミンナ";

            // 抱く (イダク) → ダク — いだく is literary; modern standalone 抱く is overwhelmingly だく.
            if (word is { DictionaryForm: "抱く", Reading: { } r } && r.StartsWith("イダ"))
                word.Reading = r.Replace("イダ", "ダ");

            // 様 disambiguation: さま (honorific suffix, 1545790) vs よう (appearance/manner, 1605840)
            // Sudachi reading reliably distinguishes: サマ → honorific, ヨウ → manner
            // if (word is { Text: "様", Reading: "サマ" })
            //     word.PreMatchedWordId = 1545790;

            // Kana よう as 形状詞/助動詞語幹 → 様/manner (1605840), not 陽/positive (1605845)
            if (word is { Text: "よう", Reading: "ヨウ", DictionaryForm: "よう" })
                word.PreMatchedWordId = 1605840;

            // 事 (ジ) → コト when Sudachi misclassified as suffix after verb/expression
            // ジ reading only occurs in kango compounds (仕事, 用事, 無事); those are parsed as single tokens.
            // When 事 is orphaned (after a non-noun), it is the nominalizer こと.
            if (word is { Text: "事", Reading: "ジ", WasReclassifiedFromSuffix: true })
                word.Reading = "コト";

            // たった in time-elapsed context → 経つ (1251100), not 断つ/立つ.
            // When preceded by a time-unit noun (年/月/日/週/間), the intended meaning is
            // "X time has passed" (経つ), not "to cut" (断つ) or "to stand" (立つ).
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

            // 糞 (フン, animal feces) → クソ (damn/shit) unless context suggests literal droppings.
            // ふん is natural after の (鼠の糞, 犬の糞) or before と (糞と尿);
            // standalone 糞 is overwhelmingly くそ in modern Japanese.
            if (word is { Text: "糞", Reading: "フン" })
            {
                var prev = i > 0 ? wordInfos[i - 1] : null;
                var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                bool keepFun = prev is { Text: "の" } || next is { Text: "と" };
                if (!keepFun)
                    word.Reading = "クソ";
            }

            // 訳 (ヤク) → ワケ standalone — ヤク reading is for compounds (翻訳, 英訳) or 訳す;
            // standalone 訳 is always わけ (reason, meaning)
            // if (word is { Text: "訳", Reading: "ヤク", PartOfSpeech: PartOfSpeech.Noun })
            //     word.Reading = "ワケ";

            // 町 (チョウ) → マチ — チョウ reading primarily in compounds (町長, 市町村) parsed as single tokens.
            // if (word is { Text: "町", Reading: "チョウ" })
            //     word.Reading = "マチ";

            // あの: Sudachi sometimes misclassifies as 感動詞 (filler) when it's prenominal,
            // and as 連体詞 when it's actually a filler interjection.
            // Strategy: override 感動詞→PrenounAdjectival always (Sudachi filler detection unreliable),
            // then 連体詞→Interjection only when clearly not modifying a noun.
            if (word.Text == "あの")
            {
                if (word.PartOfSpeech == PartOfSpeech.Interjection)
                {
                    word.PartOfSpeech = PartOfSpeech.PrenounAdjectival;
                }
                else if (word.PartOfSpeech == PartOfSpeech.PrenounAdjectival)
                {
                    var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                    bool nextIsNoun = next is { PartOfSpeech: PartOfSpeech.Noun or PartOfSpeech.Pronoun
                        or PartOfSpeech.NaAdjective or PartOfSpeech.Counter or PartOfSpeech.Numeral
                    };
                    if (!nextIsNoun)
                        word.PartOfSpeech = PartOfSpeech.Interjection;
                }
            }

            // Continuative-form detection: noun immediately before a verb (no particle between)
            // is likely an ichidan verb stem used as a conjunctive (連用中止法).
            // e.g. 体を支え立ち上がった → 支え = 支える continuative, not the noun 支え.
            // Reclassify so the scorer applies verb-affinity and ichidan stem penalties correctly.
            // Guard: only for kanji tokens with え-row endings (valid ichidan stems), skip pre-matched.
            if (word.PartOfSpeech == PartOfSpeech.Noun
                && word.DictionaryForm == word.Text
                && word.PreMatchedWordId == null
                && word.Text.Length >= 2
                && KanaScoringHelpers.ContainsKanji(word.Text)
                && IsIchidanStemEnding(word.Reading)
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].PartOfSpeech == PartOfSpeech.Verb
                // A suru-noun before できる is the potential pattern (真似+できない), not a
                // continuative verb stem — keep the noun reading.
                && !(wordInfos[i + 1].DictionaryForm is "できる" or "出来る"
                     && HasSuruVerbCompoundLookup?.Invoke(word.Text) == true))
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = word.Text + "る";
            }

            // 露 directly before になる is あらわ "exposed" (服が露になった), not dew/Russia —
            // Sudachi's ロ lexeme reading otherwise drags scoring to the wrong homograph.
            if (word is { Text: "露", PartOfSpeech: PartOfSpeech.Noun }
                && i + 2 < wordInfos.Count
                && wordInfos[i + 1].Text == "に"
                && wordInfos[i + 2].DictionaryForm is "なる" or "成る")
            {
                word.Reading = "アラワ";
                word.NormalizedForm = "露わ";
            }

            // Clause-final あり after a に/と-ending token is the classical continuative of ある
            // (私は貴女と共にあり――), not the noun 蟻. Real ant sentences continue with が/は/を,
            // so the clause-final gate keeps them on the noun.
            if (word is { Text: "あり", PartOfSpeech: PartOfSpeech.Noun }
                && i > 0 && (wordInfos[i - 1].Text.EndsWith("に") || wordInfos[i - 1].Text.EndsWith("と"))
                && (i + 1 >= wordInfos.Count
                    || wordInfos[i + 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol
                        or PartOfSpeech.Symbol or PartOfSpeech.BlankSpace))
            {
                word.PartOfSpeech = PartOfSpeech.Verb;
                word.DictionaryForm = "ある";
            }

            // 捩* (モジ*) → ネジ* — standalone 捩る is almost always ねじる (to twist);
            // もじる (to parody) is rare and typically written in kana.
            if (word.DictionaryForm == "捩る" && word.Reading.StartsWith("モジ"))
                word.Reading = word.Reading.Replace("モジ", "ネジ");

            // 大勢 (タイセイ, general trend) → オオゼイ (many people) — the common reading.
            // タイセイ reading only in set phrases like 大勢に影響がない.
            if (word is { Text: "大勢", Reading: "タイセイ" })
            {
                var next = i + 1 < wordInfos.Count ? wordInfos[i + 1] : null;
                var next2 = i + 2 < wordInfos.Count ? wordInfos[i + 2] : null;
                bool isTaiseiContext = next is { Text: "に" } && next2 is { DictionaryForm: "影響" };
                if (!isTaiseiContext)
                    word.Reading = "オオゼイ";
            }

            // 大仰 (オオノキ, place name) → オオギョウ (exaggerated, adj-na).
            // The place name reading is rare; the adjective is by far the common reading in prose.
            if (word is { Text: "大仰", Reading: "オオノキ" })
            {
                word.Reading = "オオギョウ";
                word.PartOfSpeech = PartOfSpeech.NaAdjective;
            }

            // イキ (katakana) → 行く, not 生きる. Sudachi maps katakana イキ to dict=イキる/norm=生きる,
            // but standalone katakana イキ is slang for 行く (イク). 生きる is never written as イキ.
            // After CombineAuxiliary, the token may be イキました/イキます etc.
            if (word.DictionaryForm == "イキる" && word.Text.StartsWith("イキ"))
            {
                word.DictionaryForm = "行く";
                word.NormalizedForm = "行く";
            }

            // いける (kana verb): default to 行ける (1631370 "to be good; go well") — the overwhelmingly
            // common bare いける. Switch to 生ける (1587190 "to arrange flowers") only with a flower
            // object nearby (花をいける). Sudachi gives dict=いける for all senses (生ける/活ける/埋ける/
            // 行ける); the 花 object is the disambiguator.
            if (word is { Text: "いける", DictionaryForm: "いける" })
            {
                var prevTok = i > 0 ? wordInfos[i - 1] : null;
                var prev2Tok = i >= 2 ? wordInfos[i - 2] : null;
                bool flowerContext =
                    (prevTok != null && (prevTok.Text.Contains('花') || prevTok.Text.Contains('華')))
                    || (prev2Tok != null && (prev2Tok.Text.Contains('花') || prev2Tok.Text.Contains('華')));
                // Set DictionaryForm too, not just PreMatchedWordId: the DeckWord cache keys on
                // (Text, POS, DictForm, Reading) and is context-blind, so both senses must produce
                // distinct cache keys or whichever parses first in a deck wins for all いける.
                word.PreMatchedWordId = flowerContext ? 1587190 : 1631370;
                word.DictionaryForm = flowerContext ? "生ける" : "行ける";
            }

            // ツイてる/ツイてない/ツイてた (katakana ツイ + てる) is the colloquial 付いてる "to be lucky"
            // (1894260, uk). The grammatical ついて "about" (1854750) is never written in katakana, so the
            // katakana ツイ head is an unambiguous signal (mirrors the ノリ/セン/イキ katakana rules above).
            if (word.Text.StartsWith("ツイて", StringComparison.Ordinal))
            {
                word.PreMatchedWordId = 1894260;
                word.DictionaryForm = "ツイてる";
                // The PreMatched path bypasses deconjugation, so the conjugation chain must be set
                // explicitly or non-present forms render as the bare lemma (ツイてた = past, not ツイてる).
                word.PreMatchedConjugations = word.Text["ツイて".Length..] switch
                {
                    "なかった" => ["negative", "past"],
                    "ない" => ["negative"],
                    "た" => ["past"],
                    _ => word.PreMatchedConjugations
                };
            }

            // 弾ける: Sudachi gives dict=弾ける for both はじける (to burst) and the potential of
            // 弾く/ひく (to play). The reading disambiguates: ヒケ* = 弾く potential, ハジケ* = 弾ける.
            if (word.DictionaryForm == "弾ける" && word.Reading.StartsWith("ヒケ"))
            {
                word.DictionaryForm = "弾く";
                word.NormalizedForm = "弾く";
            }

            // 来る: Sudachi sometimes classifies modern くる as archaic きたる (文語四段-ラ行),
            // giving NormalizedForm=来たる and Reading=キタル. This causes the scorer to favor
            // the きたる entry (1591270) over くる (1547720) via ReadingMatchScore.
            // Handles two patterns:
            //   1. Bare 来る/来 with NormalizedForm=来たる (Sudachi 文語四段 misclassification)
            //   2. Combined tokens like 来たー/来た where Reading=キタ… from concatenation
            //      (Sudachi correctly said カ行変格 for 来, but combined reading キタ… matches きたる)
            // Preserve genuine archaic forms: 来り (continuative unique to きたる) is excluded.
            if (word.NormalizedForm == "来たる" && word.Text is "来る" or "来")
            {
                word.Reading = word.Reading.Replace("キタ", "ク");
                word.NormalizedForm = "来る";
                word.DictionaryForm = "来る";
            }
            else if (word.Text.Length >= 2 && word.Text[0] == '来' && word.Text[1] != 'り'
                     && word.DictionaryForm == "来る" && word.Reading.StartsWith("キタ"))
            {
                word.Reading = "ク" + word.Reading[2..];
            }

            // Clause-initial よって、 is the conjunction 因って "therefore" (1605970), not the te-form
            // of 依る/因る "to depend on" (1168660). Mid-clause よって (場合によって) keeps the verb.
            // Setting DictionaryForm too keeps the context-blind DeckWord cache from collapsing both.
            if (word is { Text: "よって" } && word.DictionaryForm is "依る" or "因る" or "よる"
                && (i == 0 || wordInfos[i - 1].PartOfSpeech is PartOfSpeech.SupplementarySymbol
                    or PartOfSpeech.Symbol or PartOfSpeech.BlankSpace)
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
