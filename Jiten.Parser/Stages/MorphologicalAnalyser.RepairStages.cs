using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Utils;
using WanaKanaShaapu;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    // The verb heads a quotative って attaches to (言う/思う/聞く/考える/感じる); the datte-quote
    // rewrite rule keys on the same class.
    internal static readonly char[] QuoteVerbHeads = ['言', '思', '聞', '考', '感'];

    // The geminate suffixes 〜っぱなし/っぷり/っぽい attach to a verb 連用形 or noun, and Sudachi
    // routinely lets the っ (and sometimes more) bleed into the neighbours: 流|しっ|ぱなし
    // (しっ→知る), 上|が|りっぱ|な|し (りっぱ→立派), 脳天|気っぷ|り (気っぷ→気風), 皮肉っ|ぽい
    // (→皮肉る). Re-cut so っ heads its suffix, pin the suffix entry, and reassemble the stem
    // leftward while the concatenation deconjugates to an attested verb (上+が+り→上がり).
    private static readonly Dictionary<string, (string Dict, int Pin)> GeminateSuffixes = new()
    {
        ["ぱなし"] = ("っぱなし", 1008020),
        ["ぷり"] = ("っぷり", 2202980),
        ["ぽい"] = ("っぽい", 2083720),
        ["ぽく"] = ("っぽい", 2083720),
        ["ぽさ"] = ("っぽい", 2083720),
    };

    private List<WordInfo> RepairGeminateSuffixTheft(List<WordInfo> wordInfos, IReadOnlyList<int> candidates)
    {
        if (wordInfos.Count < 2) return wordInfos;

        var deconj = Deconjugator.Instance;

        // Extend the stem when the concatenation is a verb renyoukei AND either attests as a
        // surface itself (上がり, こもり) or the previous token is an orphaned kanji fragment
        // (失+い, 打+ち). A complete kana word before the stem (ワクワク+し) stays split so し
        // resolves as する through the normal machinery.
        bool StemExtends(WordInfo prev, string candidate)
        {
            if (candidate.Length == 0) return false;
            var forms = deconj.Deconjugate(NormalizeToHiragana(candidate));
            bool isVerbStem = forms.Any(f => f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal))
                                             && HasVerbOrAdjectiveLookup?.Invoke(f.Text) == true);
            if (!isVerbStem) return false;
            if (HasNonNameCompoundLookup?.Invoke(candidate) == true) return true;
            return prev.Text.Any(JapaneseTextHelper.IsKanji);
        }

        List<WordInfo>? list = null;
        int indexDelta = 0;
        int skipOriginalThrough = -1;
        foreach (int originalIndex in candidates)
        {
            if (originalIndex <= skipOriginalThrough) continue;
            var toks = list ?? wordInfos;
            int i = originalIndex + indexDelta;
            if ((uint)i >= (uint)toks.Count) continue;
            var t = toks[i];
            string? stemText = null, suffixText = null;
            int consumed = 0;

            // The stem left of っ must be renyoukei-shaped (kanji, or i/e-row kana final) or a
            // noun-ish kanji — a-row finals (やっ|ぱなし = やっぱ+なし) are never the suffix host.
            static bool CanHostGeminateSuffix(string stem) =>
                stem.Length > 0 && (JapaneseTextHelper.IsKanji(stem[^1])
                    || "いきしちにひみりぎじぢびぴえけせてねへめれげぜでべぺ".Contains(stem[^1]));

            if (t.Text.Length > 1 && t.Text.EndsWith('っ') && i + 1 < toks.Count
                && GeminateSuffixes.ContainsKey(toks[i + 1].Text)
                && CanHostGeminateSuffix(t.Text[..^1]))
            {
                stemText = t.Text[..^1];
                suffixText = "っ" + toks[i + 1].Text;
                consumed = 2;
            }
            else if (t.Text.Length > 2 && t.Text.EndsWith("っぱ", StringComparison.Ordinal)
                     && i + 1 < toks.Count
                     && (toks[i + 1].Text == "なし"
                         || (i + 2 < toks.Count && toks[i + 1].Text == "な" && toks[i + 2].Text == "し"))
                     && CanHostGeminateSuffix(t.Text[..^2]))
            {
                stemText = t.Text[..^2];
                suffixText = "っぱなし";
                consumed = toks[i + 1].Text == "なし" ? 2 : 3;
            }
            else if (t.Text.Length > 2 && t.Text.EndsWith("っぷ", StringComparison.Ordinal)
                     && i + 1 < toks.Count && toks[i + 1].Text == "り"
                     && CanHostGeminateSuffix(t.Text[..^2]))
            {
                stemText = t.Text[..^2];
                suffixText = "っぷり";
                consumed = 2;
            }

            if (stemText == null) continue;

            list ??= new List<WordInfo>(wordInfos);
            skipOriginalThrough = originalIndex + consumed - 1;
            var (suffixDict, suffixPin) = GeminateSuffixes[suffixText!.TrimStart('っ')];
            var last = list[i + consumed - 1];

            var stem = new WordInfo(t)
            {
                Text = stemText,
                DictionaryForm = stemText,
                NormalizedForm = stemText,
                Reading = t.Reading != null && t.Reading.Length > t.Text.Length - stemText.Length
                    ? t.Reading[..^(t.Text.Length - stemText.Length)]
                    : "",
                EndOffset = t.StartOffset >= 0 ? t.StartOffset + stemText.Length : -1,
                PartOfSpeech = PartOfSpeech.Noun,
            };
            var suffix = new WordInfo
            {
                Text = suffixText,
                DictionaryForm = suffixDict,
                NormalizedForm = suffixDict,
                Reading = WanaKana.ToKatakana(suffixText),
                PartOfSpeech = PartOfSpeech.Suffix,
                StartOffset = stem.EndOffset,
                EndOffset = last.EndOffset,
                PreMatchedWordId = suffixPin,
                HardPinned = true,
            };

            list.RemoveRange(i, consumed);
            list.Insert(i, suffix);
            list.Insert(i, stem);

            // Reassemble the stem leftward while it still deconjugates to an attested verb
            // (上+が+り→上がり→上がる); stop before over-reaching (を+飛ばし fails the test).
            int merges = 0;
            int stemIdx = i;
            while (stemIdx > 0 && merges < 3)
            {
                var prev = list[stemIdx - 1];
                var cand = prev.Text + list[stemIdx].Text;
                if (!StemExtends(prev, cand)) break;
                var merged = new WordInfo(list[stemIdx])
                {
                    Text = cand,
                    DictionaryForm = cand,
                    NormalizedForm = cand,
                    Reading = (prev.Reading ?? "") + (list[stemIdx].Reading ?? ""),
                    StartOffset = prev.StartOffset,
                };
                list[stemIdx] = merged;
                list.RemoveAt(stemIdx - 1);
                stemIdx--;
                merges++;
            }

            indexDelta = list.Count - wordInfos.Count;
        }

        return list ?? wordInfos;
    }

    // True when the surface cuts into two attested words (ハロー|ワーク): such a blob belongs
    // to the resegmentation lattice, which scores the cut properly.
    private bool HasAttestedBipartition(string text)
    {
        for (int cutAt = 2; cutAt <= text.Length - 2; cutAt++)
        {
            if (HasNonNameCompoundLookup?.Invoke(text[..cutAt]) == true
                && HasNonNameCompoundLookup?.Invoke(text[cutAt..]) == true)
                return true;
        }

        return false;
    }

    // Sudachi shreds OOV katakana compounds into coincidentally-attested fragments (ポン|コツ,
    // ホロ|グラフ "graph", ドシ|ロウト "funnel", コーポ|レイ|テッド). Re-merge a contiguous
    // katakana run when the whole attests, or when any piece is fragment-shaped (≤2 chars or
    // unattested) — the merged span then resolves or fails as one unit downstream instead of
    // the fragments matching junk separately. Two genuine long words meeting (システム|エラー)
    // stay separate.
    private List<WordInfo> RepairKatakanaShreds(List<WordInfo> wordInfos, IReadOnlyList<int> candidates)
    {
        if (wordInfos.Count == 0) return wordInfos;

        static bool IsKatakanaRunToken(WordInfo w) =>
            w.Text.Length > 0 && w.PreMatchedWordId == null
            && !w.IsPersonNameContext
            && !PosMapper.IsNameLikeSudachiNoun(w.PartOfSpeech, w.PartOfSpeechSection1,
                w.PartOfSpeechSection2, w.PartOfSpeechSection3)
            && w.Text.All(JapaneseTextHelper.IsKatakanaWordChar);

        List<WordInfo>? list = null;
        int indexDelta = 0;
        int skipOriginalThrough = -1;
        foreach (int originalIndex in candidates)
        {
            if (originalIndex <= skipOriginalThrough) continue;
            var toks = list ?? wordInfos;
            int i = originalIndex + indexDelta;
            // A sentence-final token has no run to extend but can still take the tail-word split.
            if (i < 0 || i >= toks.Count) continue;

            if (!IsKatakanaRunToken(toks[i])) continue;

            // Runs must be offset-contiguous in the source text: stripped markup leaves
            // unrelated tokens adjacent ([name]アリサ[line]“リア充 → アリサ|リア充), and merging
            // across that gap manufactures アリサリア.
            static bool Contiguous(WordInfo a, WordInfo b) =>
                a.EndOffset >= 0 && b.StartOffset >= 0 && a.EndOffset == b.StartOffset;

            int end = i;
            while (end + 1 < toks.Count && end - i < 4 && IsKatakanaRunToken(toks[end + 1])
                   && Contiguous(toks[end], toks[end + 1]))
                end++;

            // A katakana-headed mixed token continues the run (ブロー|アップされる, クオン|ツが):
            // the head belongs to the compound, the tail is grammar to split back off.
            string mixedTail = "";
            int mixedIdx = -1;
            if (end + 1 < toks.Count && end - i < 4 && toks[end + 1].PreMatchedWordId == null
                && Contiguous(toks[end], toks[end + 1]))
            {
                var cand = toks[end + 1].Text;
                int kl = 0;
                while (kl < cand.Length && JapaneseTextHelper.IsKatakanaWordChar(cand[kl])) kl++;
                // The tail must be grammar (hiragana される/が) — a kanji tail means the token is
                // its own compound (リア充), not a shredded head plus grammar.
                if (kl >= 1 && kl < cand.Length && cand[kl..].All(c => c is >= 'ぁ' and <= 'ゖ'))
                {
                    mixedIdx = end + 1;
                    mixedTail = cand[kl..];
                }
            }

            // A single unattested blob can still hold a real loanword at its tail (an OOV name
            // fused with レベル). Split the longest attested ≥3-char tail off when the remaining
            // head is itself unattested — an attested head would mean cutting a plausible whole
            // into two coincidental words, which drops whole instead. The head must be ≥4 chars
            // to be confidently its own name unit; a shorter head defers to the resegmentation
            // lattice, which keeps name candidates.
            if (end == i && mixedIdx < 0)
            {
                var text = toks[i].Text;
                if (text.Length >= 7 && HasNonNameCompoundLookup?.Invoke(text) != true
                    && !HasAttestedBipartition(text))
                {
                    for (int headLen = 4; headLen <= text.Length - 3; headLen++)
                    {
                        var head = text[..headLen];
                        var tail = text[headLen..];
                        if (HasNonNameCompoundLookup?.Invoke(tail) != true
                            || HasNonNameCompoundLookup?.Invoke(head) == true)
                            continue;

                        list ??= new List<WordInfo>(wordInfos);
                        var source = list[i];
                        int cut = source.StartOffset >= 0 ? source.StartOffset + headLen : -1;
                        var headToken = new WordInfo(source)
                        {
                            Text = head, DictionaryForm = head, NormalizedForm = head,
                            Reading = source.Reading is { Length: > 0 } r && r.Length >= headLen
                                ? r[..headLen] : head,
                            EndOffset = cut,
                        };
                        var tailToken = new WordInfo(source)
                        {
                            Text = tail, DictionaryForm = tail, NormalizedForm = tail,
                            Reading = source.Reading is { Length: > 0 } r2 && r2.Length >= headLen
                                ? r2[headLen..] : tail,
                            StartOffset = cut,
                        };
                        list[i] = headToken;
                        list.Insert(i + 1, tailToken);
                        skipOriginalThrough = originalIndex;
                        indexDelta = list.Count - wordInfos.Count;
                        break;
                    }
                }

                continue;
            }

            string full = string.Concat(toks.Skip(i).Take(end - i + 1).Select(t => t.Text));
            if (mixedIdx >= 0)
                full += toks[mixedIdx].Text[..^mixedTail.Length];
            bool wholeAttested = HasNonNameCompoundLookup?.Invoke(full) == true;
            bool anyFragment = mixedIdx >= 0;
            for (int j = i; j <= end && !anyFragment; j++)
                anyFragment = toks[j].Text.Length <= 2
                              || HasNonNameCompoundLookup?.Invoke(toks[j].Text) != true;

            if (!wholeAttested && !anyFragment)
            {
                skipOriginalThrough = originalIndex + end - i;
                continue;
            }

            // An attested long tail piece is its own word, not shred material: a shredded OOV name
            // ending in レベル merges only the unattested head and keeps レベル. Only while at
            // least two pieces remain to merge — a two-piece run of coincidentally-attested halves
            // is name material that must still merge whole and drop, not leave its tail behind.
            if (!wholeAttested && mixedIdx < 0)
            {
                while (end > i + 1 && toks[end].Text.Length >= 3
                       && HasNonNameCompoundLookup?.Invoke(toks[end].Text) == true)
                    end--;

                full = string.Concat(toks.Skip(i).Take(end - i + 1).Select(t => t.Text));
                wholeAttested = HasNonNameCompoundLookup?.Invoke(full) == true;
            }

            list ??= new List<WordInfo>(wordInfos);
            int last = mixedIdx >= 0 ? mixedIdx : end;
            int mergedEnd = mixedIdx >= 0
                ? (list[mixedIdx].EndOffset >= 0 ? list[mixedIdx].EndOffset - mixedTail.Length : -1)
                : list[end].EndOffset;
            var merged = new WordInfo(list[i])
            {
                Text = full,
                DictionaryForm = full,
                NormalizedForm = full,
                Reading = string.Concat(list.Skip(i).Take(end - i + 1).Select(t => t.Reading ?? ""))
                          + (mixedIdx >= 0 ? full[^(list[mixedIdx].Text.Length - mixedTail.Length)..] : ""),
                PartOfSpeech = PartOfSpeech.Noun,
                EndOffset = mergedEnd,
            };
            list.RemoveRange(i, last - i + 1);
            list.Insert(i, merged);
            if (mixedIdx >= 0)
                list.InsertRange(i + 1, TokenizeGrammarRemainder(mixedTail, mergedEnd));
            skipOriginalThrough = originalIndex + last - i;
            indexDelta = list.Count - wordInfos.Count;
        }

        return list ?? wordInfos;
    }

    // Sudachi's lattice steals compound boundaries in two recurring shapes: a location suffix
    // pulls the compound's last kanji into itself (滑走|路上 — 上 belongs to 滑走路), and a
    // demonstrative expression eats the next compound's first kanji (その時|系列 — 時 belongs
    // to 時系列). Both re-cut on attestation of the corrected pieces.
    // Location/relational suffixes plus 的: all attach to the FULL preceding compound
    // (滑走路+上, 消去法+的), so a 2-char token headed by a stolen kanji re-cuts.
    private static readonly HashSet<char> LocationSuffixChars =
        ['上', '中', '内', '外', '前', '後', '間', '下', '先', '際', '的'];

    // B must outrank the extended compound by this factor before its own-word reading beats the
    // theft re-cut — the tuned cutoff separating 日本|国内 (stays) from 滑走|路上 (re-cuts).
    private const int BoundaryTheftRankDominanceFactor = 4;

    private static readonly string[] DemonstrativeHeads = ["その", "この", "あの", "どの"];

    // Sudachi splits a conjugated verb's kanji head from its kana tail, lemmatising both as
    // unrelated words: 会|おう (会+王), 信|じろ (信+囲炉裏?), 歩|こう, 過|ごせ, 見|直し. When the
    // concatenation deconjugates in one step to an attested verb that starts with the kanji,
    // the split is spurious — merge back as that verb.
    private List<WordInfo> RepairKanjiVerbShred(List<WordInfo> wordInfos, IReadOnlyList<int> candidates)
    {
        if (wordInfos.Count < 2) return wordInfos;

        var deconj = Deconjugator.Instance;

        List<WordInfo>? list = null;
        int indexDelta = 0;
        foreach (int originalIndex in candidates)
        {
            var toks = list ?? wordInfos;
            int i = originalIndex + indexDelta;
            if (i < 0 || i + 1 >= toks.Count) continue;
            var k = toks[i];
            var tail = toks[i + 1];
            if (k.PreMatchedWordId != null) continue;
            if (k.Text.Length != 1 || !JapaneseTextHelper.IsKanji(k.Text[0])) continue;
            // 何 heads no verb — its kana continuations are always their own words (何|かって).
            if (k.Text == "何") continue;

            if (TryShiftStrandedOkurigana(ref list, wordInfos, i, ref indexDelta)) continue;

            toks = list ?? wordInfos;
            k = toks[i];
            tail = toks[i + 1];
            if (tail.PreMatchedWordId != null) continue;
            if (k.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun)) continue;
            // A clause-initial single kanji is speaker-name territory in script dumps
            // ([name]至[line]“そうなん?” would merge into 至る).
            if (i == 0 || toks[i - 1].PartOfSpeech is PartOfSpeech.Symbol
                or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace) continue;
            if (tail.Text.Length is < 1 or > 4 || !tail.Text.All(c => c is >= 'ぁ' and <= 'ゖ')) continue;
            if (tail.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary
                or PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol) continue;
            // A copula tail belongs to the sentence (目|だった must not become 目立った), and a
            // dictionary-form verb standing complete is its own word (今|いる).
            if (tail.DictionaryForm is "だ" or "です"
                || tail.Text is "だ" or "だっ" or "だった" or "だったら" or "で" or "です" or "でし"
                    or "でした" or "だろ" or "だろう" or "じゃ" or "じゃない" or "じゃなく"
                    // する-negatives after a noun are noun+しない (話|しないで), never a shred.
                    or "しない" or "しないで" or "しなく" or "せず") continue;
            // A bare し before ない is the same する-negative one token earlier (話|し|ないで).
            if (tail.Text == "し" && i + 2 < toks.Count
                && toks[i + 2].Text.StartsWith("な", StringComparison.Ordinal)) continue;
            if (tail.PartOfSpeech == PartOfSpeech.Verb && tail.Text == tail.DictionaryForm) continue;

            var cand = k.Text + tail.Text;
            var form = deconj.Deconjugate(cand).FirstOrDefault(f =>
                f.Text.Length > 1 && f.Text != cand
                && f.Text.StartsWith(k.Text, StringComparison.Ordinal)
                && f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal))
                && f.Process.Length > 0
                && HasNonNameCompoundLookup?.Invoke(f.Text) == true);
            if (form == null) continue;

            list ??= new List<WordInfo>(wordInfos);
            list[i] = new WordInfo(k)
            {
                Text = cand,
                DictionaryForm = form.Text,
                NormalizedForm = form.Text,
                Reading = "",
                PartOfSpeech = PartOfSpeech.Verb,
                EndOffset = tail.EndOffset,
            };
            list.RemoveAt(i + 1);
            indexDelta--;
        }

        return list ?? wordInfos;
    }

    // A sokuon contraction right after a kanji verb stem (っス, った, って, っつ) makes Sudachi cut
    // every boundary of the run one character early: the stem's okurigana is swallowed by the
    // contraction and the kanji is left bare (探|しっス, 探|すった|って, 捜|しっ|ス). Give the okurigana
    // back and shift the run's boundaries right by one, which restores stem and contraction together
    // (探し|っス, 探す|った|って, 捜し|っス). Gated on the second character being the sokuon — that is what
    // marks the cut as the contraction's doing rather than a real boundary — and on the stem plus its
    // okurigana resolving to an attested verb.
    private bool TryShiftStrandedOkurigana(ref List<WordInfo>? list, List<WordInfo> wordInfos, int i, ref int indexDelta)
    {
        var toks = list ?? wordInfos;
        var k = toks[i];

        int end = i;
        int runLength = 0;
        while (end + 1 < toks.Count && end - i < 3)
        {
            var t = toks[end + 1];
            if (t.PreMatchedWordId != null || t.Text.Length == 0) break;
            if (!t.Text.All(c => c is (>= 'ぁ' and <= 'ゖ') or (>= 'ァ' and <= 'ヺ'))) break;
            if (runLength + t.Text.Length > 4) break;
            end++;
            runLength += t.Text.Length;
            if (t.Text[^1] is not ('っ' or 'ッ')) break;
        }

        if (end == i || runLength < 2) return false;

        string run = string.Concat(toks.Skip(i + 1).Take(end - i).Select(t => t.Text));
        // The okurigana is a full mora; a small kana leading the run means the sokuon is the stem's
        // own (真|っ赤), not a stolen boundary.
        if (run[0] is 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' or 'っ' or 'ゃ' or 'ゅ' or 'ょ' or 'ゎ'
            || run[0] is not (>= 'ぁ' and <= 'ゖ')) return false;
        if (run[1] is not ('っ' or 'ッ')) return false;

        var stem = k.Text + run[0];
        var dictionaryForm = ResolveVerbStem(stem, k.Text);
        if (dictionaryForm == null) return false;

        list ??= [..wordInfos];
        int offset = k.EndOffset >= 0 ? k.EndOffset + 1 : -1;
        list[i] = new WordInfo(k)
        {
            Text = stem,
            DictionaryForm = dictionaryForm,
            NormalizedForm = dictionaryForm,
            Reading = "",
            PartOfSpeech = PartOfSpeech.Verb,
            EndOffset = offset,
        };

        var shifted = new List<WordInfo>(end - i);
        int taken = 1;
        for (int j = i + 1; j <= end && taken < run.Length; j++)
        {
            int len = Math.Min(toks[j].Text.Length, run.Length - taken);
            var text = run.Substring(taken, len);
            shifted.Add(new WordInfo(toks[j])
            {
                Text = text,
                DictionaryForm = text,
                NormalizedForm = text,
                Reading = "",
                // A shifted って can only be the quotative: the contraction is what stole the mora.
                PartOfSpeech = text == "って" ? PartOfSpeech.Particle : toks[j].PartOfSpeech,
                PartOfSpeechSection1 = text == "って" ? PartOfSpeechSection.AdverbialParticle : toks[j].PartOfSpeechSection1,
                StartOffset = offset,
                EndOffset = offset >= 0 ? offset + len : -1,
            });
            offset = offset >= 0 ? offset + len : -1;
            taken += len;
        }

        list.RemoveRange(i + 1, end - i);
        list.InsertRange(i + 1, shifted);
        indexDelta += shifted.Count - (end - i);
        return true;
    }

    // The stem's dictionary form when kanji + okurigana is a verb: either a dictionary-form verb
    // itself (探す) or one deconjugation step off one (探し, 探せ → 探す).
    private string? ResolveVerbStem(string stem, string kanji)
    {
        if (stem[^1] is 'う' or 'く' or 'ぐ' or 'す' or 'つ' or 'ぬ' or 'ぶ' or 'む' or 'る'
            && HasNonNameCompoundLookup?.Invoke(stem) == true
            && DeconjugatesToVerb(stem))
            return stem;

        foreach (var f in Deconjugator.Instance.Deconjugate(stem))
        {
            if (f.Process.Length != 1 || f.Text.Length <= 1 || f.Text == stem) continue;
            if (!f.Text.StartsWith(kanji, StringComparison.Ordinal)) continue;
            if (!f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal))) continue;
            if (HasNonNameCompoundLookup?.Invoke(f.Text) == true) return f.Text;
        }

        return null;
    }

    private List<WordInfo> RepairCompoundBoundaryTheft(List<WordInfo> wordInfos, IReadOnlyList<int> candidates)
    {
        if (wordInfos.Count < 2) return wordInfos;

        List<WordInfo>? list = null;
        foreach (int i in candidates)
        {
            var toks = list ?? wordInfos;
            if (i < 0 || i + 1 >= toks.Count) continue;
            var a = toks[i];
            var b = toks[i + 1];
            if (a.PreMatchedWordId != null || b.PreMatchedWordId != null) continue;

            // [滑走][路上] → [滑走路][上], [小][動物的] → [小動物][的]: B is a 2–3 char word whose
            // last char is a compound-final suffix, and A's extension by B's head attests.
            if (b.Text.Length is 2 or 3 && b.Text != "時間" && LocationSuffixChars.Contains(b.Text[^1])
                && b.Text[..^1].All(JapaneseTextHelper.IsKanji)
                && a.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Prefix
                && b.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.NaAdjective
                && a.Text.Length >= 1 && a.Text.All(JapaneseTextHelper.IsKanji)
                && (a.Text.Length >= 2 || a.PartOfSpeech == PartOfSpeech.Prefix)
                // A numeral head means B is a duration/counter word (二十|時間), never a theft.
                && !a.Text.All(JapaneseTextHelper.IsNumeralChar)
                && HasNonNameCompoundLookup?.Invoke(a.Text + b.Text[..^1]) == true
                // B is a genuine constituent when it heads its own compound with what follows
                // (人類|史上|初 — 史上初 attests; 滑走|路上|で does not).
                && !(i + 2 < toks.Count
                     && HasNonNameCompoundLookup?.Invoke(b.Text + toks[i + 2].Text) == true)
                // A much more frequent B is its own word, not a theft (日本|国内 stays: 国内
                // outranks 日本国 by far; 滑走|路上 re-cuts: 滑走路 holds its own against 路上).
                && !(GetNonNameCompoundFrequencyRank?.Invoke(b.Text) is int bRank
                     && GetNonNameCompoundFrequencyRank?.Invoke(a.Text + b.Text[..^1]) is var extRank
                     && (extRank == null || bRank * BoundaryTheftRankDominanceFactor < extRank.Value)))
            {
                var extended = a.Text + b.Text[..^1];
                var suffix = b.Text[^1..];
                int extendedEnd = a.EndOffset >= 0 ? a.EndOffset + b.Text.Length - 1 : -1;

                list ??= new List<WordInfo>(wordInfos);
                list[i] = new WordInfo(a)
                {
                    Text = extended,
                    DictionaryForm = extended,
                    NormalizedForm = extended,
                    Reading = "",
                    PartOfSpeech = PartOfSpeech.Noun,
                    EndOffset = extendedEnd,
                };
                list[i + 1] = new WordInfo(b)
                {
                    Text = suffix,
                    DictionaryForm = suffix,
                    NormalizedForm = suffix,
                    Reading = "",
                    PartOfSpeech = PartOfSpeech.Suffix,
                    StartOffset = extendedEnd,
                };
                continue;
            }

            // [その時][系列] → [その][時系列]: a demonstrative expression whose trailing kanji +
            // the next noun attests as a compound.
            if (a.PartOfSpeech == PartOfSpeech.Expression && a.Text.Length == 3
                && DemonstrativeHeads.Contains(a.Text[..2])
                && JapaneseTextHelper.IsKanji(a.Text[2])
                && b.Text.Length >= 1 && b.Text.All(JapaneseTextHelper.IsKanji)
                && b.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && HasNonNameCompoundLookup?.Invoke(a.Text[2] + b.Text) == true)
            {
                var stolen = a.Text[2];
                var demonstrative = a.Text[..2];
                var extendedNoun = stolen + b.Text;

                list ??= new List<WordInfo>(wordInfos);
                list[i] = new WordInfo(a)
                {
                    Text = demonstrative,
                    DictionaryForm = demonstrative,
                    NormalizedForm = demonstrative,
                    // The demonstrative is always two kana, so its reading is the first two katakana —
                    // the stolen kanji's reading length varies (時=トキ, 日=ヒ) and can't be trimmed off.
                    Reading = a.Reading is { Length: >= 2 } ar ? ar[..2] : "",
                    PartOfSpeech = PartOfSpeech.PrenounAdjectival,
                    EndOffset = a.EndOffset >= 0 ? a.EndOffset - 1 : -1,
                };
                list[i + 1] = new WordInfo(b)
                {
                    Text = extendedNoun,
                    DictionaryForm = extendedNoun,
                    NormalizedForm = extendedNoun,
                    Reading = "",
                    StartOffset = b.StartOffset >= 0 ? b.StartOffset - 1 : -1,
                };
            }
        }

        return list ?? wordInfos;
    }

    // An OOV compound verb Sudachi normalises to X返る (のさばり返る) — the intensifier suffix 返る on a
    // JMDict verb stem — has no JMDict entry of its own and drops whole. Split it into the head verb +
    // 返る so both resolve; a following て is folded into 返って here so the te-form is not later mis-read
    // as a quotative って. A genuine X返る (静まり返る, 呆れ返る) is a JMDict compound and is excluded by the
    // lookup gate, so only the OOV coinages reach the split.
    private List<WordInfo> RepairIntensifierKaeru(List<WordInfo> wordInfos) =>
        HasNonNameCompoundLookup == null ? wordInfos : ScanRewrite(wordInfos, TryRepairIntensifierKaeru);

    private int TryRepairIntensifierKaeru(List<WordInfo> tokens, int i, List<WordInfo>? _, Func<List<WordInfo>> output)
    {
        var w = tokens[i];
        if (!(w.PartOfSpeech == PartOfSpeech.Verb && w.PreMatchedWordId == null
              && w.NormalizedForm.Length >= 4 && w.Text.Length >= 3
              && w.NormalizedForm.EndsWith("返る", StringComparison.Ordinal)
              && !HasNonNameCompoundLookup!(w.NormalizedForm)))
            return 0;

        // The head is the normalised form minus 返る (a renyoukei, kana-stable): のさばり → のさばる.
        var headStem = w.NormalizedForm[..^2];
        string? headDict = null;
        foreach (var f in Deconjugator.Instance.Deconjugate(headStem))
            if (f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) && HasNonNameCompoundLookup(f.Text))
            { headDict = f.Text; break; }

        // The surface must open with that same kana head (のさばりかえっ = のさばり + かえっ).
        if (headDict == null || !w.Text.StartsWith(headStem, StringComparison.Ordinal)
            || w.Text.Length <= headStem.Length)
            return 0;

        var result = output();
        var suffixSurface = w.Text[headStem.Length..];
        int cut = w.StartOffset >= 0 ? w.StartOffset + headStem.Length : -1;
        result.Add(new WordInfo(w)
        {
            Text = headStem, DictionaryForm = headDict, NormalizedForm = headDict,
            Reading = "", PartOfSpeech = PartOfSpeech.Verb, EndOffset = cut
        });
        // 返っ + a following て/た → the single 返って/返った (pinned so it is not read as 却って).
        if (suffixSurface.EndsWith("っ", StringComparison.Ordinal)
            && i + 1 < tokens.Count && tokens[i + 1].Text is "て" or "た")
        {
            result.Add(new WordInfo(w)
            {
                Text = suffixSurface + tokens[i + 1].Text, DictionaryForm = "返る", NormalizedForm = "返る",
                Reading = "", PartOfSpeech = PartOfSpeech.Verb, PreMatchedWordId = 1512150,
                // The chain is recovered against kana かえる: deconjugation is kana-side,
                // so the kanji lemma would never match its output forms.
                PreMatchedConjugations = PinnedConjugationProcess(suffixSurface + tokens[i + 1].Text, "かえる"),
                StartOffset = cut, EndOffset = tokens[i + 1].EndOffset
            });
            return 2;
        }

        result.Add(new WordInfo(w)
        {
            Text = suffixSurface, DictionaryForm = "返る", NormalizedForm = "返る",
            Reading = "", PartOfSpeech = PartOfSpeech.Verb, PreMatchedWordId = 1512150,
            PreMatchedConjugations = PinnedConjugationProcess(suffixSurface, "かえる"),
            StartOffset = cut, EndOffset = w.EndOffset
        });
        return 1;
    }

    // 段飛ばし (2746000, "skipping steps") is a lexicalised counter-compound. A preceding numeral
    // greedily claims 段 — 三|段|飛ばし and Sudachi's fused 一段|飛ばし both leave 飛ばし stranded on the
    // securities-fraud noun (1637130) — so reform the compound with 段 as its head: 三|段飛ばし,
    // 一|段飛ばし. Scoped to the attested 飛ばし compound (a blanket 段+X rule would split 三段跳び).
    private List<WordInfo> RepairDanTobashi(List<WordInfo> wordInfos) =>
        ScanRewrite(wordInfos, static (tokens, i, _, output) =>
        {
            var w = tokens[i];
            if (!(i + 1 < tokens.Count && tokens[i + 1] is { Text: "飛ばし" } b
                  && w.PreMatchedWordId == null && b.PreMatchedWordId == null
                  && w.Text.EndsWith("段", StringComparison.Ordinal)))
                return 0;

            var head = w.Text[..^1];
            // Standalone 段 (三|段|飛ばし): merge 段+飛ばし; the numeral before it is left untouched.
            if (head.Length == 0)
            {
                output().Add(new WordInfo(b)
                {
                    Text = "段飛ばし", DictionaryForm = "段飛ばし", NormalizedForm = "段飛ばし",
                    Reading = "ダントバシ", PartOfSpeech = PartOfSpeech.Noun,
                    StartOffset = w.StartOffset, EndOffset = b.EndOffset, PreMatchedWordId = 2746000
                });
                return 2;
            }
            // Numeral+段 fused by Sudachi (一段|飛ばし): release 段 to the compound → 一 + 段飛ばし.
            // 何/数 head 何段/数段 ("how many / several steps"), so admit them alongside true numerals.
            if (head.All(c => JapaneseTextHelper.IsNumeralChar(c) || c is '何' or '数'))
            {
                var result = output();
                int cut = w.StartOffset >= 0 ? w.StartOffset + head.Length : -1;
                result.Add(new WordInfo(w)
                {
                    Text = head, DictionaryForm = head, NormalizedForm = head, Reading = "", EndOffset = cut
                });
                result.Add(new WordInfo(b)
                {
                    Text = "段飛ばし", DictionaryForm = "段飛ばし", NormalizedForm = "段飛ばし",
                    Reading = "ダントバシ", PartOfSpeech = PartOfSpeech.Noun,
                    StartOffset = cut, EndOffset = b.EndOffset, PreMatchedWordId = 2746000
                });
                return 2;
            }
            return 0;
        });

    private List<WordInfo> RepairTankaToTaNKa(List<WordInfo> wordInfos)
    {
        var result = new List<WordInfo>(wordInfos.Count + 4);
        var deconj = Deconjugator.Instance;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            // たか mis-tokenised as the noun 鷹/高 when it's actually past-tense た + question か
            // (言い過ぎ|たか → 言い過ぎた|か, 飲み過ぎ|たか → 飲み過ぎた|か). Like the たんか repair below
            // but without the ん. Gated on the preceding token + た forming a real verb past tense, so
            // genuine 鷹/高 nouns (preceded by を/の, or with no verb stem before) are left alone.
            if (word is { PartOfSpeech: PartOfSpeech.Noun, Text: "たか" } && result.Count > 0)
            {
                var prevTok = result[^1];
                var pastForms = deconj.Deconjugate(NormalizeToHiragana(prevTok.Text + "た"));
                bool validPast = pastForms.Any(f =>
                    f.Process.Any(p => p == "past") &&
                    f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)));
                if (validPast)
                {
                    result[^1] = new WordInfo(prevTok)
                    {
                        Text = prevTok.Text + "た",
                        PartOfSpeech = PartOfSpeech.Verb,
                        Reading = string.IsNullOrEmpty(prevTok.Reading) ? prevTok.Reading : prevTok.Reading + "タ",
                        EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : prevTok.EndOffset
                    };
                    result.Add(new WordInfo
                    {
                        Text = "か", DictionaryForm = "か", NormalizedForm = "か",
                        PartOfSpeech = PartOfSpeech.Particle, Reading = "カ",
                        StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1,
                        EndOffset = word.EndOffset
                    });
                    continue;
                }
            }

            // Only process たんか noun tokens
            if (word.PartOfSpeech != PartOfSpeech.Noun || word.Text != "たんか")
            {
                result.Add(word);
                continue;
            }

            // Don't split if followed by を (object marker - indicates real noun usage like たんかを吐く)
            if (i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "を")
            {
                result.Add(word);
                continue;
            }

            // Don't split if preceded by を (indicates real noun)
            if (result.Count > 0 && result[^1].Text == "を")
            {
                result.Add(word);
                continue;
            }

            // Don't split if preceded by の (possessive - indicates real noun like お島の方のたんか)
            if (result.Count > 0 && result[^1].Text == "の")
            {
                result.Add(word);
                continue;
            }

            // Helper to find the last meaningful token (skip punctuation)
            WordInfo? GetPrevToken(int offset = 1)
            {
                int count = 0;
                for (int j = result.Count - 1; j >= 0; j--)
                {
                    if (result[j].PartOfSpeech == PartOfSpeech.SupplementarySymbol) continue;
                    count++;
                    if (count == offset) return result[j];
                }

                return null;
            }

            int GetPrevTokenIndex(int offset = 1)
            {
                int count = 0;
                for (int j = result.Count - 1; j >= 0; j--)
                {
                    if (result[j].PartOfSpeech == PartOfSpeech.SupplementarySymbol) continue;
                    count++;
                    if (count == offset) return j;
                }

                return -1;
            }

            // Check if splitting would create a valid verb conjugation
            bool shouldSplit = false;
            var prev = GetPrevToken(1);

            if (prev != null)
            {
                // Pattern 1: Verb/Adjective + たんか → Verb/Adjective + た + ん + か
                // e.g., 云う + たんか → 云うた + ん + か (valid past tense)
                // e.g., 怖がって + たんか → 怖がってた + ん + か (te-form + ta)
                if (prev.PartOfSpeech == PartOfSpeech.Verb)
                {
                    var candidateText = prev.Text + "た";
                    var forms = deconj.Deconjugate(NormalizeToHiragana(candidateText));
                    if (forms.Any(f => f.Tags.Any(t => t.StartsWith('v'))))
                        shouldSplit = true;
                }

                // Pattern 1b: Te-form ending + たんか → combine with た
                // Handles cases like 怖がって + たんか where 怖がって is classified as IAdjective
                // If prev ends with て/で, adding た creates てた/でた (past progressive/resultative)
                if (!shouldSplit && (prev.Text.EndsWith('て') || prev.Text.EndsWith('で')))
                {
                    // This is likely a te-form that should combine with た from たんか
                    // e.g., 怖がって + た → 怖がってた (was scared)
                    shouldSplit = true;
                }

                // Pattern 2: Adverb もう + たんか → もう is part of てもうた (Kansai てしまった)
                // e.g., ハズレて + もう + たんか → ハズレてもうた + ん + か
                // Check by text "もう" since POS might vary
                if (prev.Text == "もう")
                {
                    var verbBefore = GetPrevToken(2);
                    if (verbBefore != null && (verbBefore.Text.EndsWith('て') || verbBefore.Text.EndsWith('で')))
                    {
                        // Combine: verbて + もう + た → verbてもうた
                        var combinedText = verbBefore.Text + "もうた";
                        var prevIdx = GetPrevTokenIndex(1);
                        var verbIdx = GetPrevTokenIndex(2);
                        // Remove in descending order to keep indices valid
                        if (prevIdx >= 0 && verbIdx >= 0)
                        {
                            if (prevIdx > verbIdx)
                            {
                                result.RemoveAt(prevIdx);
                                result.RemoveAt(verbIdx);
                            }
                            else
                            {
                                result.RemoveAt(verbIdx);
                                result.RemoveAt(prevIdx);
                            }
                        }

                        result.Add(new WordInfo(verbBefore) { Text = combinedText, PartOfSpeech = PartOfSpeech.Verb,
                            EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1 });
                        var nTok2 = CreateNToken();
                        nTok2.StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1;
                        nTok2.EndOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1;
                        result.Add(nTok2);
                        result.Add(new WordInfo { Text = "か", DictionaryForm = "か", PartOfSpeech = PartOfSpeech.Particle, Reading = "か",
                            StartOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1, EndOffset = word.EndOffset });
                        continue;
                    }
                }

                // Pattern 3: も + たんか after し (conjunction) → part of てしもた (Kansai てしまった)
                // e.g., 言うて + し + も + たんか → 言うてしもた + ん + か
                if (prev.Text == "も")
                {
                    var shiToken = GetPrevToken(2);
                    if (shiToken is { Text: "し" })
                    {
                        var verbBefore = GetPrevToken(3);
                        if (verbBefore != null && (verbBefore.Text.EndsWith('て') || verbBefore.Text.EndsWith('で') ||
                                                   verbBefore.PartOfSpeech == PartOfSpeech.Expression))
                        {
                            // Combine: verb + し + も + た → verbしもた
                            var combinedText = verbBefore.Text + "しもた";
                            var moIdx = GetPrevTokenIndex(1);
                            var shiIdx = GetPrevTokenIndex(2);
                            var verbIdx = GetPrevTokenIndex(3);
                            // Remove in descending index order
                            var indices = new[] { moIdx, shiIdx, verbIdx }.Where(x => x >= 0).OrderByDescending(x => x).ToList();
                            foreach (var idx in indices) result.RemoveAt(idx);
                            result.Add(new WordInfo(verbBefore) { Text = combinedText, PartOfSpeech = PartOfSpeech.Verb,
                                EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1 });
                            var nTok3 = CreateNToken();
                            nTok3.StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1;
                            nTok3.EndOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1;
                            result.Add(nTok3);
                            result.Add(new WordInfo
                                       {
                                           Text = "か", DictionaryForm = "か", PartOfSpeech = PartOfSpeech.Particle, Reading = "か",
                                           StartOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1, EndOffset = word.EndOffset
                                       });
                            continue;
                        }
                    }
                }
            }

            if (shouldSplit && prev != null)
            {
                // Modify previous verb to include た
                var prevIdx = GetPrevTokenIndex(1);
                if (prevIdx >= 0)
                {
                    result[prevIdx] = new WordInfo(prev) { Text = prev.Text + "た", PartOfSpeech = PartOfSpeech.Verb,
                        EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1 };
                }

                var nTok = CreateNToken();
                nTok.StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1;
                nTok.EndOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1;
                result.Add(nTok);
                result.Add(new WordInfo { Text = "か", DictionaryForm = "か", PartOfSpeech = PartOfSpeech.Particle, Reading = "か",
                    StartOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1, EndOffset = word.EndOffset });
            }
            else
            {
                result.Add(word);
            }
        }

        return result;
    }

    private List<WordInfo> RepairColloquialNegativeNee(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 3) return wordInfos;

        var result = new List<WordInfo>(wordInfos.Count);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var current = wordInfos[i];

            // Sudachi splits colloquial ねえ (= ない negative) into ね + え after te/de-form
            // e.g., 入ってねえのに → 入っ + て + ね + え + のに
            // e.g., 飲んでねえのに → 飲んで (already merged by RepairN) + ね + え + のに
            if (current is { Text: "え", PartOfSpeech: PartOfSpeech.Interjection } &&
                result.Count >= 2 &&
                result[^1] is { Text: "ね", PartOfSpeech: PartOfSpeech.Particle } &&
                (result[^2] is { PartOfSpeech: PartOfSpeech.Particle, Text: "て" or "で" } ||
                 (result[^2].PartOfSpeech == PartOfSpeech.Verb &&
                  (result[^2].Text.EndsWith('て') || result[^2].Text.EndsWith('で')))))
            {
                result[^1] = new WordInfo(result[^1])
                {
                    Text = "ねえ", EndOffset = current.EndOffset,
                    PartOfSpeech = PartOfSpeech.Auxiliary, DictionaryForm = "ない",
                    NormalizedForm = "ない", Reading = "ネエ"
                };
                continue;
            }

            result.Add(current);
        }

        return result;
    }

    /// <summary>
    /// Merges colloquial らん + negative (ない/ねえ/ねぇ/ねー) into a single auxiliary token
    /// when preceded by a te/de-form. Sudachi tokenizes らん as adverb, which prevents
    /// CombineInflections from merging it with the preceding verb. The deconjugator already
    /// has the rule らんない → られない (n-slang), so this just needs to produce a mergeable token.
    /// e.g., 付き合っ + て + らん + ない → 付き合っ + て + らんない (auxiliary)
    /// </summary>
    private List<WordInfo> RepairColloquialRanNai(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 3) return wordInfos;

        var result = new List<WordInfo>(wordInfos.Count);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var current = wordInfos[i];

            if (current.Text == "らん" &&
                i + 1 < wordInfos.Count &&
                wordInfos[i + 1].Text is "ない" or "ねえ" or "ねぇ" or "ねー" &&
                result.Count >= 1 &&
                (result[^1] is { PartOfSpeech: PartOfSpeech.Particle, Text: "て" or "で" } ||
                 (result[^1].Text.EndsWith('て') || result[^1].Text.EndsWith('で'))))
            {
                var next = wordInfos[i + 1];
                result.Add(new WordInfo
                {
                    Text = "らん" + next.Text,
                    StartOffset = current.StartOffset,
                    EndOffset = next.EndOffset,
                    PartOfSpeech = PartOfSpeech.Auxiliary,
                    DictionaryForm = "られない",
                    NormalizedForm = "られない",
                    Reading = "ラン" + next.Reading
                });
                i++;
                continue;
            }

            result.Add(current);
        }

        return result;
    }

    private static readonly HashSet<string> KnownParticlesAndConjunctions =
        ["けど", "けども", "けれど", "けれども", "ので", "のに", "から", "まで"];

    private const string SmallVowelKana = "ぁぃぅぇぉァィゥェォ";

    /// <summary>Collapses a colloquial small-vowel stretch (ちょっとぉ, そんなぁ) back onto its dictionary surface.</summary>
    private bool TryStripTrailingSmallVowel(WordInfo w, out WordInfo repaired)
    {
        repaired = w;
        if (HasNonNameCompoundLookup == null || w.Text.Length < 3 || SmallVowelKana.IndexOf(w.Text[^1]) < 0)
            return false;

        // A small vowel from a different row is a digraph (ファ, ティ) — that mora's spelling, not a stretch.
        int row = VowelRowOf(w.Text[^1]);
        if (row < 0 || VowelRowOf(w.Text[^2]) != row) return false;

        foreach (var c in w.Text)
        {
            if (!JapaneseTextHelper.IsKana(c) || c == 'ー') return false;
        }

        var stripped = w.Text[..^1];
        // An attested surface is lexical (ねぇ, もぉ, すげぇ), however same-vowel it looks.
        if (HasNonNameCompoundLookup(w.Text) || !HasNonNameCompoundLookup(stripped)) return false;

        repaired = new WordInfo(w)
        {
            Text = stripped,
            DictionaryForm = w.DictionaryForm == w.Text ? stripped : w.DictionaryForm,
            Reading = w.Reading.Length > 1 && SmallVowelKana.IndexOf(w.Reading[^1]) >= 0 ? w.Reading[..^1] : w.Reading
        };
        return true;
    }

    private List<WordInfo> RepairVowelElongation(List<WordInfo> wordInfos)
    {
        bool changed = false;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var w = wordInfos[i];

            if (TryStripTrailingSmallVowel(w, out var deElongated))
            {
                wordInfos[i] = deElongated;
                w = deElongated;
                changed = true;
            }

            // Strip trailing ー from particles/conjunctions (colloquial elongation like けどー).
            if (w.Text.Length >= 2 && w.Text[^1] == 'ー'
                && w.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Conjunction)
            {
                var prtStripped = w.Text[..^1];
                bool allHiragana = true;
                foreach (var c in prtStripped)
                {
                    if (c is < '぀' or > 'ゟ') { allHiragana = false; break; }
                }
                if (allHiragana)
                {
                    wordInfos[i] = new WordInfo(w) { Text = prtStripped };
                    changed = true;
                    continue;
                }
            }

            // Normalize katakana tokens with trailing ー that are actually particles
            // (e.g. ケドー matched as KEDO organization → should be けど conjunction)
            if (w.Text.Length >= 2 && w.Text[^1] == 'ー' && w.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
            {
                var body = w.Text[..^1];
                bool allKatakana = body.Length > 0;
                foreach (var c in body)
                {
                    if (c is < '゠' or > 'ヿ') { allKatakana = false; break; }
                }
                if (allKatakana)
                {
                    var hiragana = KanaConverter.ToHiragana(body);
                    if (KnownParticlesAndConjunctions.Contains(hiragana))
                    {
                        wordInfos[i] = new WordInfo(w)
                        {
                            Text = hiragana,
                            DictionaryForm = hiragana,
                            NormalizedForm = hiragana,
                            Reading = body,
                            PartOfSpeech = PartOfSpeech.Conjunction
                        };
                        changed = true;
                        continue;
                    }
                }
            }

            if (!w.Text.Contains('ー') || w.Text[^1] == 'ー' || w.PartOfSpeech == PartOfSpeech.Interjection) continue;

            bool allHiraganaOrBar = true;
            foreach (var c in w.Text)
            {
                if (c != 'ー' && !(c >= '\u3040' && c <= '\u309F'))
                {
                    allHiraganaOrBar = false;
                    break;
                }
            }

            if (!allHiraganaOrBar) continue;

            var stripped = w.Text.Replace("ー", "");
            if (stripped.Length == 0 || stripped == w.Text) continue;

            bool normalizedHasKanji = false;
            foreach (var c in w.NormalizedForm)
            {
                if (c >= '\u4E00' && c <= '\u9FFF')
                {
                    normalizedHasKanji = true;
                    break;
                }
            }

            if (!normalizedHasKanji && stripped != w.NormalizedForm) continue;

            wordInfos[i] = new WordInfo(w)
            {
                Text = stripped,
                DictionaryForm = w.DictionaryForm.Replace("ー", ""),
                Reading = w.Reading.Replace("ー", ""),
            };
            changed = true;
        }

        if (wordInfos.Count < 2) return wordInfos;

        var deconjugator = Deconjugator.Instance;
        var result = new List<WordInfo>(wordInfos.Count);

        static WordInfo MakeInterjection(string text) =>
            new()
            {
                Text = text, DictionaryForm = text, NormalizedForm = text, Reading = text, PartOfSpeech = PartOfSpeech.Interjection
            };

        static bool IsVerbPast(IReadOnlyList<DeconjugationForm> forms) =>
            forms.Any(f => f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) && f.Process.Any(p => p == "past"));

        static bool IsRuVerb(IReadOnlyList<DeconjugationForm> forms, string expectedDictionaryHiragana) =>
            forms.Any(f => f.Text == expectedDictionaryHiragana && f.Tags.Any(t => t is "v1" or "v5r"));

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var current = wordInfos[i];

            if (result.Count == 0)
            {
                result.Add(current);
                continue;
            }

            var prev = result[^1];

            // Pattern: [noun] + [んー filler] → merge ん into preceding token, discard ー
            // Sudachi splits Xん+ー as X + んー when ー causes the filler interpretation
            // e.g., 総ちゃんー → 総 + ちゃ(noun) + んー(filler) → 総 + ちゃん
            if (current.PartOfSpeech is PartOfSpeech.Interjection or PartOfSpeech.Filler &&
                current.Text is ['ん', _, ..] &&
                current.Text[1..].All(c => c == 'ー') &&
                prev.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun &&
                prev.Text.Length <= 2 &&
                !prev.Text.EndsWith('ん'))
            {
                result[^1] = new WordInfo(prev) { Text = prev.Text + "ん", EndOffset = current.EndOffset, PartOfSpeech = PartOfSpeech.Suffix };
                changed = true;
                continue;
            }

            // Pattern: [verb] + [あ interjection] + [う interjection] → reciprocal auxiliary あう (合う)
            // Sudachi shreds kana 〜あう compounds into interjections (微笑みあう → 微笑み + あ + う).
            if (current is { PartOfSpeech: PartOfSpeech.Interjection, Text: "う" } &&
                result.Count >= 2 &&
                prev is { PartOfSpeech: PartOfSpeech.Interjection, Text: "あ" } &&
                result[^2].PartOfSpeech == PartOfSpeech.Verb)
            {
                result[^1] = new WordInfo(prev)
                {
                    Text = "あう", DictionaryForm = "あう", NormalizedForm = "合う", Reading = "アウ",
                    PartOfSpeech = PartOfSpeech.Verb,
                    PartOfSpeechSection1 = PartOfSpeechSection.PossibleDependant,
                    EndOffset = current.EndOffset
                };
                changed = true;
                continue;
            }

            // Pattern: [short っ-final kana adverb] + [verb] → intensifying-prefix verb when the fused
            // dictionary form is a real kana word (すっ + とぼける → すっとぼける).
            if (current.PartOfSpeech == PartOfSpeech.Verb &&
                prev.PartOfSpeech is PartOfSpeech.Adverb or PartOfSpeech.Prefix &&
                prev.Text.Length == 2 && prev.Text[^1] == 'っ' &&
                prev.Text[0] >= '぀' && prev.Text[0] <= 'ゟ' &&
                current.DictionaryForm.Length > 0 &&
                (HasKanaAppropriateCompoundLookup ?? HasCompoundLookup)?.Invoke(prev.Text + current.DictionaryForm) == true)
            {
                result[^1] = new WordInfo(current)
                {
                    Text = prev.Text + current.Text,
                    DictionaryForm = prev.Text + current.DictionaryForm,
                    NormalizedForm = prev.Text + current.DictionaryForm,
                    Reading = prev.Reading + current.Reading,
                    StartOffset = prev.StartOffset,
                };
                changed = true;
                continue;
            }

            // Pattern 0: [prefix/suffix] + [る OOV] + [ー symbol]
            // Sudachi splits る-verbs when followed by expressive elongation ー
            // e.g., 来るー → 来(prefix) + る(OOV noun) + ー(symbol)
            // e.g., おいしすぎるー → おいし + すぎ(suffix) + る(OOV) + ー → おいし + すぎる
            if (current is { PartOfSpeech: PartOfSpeech.SupplementarySymbol, Text: "ー" } &&
                result.Count >= 2 &&
                prev is { Text: "る", PartOfSpeech: PartOfSpeech.Noun } &&
                result[^2].PartOfSpeech is PartOfSpeech.Prefix or PartOfSpeech.Suffix)
            {
                var preceding = result[^2];
                var verbText = preceding.Text + "る";
                result.RemoveAt(result.Count - 1);
                result[^1] = new WordInfo(preceding)
                {
                    Text = verbText, EndOffset = current.EndOffset,
                    DictionaryForm = verbText, NormalizedForm = verbText,
                    PartOfSpeech = PartOfSpeech.Verb,
                    PartOfSpeechSection1 = preceding.PartOfSpeech == PartOfSpeech.Suffix
                        ? PartOfSpeechSection.PossibleDependant
                        : PartOfSpeechSection.None
                };
                changed = true;
                continue;
            }

            // Pattern 0c: [kanji-stem OOV] + [o-row hiragana OOV] + [ー symbol] → godan volitional with
            // the trailing う of the volitional colloquially lengthened to ー.
            // e.g., 泳ごー → 泳(noun/OOV) + ご(noun/OOV) + ー(symbol) → 泳ご + elongation う.
            // Generic across any godan verb whose stem ends in a vowel-o kana (こ/ご/そ/ぞ/と/ど/の/ほ/ぼ/ぽ/も/よ/ろ/お).
            if (current is { PartOfSpeech: PartOfSpeech.SupplementarySymbol, Text: "ー" } &&
                result.Count >= 2 &&
                prev.Text.Length == 1 &&
                GodanVolitionalOKana.Contains(prev.Text[0]) &&
                prev.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun &&
                result[^2].Text.Length >= 1 &&
                result[^2].Text.All(c => c >= '\u4E00' && c <= '\u9FFF'))
            {
                var stem = result[^2];
                var volitionalCandidate = stem.Text + prev.Text + "う";
                var volitionalHiragana = NormalizeToHiragana(stem.Reading + prev.Text + "う");
                var forms = deconjugator.Deconjugate(volitionalHiragana);
                bool isValidVolitional = forms.Any(f =>
                    f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) &&
                    f.Process.Any(p => p.Contains("volitional", StringComparison.Ordinal)));

                if (isValidVolitional)
                {
                    result.RemoveAt(result.Count - 1);
                    result[^1] = new WordInfo(stem)
                    {
                        Text = stem.Text + prev.Text + "う",
                        DictionaryForm = volitionalCandidate,
                        NormalizedForm = volitionalCandidate,
                        Reading = KanaConverter.ToHiragana(stem.Reading + prev.Reading + "う"),
                        PartOfSpeech = PartOfSpeech.Verb,
                        EndOffset = current.EndOffset
                    };
                    changed = true;
                    continue;
                }
            }

            // Pattern 0d: [kanji noun] + [o-kana+ー as single token] → godan volitional
            // Like 0c but the ー is embedded in the second token instead of separate.
            // e.g., 遊ぼー → 遊(noun) + ぼー(adverb) → 遊ぼう (volitional of 遊ぶ)
            if (current.Text.Length == 2 && current.Text[^1] == 'ー' &&
                GodanVolitionalOKana.Contains(current.Text[0]) &&
                prev.Text.Length >= 1 && prev.Text.All(c => c >= '一' && c <= '鿿'))
            {
                var oKana = current.Text[0].ToString();
                var volitionalCandidate = prev.Text + oKana + "う";
                var volitionalHiragana = NormalizeToHiragana(prev.Reading + oKana + "う");
                var forms = deconjugator.Deconjugate(volitionalHiragana);
                bool isValidVolitional = forms.Any(f =>
                    f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) &&
                    f.Process.Any(p => p.Contains("volitional", StringComparison.Ordinal)));

                if (isValidVolitional)
                {
                    result[^1] = new WordInfo(prev)
                    {
                        Text = prev.Text + oKana + "う",
                        DictionaryForm = volitionalCandidate,
                        NormalizedForm = volitionalCandidate,
                        Reading = KanaConverter.ToHiragana(prev.Reading + oKana + "う"),
                        PartOfSpeech = PartOfSpeech.Verb,
                        EndOffset = current.EndOffset
                    };
                    changed = true;
                    continue;
                }
            }

            // Pattern 0b: [adjective-stem/prefix] + [くー/きー/etc. interjection]
            // Sudachi splits i-adjective adverbial forms when followed by expressive ー
            // e.g., 早くー → 早(prefix) + くー(interjection) → 早く
            if (current.PartOfSpeech is PartOfSpeech.Interjection &&
                current.Text.Length >= 2 && current.Text[^1] == 'ー' &&
                current.Text[..^1].All(c => c >= '\u3040' && c <= '\u309F') &&
                prev.PartOfSpeech is PartOfSpeech.Prefix or PartOfSpeech.IAdjective &&
                prev.DictionaryForm.EndsWith('い'))
            {
                var adverbText = prev.Text + current.Text[..^1];
                result[^1] = new WordInfo(prev)
                {
                    Text = adverbText, EndOffset = current.EndOffset,
                    DictionaryForm = prev.DictionaryForm,
                    PartOfSpeech = PartOfSpeech.IAdjective
                };
                changed = true;
                continue;
            }

            // Pattern 1: Token ending in "るう" that might be a misparsed verb + elongation
            // e.g., "かるう" could be part of "ぶつかる" + "う"
            if (current.Text.EndsWith("るう", StringComparison.Ordinal) && current.Text.Length >= 2)
            {
                var verbCandidate = prev.Text + current.Text[..^1]; // prev + current minus trailing う
                var verbHiragana = NormalizeToHiragana(verbCandidate);

                // Check if this forms a valid る-verb by testing negative form deconjugation.
                // Godan-ru verbs use らない (ぶつかる → ぶつからない), ichidan verbs use ない (食べる → 食べない).
                // Validate by requiring the deconjugator to recover the exact candidate (hiragana) as v1 or v5r.
                var isValidRuVerb = verbHiragana.EndsWith("る", StringComparison.Ordinal) &&
                                    (IsRuVerb(deconjugator.Deconjugate(verbHiragana[..^1] + "ない"), verbHiragana) ||
                                     IsRuVerb(deconjugator.Deconjugate(verbHiragana[..^1] + "らない"), verbHiragana));

                if (isValidRuVerb)
                {
                    result[^1] = new WordInfo(prev)
                                 {
                                     Text = verbCandidate, DictionaryForm = verbCandidate, NormalizedForm = verbCandidate,
                                     Reading = KanaConverter.ToHiragana(prev.Reading + current.Text[..^1]), PartOfSpeech = PartOfSpeech.Verb,
                                     EndOffset = current.EndOffset >= 0 ? current.EndOffset - 1 : -1
                                 };
                    // Add the elongation う as a separate token
                    var interjection = MakeInterjection("う");
                    interjection.StartOffset = current.EndOffset >= 0 ? current.EndOffset - 1 : -1;
                    interjection.EndOffset = current.EndOffset;
                    result.Add(interjection);
                    changed = true;
                    continue;
                }
            }

            // Pattern 3: Token + "たあ" (often misparsed as particle と)
            // e.g., "おき" + "たあ" should be "おきた" + "あ" (past of 起きる)
            if (current.Text == "たあ")
            {
                var pastCandidate = prev.Text + "た";
                var pastHiragana = NormalizeToHiragana(pastCandidate);

                // Check if this forms a valid verb past tense
                var isValidVerbPast = IsVerbPast(deconjugator.Deconjugate(pastHiragana));

                if (isValidVerbPast)
                {
                    result[^1] = new WordInfo(prev)
                    {
                        Text = pastCandidate, Reading = KanaConverter.ToHiragana(prev.Reading + "た"), PartOfSpeech = PartOfSpeech.Verb,
                        EndOffset = current.StartOffset >= 0 ? current.StartOffset + 1 : -1
                    };
                    var interjection = MakeInterjection("あ");
                    interjection.StartOffset = current.StartOffset >= 0 ? current.StartOffset + 1 : -1;
                    interjection.EndOffset = current.EndOffset;
                    result.Add(interjection);
                    changed = true;
                    continue;
                }
            }

            // Pattern 4: Token ending in "た" + "ああ" (interjection)
            // e.g., "いきた" + "ああ" where いきた is misparsed as nominal adjective
            if (current.Text == "ああ")
            {
                var prevHiragana = NormalizeToHiragana(prev.Text);

                // Check if prev token ending in た is a valid verb past tense
                if (prevHiragana.EndsWith("た", StringComparison.Ordinal) || prevHiragana.EndsWith("だ", StringComparison.Ordinal))
                {
                    if (IsVerbPast(deconjugator.Deconjugate(prevHiragana)) && prev.PartOfSpeech != PartOfSpeech.Verb)
                    {
                        result[^1] = new WordInfo(prev) { PartOfSpeech = PartOfSpeech.Verb };
                        changed = true;
                    }
                }
            }

            // Pattern 5: small-vowel elongation fused onto a conjugation ending. Sudachi emits
            // [kana][run of ≥2 identical small vowels] as one OOV noun: 来やがれぇぇぇ →
            // 来 + やが + れぇぇぇ. Strip the elongation and reattach the leading kana to the
            // preceding verb/auxiliary when it makes a real conjugation (やが+れ → やがれ = やがる
            // imperative). A bare noun-tagged stem (移れぇぇぇ → 移 noun) fails the verb/aux gate
            // and is left as-is.
            if (prev.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary &&
                current.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Interjection &&
                IsTrailingSmallVowelRun(current.Text, out int coreLen))
            {
                var core = current.Text[..coreLen];
                var candidate = NormalizeToHiragana(prev.Text + core);
                var prevDictHiragana = NormalizeToHiragana(prev.DictionaryForm);
                bool valid = candidate != prevDictHiragana && deconjugator.Deconjugate(candidate).Any(f =>
                    f.Text == prevDictHiragana &&
                    f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)));

                if (valid)
                {
                    result[^1] = new WordInfo(prev)
                    {
                        Text = prev.Text + core,
                        Reading = prev.Reading + WanaKanaShaapu.WanaKana.ToKatakana(core),
                        EndOffset = current.StartOffset >= 0 ? current.StartOffset + coreLen : prev.EndOffset
                    };
                    changed = true;
                    continue;
                }
            }

            result.Add(current);
        }

        return changed ? result : wordInfos;
    }

    private List<WordInfo> RepairNTokenisation(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        // Phase 1: Split compound tokens that Sudachi incorrectly grouped
        List<WordInfo>? split = null;
        for (int idx = 0; idx < wordInfos.Count; idx++)
        {
            var word = wordInfos[idx];

            // Split tokens starting with ん (e.g., んだ → ん + だ)
            if (word.Text.Length > 1 && word.Text[0] == 'ん')
            {
                var remainder = word.Text[1..];
                bool startsWithSuffix = false;
                foreach (var s in NCompoundSuffixes)
                {
                    if (remainder.StartsWith(s, StringComparison.Ordinal)) { startsWithSuffix = true; break; }
                }

                if (startsWithSuffix)
                {
                    split ??= CopyAccumulatorUpTo(wordInfos, idx);
                    var nToken = CreateNToken();
                    nToken.StartOffset = word.StartOffset;
                    nToken.EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1;
                    if (word.PartOfSpeech == PartOfSpeech.Interjection)
                        nToken.DictionaryForm = "の";
                    split.Add(nToken);
                    split.Add(new WordInfo(word)
                    {
                        Text = remainder, DictionaryForm = remainder,
                        NormalizedForm = remainder, Reading = remainder,
                        StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
                    });
                    continue;
                }
            }

            // Split tokens starting with だ when preceded by ん (e.g., だが → だ + が)
            if (word.Text.Length > 1 && word.Text[0] == 'だ')
            {
                var prevEmitted = split != null ? (split.Count > 0 ? split[^1] : null)
                                                : (idx > 0 ? wordInfos[idx - 1] : null);
                if (prevEmitted != null && (prevEmitted.Text == "ん" || prevEmitted.Text.EndsWith('ん')))
                {
                    var remainder = word.Text[1..];
                    if (DaCompoundSuffixes.Contains(remainder))
                    {
                        split ??= CopyAccumulatorUpTo(wordInfos, idx);
                        var daToken = CreateDaToken();
                        daToken.StartOffset = word.StartOffset;
                        daToken.EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1;
                        split.Add(daToken);
                        split.Add(new WordInfo(word)
                        {
                            Text = remainder, DictionaryForm = remainder,
                            NormalizedForm = remainder, Reading = remainder,
                            StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
                        });
                        continue;
                    }
                }
            }

            // Split そうだ → そう + だ (appearance/hearsay pattern should be split for combining logic)
            if (word is { Text: "そうだ", PartOfSpeech: PartOfSpeech.Adverb })
            {
                split ??= CopyAccumulatorUpTo(wordInfos, idx);
                split.Add(new WordInfo(word)
                          {
                              Text = "そう", DictionaryForm = "そう", NormalizedForm = "そう", Reading = "そう",
                              PartOfSpeech = PartOfSpeech.Auxiliary, PartOfSpeechSection1 = PartOfSpeechSection.AuxiliaryVerbStem,
                              EndOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1
                          });
                var daToken = CreateDaToken();
                daToken.StartOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1;
                daToken.EndOffset = word.EndOffset;
                split.Add(daToken);
                continue;
            }

            split?.Add(word);
        }

        // Phase 2: Recombine verb stems with ん using deconjugator validation
        var source = split ?? wordInfos;
        List<WordInfo>? result = null;
        bool changed = false;
        var deconj = Deconjugator.Instance;

        for (int i = 0; i < source.Count; i++)
        {
            var current = source[i];

            // Case: Token already ends with ん (e.g., 飲ん) and next is だ/で - combine as past/te-form
            // Skip na-adjectives (e.g., たくさん + で should NOT combine - で is the copula, not verb conjugation)
            // Skip suffixes (e.g., さん + だ should NOT combine - さん is honorific, だ is copula)
            if (current.Text.EndsWith('ん') && current.Text.Length > 1 && current.Text != "ん" &&
                !IsNaAdjectiveToken(current) &&
                current.PartOfSpeech != PartOfSpeech.Suffix &&
                !NormalizeToHiragana(current.DictionaryForm).EndsWith('ん') &&
                i + 1 < source.Count && source[i + 1].Text is "だ" or "で")
            {
                var candidateText = current.Text + source[i + 1].Text;
                if (IsNdaVerbForm(deconj.Deconjugate(NormalizeToHiragana(candidateText))))
                {
                    var candidateReading = KanaConverter.ToHiragana(current.Reading + source[i + 1].Reading);
                    result ??= CopyAccumulatorUpTo(source, i);
                    result.Add(new WordInfo(current)
                    {
                        Text = candidateText, PartOfSpeech = PartOfSpeech.Verb,
                        NormalizedForm = candidateText, Reading = candidateReading,
                        EndOffset = source[i + 1].EndOffset
                    });
                    changed = true;
                    i++;
                    continue;
                }
            }

            // Case: Standalone ん - try to combine with preceding verb stem
            if (current.Text == "ん" && (result != null ? result.Count > 0 : i > 0))
            {
                result ??= CopyAccumulatorUpTo(source, i);
                bool combined = false;

                // し(為る・連用形)+ん+だ/で is ungrammatical — explanatory のだ attaches to the
                // 連体形 (するんだ), never the 連用形 — so kana しんだ here is 死んだ
                // (きみのなかまもすべてしんだ).
                if (i + 1 < source.Count && source[i + 1].Text is "だ" or "で"
                    && result.Count > 0
                    && result[^1] is { Text: "し", PartOfSpeech: PartOfSpeech.Verb } prevShi
                    && prevShi.DictionaryForm is "する" or "為る")
                {
                    result[^1] = new WordInfo(prevShi)
                    {
                        Text = "しん" + source[i + 1].Text,
                        DictionaryForm = "死ぬ", NormalizedForm = "死ぬ",
                        Reading = "シン" + source[i + 1].Reading,
                        EndOffset = source[i + 1].EndOffset
                    };
                    changed = true;
                    i++;
                    continue;
                }

                // Try んだ/んで pattern (past/te-form) - only for verb conjugation, not explanatory ん
                // Skip when ん is explanatory particle (DictionaryForm = "の" or "ん") or negative auxiliary (DictionaryForm = "ぬ")
                if (i + 1 < source.Count && source[i + 1].Text is "だ" or "で" &&
                    current.DictionaryForm is not "ぬ" and not "の" and not "ん")
                {
                    var suffix = "ん" + source[i + 1].Text;
                    var suffixReading = "ん" + source[i + 1].Reading;
                    if (TryCombineWithLookback(result, suffix, suffixReading, deconj, IsNdaVerbForm, out var combinedWord))
                    {
                        combinedWord!.EndOffset = source[i + 1].EndOffset;
                        result.Add(combinedWord);
                        combined = true;
                        i++;
                    }
                }

                // Fallback for ん classified as explanatory (from interjection split):
                // Sudachi sometimes misparsed verb stems as nouns (e.g., 喜んだだろうね → 喜(noun) + んだ)
                // Validate via dictionary lookup that noun + ぶ/む/ぬ/ぐ is a real verb
                if (!combined && i + 1 < source.Count && source[i + 1].Text is "だ" or "で" &&
                    current.DictionaryForm is "の" or "ん" &&
                    result.Count > 0 && result[^1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun &&
                    HasCompoundLookup != null)
                {
                    var prev = result[^1];
                    string[] ndaVerbEndings = ["ぶ", "む", "ぬ", "ぐ"];
                    foreach (var ending in ndaVerbEndings)
                    {
                        if (HasCompoundLookup(prev.Text + ending) ||
                            HasCompoundLookup(NormalizeToHiragana(prev.Text) + ending))
                        {
                            var candidateText = prev.Text + "ん" + source[i + 1].Text;
                            var candidateReading = KanaConverter.ToHiragana(prev.Reading + "ん" + source[i + 1].Reading);
                            result.RemoveAt(result.Count - 1);
                            result.Add(new WordInfo(prev)
                            {
                                Text = candidateText, PartOfSpeech = PartOfSpeech.Verb,
                                NormalizedForm = candidateText, Reading = candidateReading,
                                EndOffset = source[i + 1].EndOffset,
                                PartOfSpeechSection1 = PartOfSpeechSection.None,
                                PartOfSpeechSection2 = PartOfSpeechSection.None,
                                PartOfSpeechSection3 = PartOfSpeechSection.None
                            });
                            combined = true;
                            i++;
                            break;
                        }
                    }
                }

                // If んだ/んで didn't match, try negative ん contraction (ませ+ん→ません)
                // Only for actual negative auxiliary (DictionaryForm = "ぬ"), not explanatory ん.
                // Sudachi tags the slurred negative as ぬ after godan stems but falls back to the
                // explanatory の after an ichidan stem (足り+ん) — impossible there, because the
                // nominaliser ん attaches to the 連体形 (足りるん), never the bare stem.
                bool misreadStemNegative = current.DictionaryForm is "の" or "ん"
                    && result.Count > 0
                    && result[^1].PartOfSpeech == PartOfSpeech.Verb
                    && result[^1].Text.Length > 0
                    && result[^1].Text != result[^1].DictionaryForm
                    // After a past/te-form (死んだ+ん, 読んで+ん) the ん is the explanatory のだ or a
                    // slurred ている — the negative can only follow a bare stem.
                    && result[^1].Text[^1] is not ('た' or 'だ' or 'て' or 'で')
                    // する's slurred negative is せん, never しん/すん — the mizenkei-ん deconjugation
                    // rule would wrongly validate し+ん (kana しん is 死ぬ material instead), and a
                    // bare す stem before explanatory ん is the contracted する (すんだ = するんだ).
                    && !(result[^1].Text is "し" or "す" && result[^1].DictionaryForm is "する" or "為る")
                    // ある's negative is ない/あらん, never あん — bare あ before explanatory ん is
                    // the contracted ある (あんだ = あるんだ).
                    && !(result[^1].Text == "あ" && result[^1].DictionaryForm is "ある" or "有る" or "在る");

                if (!combined && (current.DictionaryForm == "ぬ" || misreadStemNegative) &&
                    TryCombineWithLookback(result, "ん", "ん", deconj, IsAnyVerbForm, out var negativeWord))
                {
                    negativeWord!.EndOffset = current.EndOffset;
                    // Sudachi identified this ん as the negative auxiliary ぬ. The deconjugator
                    // alone can't tell it from a slurred る (してん) and prefers that shorter
                    // path, so record the diagnosis for chain selection.
                    negativeWord.IsSlurredNegative = true;

                    // After combining ませ+ん→ません, try to combine preceding verb stem with ません
                    // e.g., [し, ませ] + ん → [しません]
                    if (negativeWord.Text.EndsWith("ません", StringComparison.Ordinal) && result.Count > 0)
                    {
                        var verbStem = result[^1];
                        var candidateText = verbStem.Text + negativeWord.Text;
                        var candidateHiragana = NormalizeToHiragana(candidateText);
                        var forms = deconj.Deconjugate(candidateHiragana);
                        if (IsMasenVerbForm(forms))
                        {
                            result.RemoveAt(result.Count - 1);
                            negativeWord.Text = candidateText;
                            negativeWord.StartOffset = verbStem.StartOffset;
                            negativeWord.DictionaryForm = verbStem.DictionaryForm;
                            negativeWord.NormalizedForm = candidateText;
                            negativeWord.Reading = KanaConverter.ToHiragana(verbStem.Reading + negativeWord.Reading);
                            if (verbStem.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant))
                                negativeWord.PartOfSpeechSection1 = PartOfSpeechSection.PossibleDependant;
                        }
                    }

                    // The lookback stops at the shortest valid form, so a slurred negative on a voice
                    // auxiliary absorbs only the ん (られん→られる validates alone) and strands the
                    // preceding stem (認め|られん). Like the ません case above, reattach leftward while
                    // the combined token is still headed by a voice auxiliary: each step is valid only
                    // if the stem's own dictionary form deconjugates out of the whole (認められん→認める).
                    // Loops so causative-passive chains reattach fully (させ+られん, then 食べ+させられん).
                    while (VerbIndicatingAuxiliaries.Contains(negativeWord.DictionaryForm) && result.Count > 0 &&
                           (result[^1].PartOfSpeech == PartOfSpeech.Verb ||
                            (result[^1].PartOfSpeech == PartOfSpeech.Auxiliary &&
                             VerbIndicatingAuxiliaries.Contains(result[^1].DictionaryForm))))
                    {
                        var stem = result[^1];
                        var candidateText = stem.Text + negativeWord.Text;
                        var forms = deconj.Deconjugate(NormalizeToHiragana(candidateText));
                        var stemTarget = NormalizeToHiragana(stem.DictionaryForm);
                        if (!ContainsText(forms, stemTarget))
                            break;

                        result.RemoveAt(result.Count - 1);
                        negativeWord.Text = candidateText;
                        negativeWord.StartOffset = stem.StartOffset;
                        negativeWord.DictionaryForm = stem.DictionaryForm;
                        negativeWord.NormalizedForm = candidateText;
                        negativeWord.Reading = KanaConverter.ToHiragana(stem.Reading + negativeWord.Reading);
                        if (stem.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant))
                            negativeWord.PartOfSpeechSection1 = PartOfSpeechSection.PossibleDependant;
                    }

                    result.Add(negativeWord);
                    combined = true;
                }

                if (combined)
                    changed = true;
                else
                    result.Add(current);
                continue;
            }

            result?.Add(current);
        }

        return changed ? result! : source;
    }

    // True if the surface deconjugates to a verb (godan/ichidan) whose dictionary form is in JMDict.
    private bool DeconjugatesToVerbInLookup(string surface)
    {
        if (HasVerbOrAdjectiveLookup == null) return false;
        foreach (var f in Deconjugator.Instance.Deconjugate(NormalizeToHiragana(surface)))
            if (f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) && HasVerbOrAdjectiveLookup(f.Text))
                return true;
        return false;
    }

    // True if the surface deconjugates to a causative/passive verb in JMDict (やらせない → やる/やらせる).
    // Stricter than the plain check so ordinary ichidan negatives (聞こえない, plain 信用ない) stay split.
    private bool DeconjugatesToCausativeOrPassiveVerb(string surface)
    {
        if (HasVerbOrAdjectiveLookup == null) return false;
        foreach (var f in Deconjugator.Instance.Deconjugate(NormalizeToHiragana(surface)))
            if (f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) && HasVerbOrAdjectiveLookup(f.Text)
                && f.Process.Any(p => p.Contains("causative") || p.Contains("passive")))
                return true;
        return false;
    }

    /// さっき loses its き to a following pronoun in the lattice (さっきみごと → さっ|きみ|ごと).
    /// The fragment さっ (the clipped first morae of さっき) never legitimately precedes きみ; give the き back — さっき plus the remainder,
    /// which re-attaches to the following token when that forms a word (み+ごと → みごと,
    /// み+たい → みたい).
    private List<WordInfo> RepairSakkiMoraTheft(List<WordInfo> wordInfos) =>
        HasCompoundLookup == null ? wordInfos : ScanRewrite(wordInfos, TryRepairSakkiMoraTheft);

    private int TryRepairSakkiMoraTheft(List<WordInfo> tokens, int i, List<WordInfo>? _, Func<List<WordInfo>> output)
    {
        var word = tokens[i];
        if (word is not { Text: "さっ" } || i + 1 >= tokens.Count || tokens[i + 1].Text != "きみ")
            return 0;

        var stolen = tokens[i + 1];
        var result = output();
        result.Add(new WordInfo(word)
        {
            Text = "さっき", DictionaryForm = "さっき", NormalizedForm = "さっき",
            Reading = "サッキ", PartOfSpeech = PartOfSpeech.Noun,
            EndOffset = word.StartOffset >= 0 ? word.StartOffset + 3 : -1
        });

        var following = i + 2 < tokens.Count ? tokens[i + 2] : null;
        if (following != null && HasCompoundLookup!("み" + following.Text))
        {
            result.Add(new WordInfo(following)
            {
                Text = "み" + following.Text,
                DictionaryForm = "み" + following.Text,
                NormalizedForm = "み" + following.Text,
                // A rebuilt token must not inherit the source token's match state, and a
                // partial reading is worse than none when the tail's reading is unknown.
                Reading = following.Reading.Length > 0 ? "ミ" + following.Reading : "",
                PartOfSpeech = PartOfSpeech.Noun,
                PreMatchedWordId = null,
                PreMatchedConjugations = null,
                StartOffset = stolen.StartOffset >= 0 ? stolen.StartOffset + 1 : -1
            });
            return 3;
        }

        result.Add(new WordInfo(stolen)
        {
            Text = "み", DictionaryForm = "み", NormalizedForm = "み",
            Reading = "ミ", PartOfSpeech = PartOfSpeech.Noun,
            PreMatchedWordId = null,
            PreMatchedConjugations = null,
            StartOffset = stolen.StartOffset >= 0 ? stolen.StartOffset + 1 : -1
        });
        return 2;
    }

    /// <summary>
    /// Collapses an over-repeated reduplicative mimetic into one occurrence resolving to the 2× entry: a run
    /// of kana tokens whose combined text is a 2-mora unit repeated 3+ times (ごろごろごろ, ぐるぐるぐる, ぺら +
    /// ぺらぺら) is pinned to the 2× word (ごろごろ). Robust to however Sudachi cut the run — XYXY+XY, XY+XYXY,
    /// or a single XYXYXY token. The pin (PreMatchedWordId) resolves it to the 2× entry and skips
    /// deconjugation/re-segmentation; DictionaryForm/NormalizedForm hold the 2× key (the reading-index
    /// source) while Text keeps the full surface. The 2× form must be a non-name JMDict entry, so ordinary
    /// kana words and plain 2× mimetics (k=2) are left untouched.
    /// </summary>
    private List<WordInfo> CollapseReduplicatedMimetic(List<WordInfo> wordInfos) =>
        GetNonNameCompoundWordId == null ? wordInfos : ScanRewrite(wordInfos, TryCollapseReduplicatedMimetic);

    private int TryCollapseReduplicatedMimetic(List<WordInfo> tokens, int i, List<WordInfo>? _, Func<List<WordInfo>> output)
    {
        var first = tokens[i];
        if (!IsKanaUnitRepetition(first.Text, out var unit))
            return 0;

        int j = i + 1;
        int unitCount = first.Text.Length / 2;
        bool allInterjections = first.PartOfSpeech == PartOfSpeech.Interjection;
        while (j < tokens.Count && IsRepetitionOf(tokens[j].Text, unit))
        {
            unitCount += tokens[j].Text.Length / 2;
            allInterjections &= tokens[j].PartOfSpeech == PartOfSpeech.Interjection;
            j++;
        }

        // A run of repeated interjection tokens (はい|はい|はい, まあ|まあ|まあ) is deliberate emphatic
        // speech, not a mimetic — and its 2× surface can be an unrelated word (はいはい → 這い這い
        // "crawling"). Genuine mimetics reach here as Adverb/Noun tokens, so leave interjection runs
        // to resolve token-by-token.
        if (unitCount < 3 || allInterjections || GetNonNameCompoundWordId!(unit + unit) is not { } twoXId)
            return 0;

        string text = "", reading = "";
        for (int k = i; k < j; k++) { text += tokens[k].Text; reading += tokens[k].Reading; }
        output().Add(new WordInfo(first)
        {
            Text = text, Reading = reading,
            DictionaryForm = unit + unit, NormalizedForm = unit + unit,
            PartOfSpeech = PartOfSpeech.Adverb,
            EndOffset = tokens[j - 1].EndOffset,
            PreMatchedWordId = twoXId
        });
        return j - i;
    }

    // A kana string that is its own leading 2-mora unit repeated a whole number of times (ごろ, ごろごろ,
    // ぐるぐるぐる); outputs the 2-char unit.
    private static bool IsKanaUnitRepetition(string s, out string unit)
    {
        unit = "";
        if (s.Length < 2 || s.Length % 2 != 0 || !JapaneseTextHelper.IsAllKana(s)) return false;
        unit = s[..2];
        return IsRepetitionOf(s, unit);
    }

    // True if s is the 2-char unit repeated a whole number of times (kana only).
    private static bool IsRepetitionOf(string s, string unit)
    {
        if (s.Length == 0 || s.Length % 2 != 0 || !JapaneseTextHelper.IsAllKana(s)) return false;
        for (int k = 0; k < s.Length; k += 2)
            if (s[k] != unit[0] || s[k + 1] != unit[1]) return false;
        return true;
    }

    // Sudachi occasionally mega-blobs an all-hiragana colloquial run into one OOV noun (ってもんじゃねえのかよ,
    // っぽくいってみようや) because of surrounding-lattice pressure; the same substring segments cleanly in
    // isolation. Re-run Sudachi on such a blob and splice the pieces, so they resolve instead of the whole
    // token dropping. Gated to hiragana/ー OOV tokens (≥5 chars) — katakana names / kanji compounds / romaji
    // are never touched — and only spliced when Sudachi actually splits it (>1 token). Results are memoized
    // per pass (the same colloquial blob recurs across a long text) and the whole stage disables itself on
    // the first FFI failure — a broken native context will not start working for the next blob.
    private List<WordInfo> RetokeniseOovBlobs(List<WordInfo> wordInfos)
    {
        if (_sudachiConfigPath == null || _sudachiDicPath == null || HasNonNameCompoundLookup == null
            || _retokeniseOovDisabled || !SudachiInterop.StreamingAvailable)
            return wordInfos;

        Dictionary<string, List<WordInfo>?>? memo = null;
        List<WordInfo>? result = null;
        for (int i = 0; i < wordInfos.Count; i++)
        {
            var w = wordInfos[i];
            bool candidate = w.Text.Length >= 5
                             && w.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                             && w.Text.All(c => c is >= 'ぁ' and <= 'ゟ' or 'ー')
                             && !HasNonNameCompoundLookup(w.Text)
                             // A token whose dictionary form is a different, resolvable word is not a
                             // Sudachi OOV blob (those carry their surface as the dictionary form) — it is
                             // an earlier repair's deliberate merge (わがまま+な → Text わがままな, dict form
                             // わがまま) and must not be torn apart again.
                             && (w.DictionaryForm == w.Text || string.IsNullOrEmpty(w.DictionaryForm)
                                 || !HasNonNameCompoundLookup(w.DictionaryForm));
            if (candidate)
            {
                memo ??= new Dictionary<string, List<WordInfo>?>(StringComparer.Ordinal);
                if (!memo.TryGetValue(w.Text, out var retok))
                {
                    try
                    {
                        retok = SudachiInterop.ProcessTextStreaming(_sudachiConfigPath, w.Text, _sudachiDicPath,
                                                                    mode: _sudachiMode, userDictCsv: _sudachiUserDictCsv);
                    }
                    catch (Exception ex)
                    {
                        // Keep this blob and stop retokenising for the rest of the parse.
                        Console.WriteLine($"[Warning] RetokeniseOovBlobs: Sudachi FFI failed on '{w.Text}', disabling retokenisation for this parse: {ex.Message}");
                        _retokeniseOovDisabled = true;
                        result?.Add(w);
                        for (int j = i + 1; j < wordInfos.Count; j++)
                            result?.Add(wordInfos[j]);
                        return result ?? wordInfos;
                    }

                    memo[w.Text] = retok;
                }

                if (retok is { Count: > 1 })
                {
                    result ??= CopyAccumulatorUpTo(wordInfos, i);
                    int off = w.StartOffset;
                    foreach (var rt in retok)
                    {
                        // Clone: the memoized pieces are spliced at every occurrence of the blob, and
                        // later stages mutate tokens in place.
                        var piece = new WordInfo(rt);
                        piece.StartOffset = off >= 0 ? off : -1;
                        off = off >= 0 ? off + piece.Text.Length : -1;
                        piece.EndOffset = off >= 0 ? off : -1;
                        result.Add(piece);
                    }
                    continue;
                }
            }
            result?.Add(w);
        }
        return result ?? wordInfos;
    }

    /// <summary>なく belongs to a bound auxiliary verb stem after a て-form (出て+こ+なく), not to a following なった.</summary>
    private static bool IsBoundAuxiliaryNegative(WordInfo w1, List<WordInfo> emitted)
    {
        if (w1.Text != "なく" || emitted.Count < 2)
            return false;

        var stem = emitted[^1];
        if (stem.PartOfSpeech != PartOfSpeech.Verb || !stem.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant))
            return false;

        var teForm = emitted[^2];
        return teForm.Text.EndsWith('て') || teForm.Text.EndsWith('で');
    }

    private List<WordInfo> ProcessSpecialCases(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count == 0)
            return wordInfos;

        List<WordInfo> newList = new List<WordInfo>(wordInfos.Count);


        for (int i = 0; i < wordInfos.Count;)
        {
            WordInfo w1 = wordInfos[i];

            // A kana したん… shape with dictionary form 湑む (したむ, dated "pour out every drop") is a
            // Sudachi mis-analysis of した (する past) + ん(だ) — the dated verb is written in kanji.
            // RepairNTokenisation already merged したん+だ→したんだ (losing the 湑む NormForm),
            // but the kana dict-form したむ survives — re-cut した (する past) + remainder.
            if (w1.DictionaryForm == "したむ" && w1.Text.StartsWith("した", StringComparison.Ordinal)
                && w1.Text.Length >= 3)
            {
                string rest = w1.Text[2..];
                int mid = w1.StartOffset >= 0 ? w1.StartOffset + 2 : -1;
                // Pin した→する (1157170): un-pinned, CombineAuxiliary re-merges した+んだ→したんだ which then
                // re-derives したむ→湑む again. The pin keeps the correct word (する) through that merge, with
                // the conjugation chain recovered explicitly (pins bypass deconjugation).
                newList.Add(new WordInfo(w1)
                {
                    Text = "した", DictionaryForm = "する", NormalizedForm = "為る",
                    PartOfSpeech = PartOfSpeech.Verb, Reading = "シタ",
                    PreMatchedWordId = 1157170, PreMatchedConjugations = PinnedConjugationProcess("した", "する"),
                    EndOffset = mid
                });
                newList.Add(new WordInfo(w1)
                {
                    Text = rest, DictionaryForm = rest, NormalizedForm = rest,
                    PartOfSpeech = PartOfSpeech.Auxiliary,
                    Reading = w1.Reading.Length > 2 ? w1.Reading[2..] : "",
                    PreMatchedWordId = rest == "んだ" ? 2849387 : null,
                    StartOffset = mid, EndOffset = w1.EndOffset
                });
                i += 1;
                continue;
            }

            // それっぽく…: Sudachi mis-tags それ's opening as a lone そ (adverb そう) and mega-blobs the rest
            // starting with れ. Re-cut そ + れ → それ (pronoun, 1006970); the remainder (っぽく…) re-tokenises
            // via RetokeniseOovBlobs below. Gated on the blob being OOV so genuine そ+れ-word pairs are safe.
            if (w1.Text == "そ" && i + 1 < wordInfos.Count
                && wordInfos[i + 1].Text.Length >= 3 && wordInfos[i + 1].Text[0] == 'れ'
                && HasNonNameCompoundLookup?.Invoke(wordInfos[i + 1].Text) == false)
            {
                var blob = wordInfos[i + 1];
                string rest = blob.Text[1..];
                int mid = w1.EndOffset >= 0 ? w1.EndOffset + 1 : -1;
                newList.Add(new WordInfo(w1)
                {
                    Text = "それ", DictionaryForm = "それ", NormalizedForm = "其れ",
                    PartOfSpeech = PartOfSpeech.Pronoun, Reading = "ソレ",
                    PreMatchedWordId = 1006970, EndOffset = mid
                });
                wordInfos[i + 1] = new WordInfo(blob)
                {
                    Text = rest, DictionaryForm = rest, NormalizedForm = rest, StartOffset = mid
                };
                i += 1;
                continue;
            }

            // Sudachi mega-blobs a る-ending verb whose て-continuation is a colloquial contraction it can't
            // parse (募るってもんじゃねえ → 募|るってもんじゃねえ). When a single-kanji Noun + the blob's leading
            // る forms a JMDict verb (募る), extract the verb; the remainder (ってもんじゃねえ) resegments on its
            // own. Gated on the blob being OOV so genuine 名詞+る words aren't split.
            if (w1.Text.Length == 1 && JapaneseTextHelper.IsKanji(w1.Text[0])
                && w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text.Length >= 3
                && wordInfos[i + 1].Text[0] == 'る'
                && HasNonNameCompoundLookup?.Invoke(wordInfos[i + 1].Text) == false
                && HasNonNameCompoundLookup?.Invoke(w1.Text + "る") == true)
            {
                var blob = wordInfos[i + 1];
                string rest = blob.Text[1..];
                int mid = w1.EndOffset >= 0 ? w1.EndOffset + 1 : -1;
                newList.Add(new WordInfo(w1)
                {
                    Text = w1.Text + "る", DictionaryForm = w1.Text + "る", NormalizedForm = w1.Text + "る",
                    PartOfSpeech = PartOfSpeech.Verb, Reading = "",
                    EndOffset = mid
                });
                wordInfos[i + 1] = new WordInfo(blob)
                {
                    Text = rest, DictionaryForm = rest, NormalizedForm = rest, StartOffset = mid
                };
                i += 1;
                continue;
            }

            // 〜つ目/〜つめ that Sudachi keeps as one token (四つ目, みっつめ→見詰める!) is
            // number + つ + the ordinal suffix 目 "-th" (1604890), not the noun "four-eyed" or a
            // verb. Split so it matches the split 三つ目; kana numerals are a closed set.
            {
                bool kanjiTsuMe = w1.Text.Length >= 3 && w1.Text.EndsWith("つ目", StringComparison.Ordinal)
                    && TakesOrdinalMeAfterTsu(w1.Text[0]);
                bool kanaTsuMe = w1.Text.EndsWith("つめ", StringComparison.Ordinal)
                    && w1.Text[..^1] is "ひとつ" or "ふたつ" or "みっつ" or "よっつ" or "いつつ"
                        or "むっつ" or "ななつ" or "やっつ" or "ここのつ";
                if (w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Verb
                    && (kanjiTsuMe || kanaTsuMe))
                {
                    var numPart = w1.Text[..^1];   // 四つ / みっつ
                    var meText = w1.Text[^1..];    // 目 / め
                    int mid = w1.StartOffset >= 0 ? w1.StartOffset + numPart.Length : -1;
                    newList.Add(new WordInfo(w1)
                    {
                        Text = numPart, DictionaryForm = numPart, NormalizedForm = numPart,
                        PartOfSpeech = PartOfSpeech.Noun,
                        Reading = w1.Reading.Length > 0 ? w1.Reading[..^1] : "", EndOffset = mid
                    });
                    newList.Add(new WordInfo
                    {
                        Text = meText, DictionaryForm = "目", NormalizedForm = "目",
                        PartOfSpeech = PartOfSpeech.Suffix, Reading = "メ",
                        PreMatchedWordId = 1604890, PreMatchedReadingIndex = 0, HardPinned = true,
                        StartOffset = mid, EndOffset = w1.EndOffset
                    });
                    i += 1;
                    continue;
                }
            }

            // X | Y達 → XY | 達: the pluralising suffix 達 binds looser than the compound XY, so Sudachi's
            // 部|下達 (下達 = a separate noun) becomes 部下|達 when X + Y-without-達 is a real compound.
            // The theft leaves a single-char fragment X (部) — a multi-char X next to a Y達 noun is two
            // genuine words (事実|上達していた) and must stay Sudachi's cut. The rebound compound must also
            // be in common use (部下, frequency rank < 40000) so a coincidental 1-char+Y lookup key can't
            // trigger a rebind on its own.
            if (w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && w1.Text.Length == 1
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { PartOfSpeech: PartOfSpeech.Noun or PartOfSpeech.CommonNoun } w2tachi
                && w2tachi.Text.Length >= 2 && w2tachi.Text.EndsWith("達", StringComparison.Ordinal)
                && HasNonNameCompoundLookup?.Invoke(w1.Text + w2tachi.Text[..^1]) == true
                && GetNonNameCompoundFrequencyRank?.Invoke(w1.Text + w2tachi.Text[..^1]) is < 40000)
            {
                var compound = w1.Text + w2tachi.Text[..^1];
                int mid = w2tachi.EndOffset >= 0 ? w2tachi.EndOffset - 1 : -1;
                newList.Add(new WordInfo(w1)
                {
                    Text = compound, DictionaryForm = compound, NormalizedForm = compound,
                    Reading = "", EndOffset = mid
                });
                newList.Add(new WordInfo(w2tachi)
                {
                    Text = "達", DictionaryForm = "達", NormalizedForm = "達",
                    PartOfSpeech = PartOfSpeech.Suffix, Reading = "タチ", StartOffset = mid
                });
                i += 2;
                continue;
            }

            // 前-rebinding: Sudachi greedily attaches 前 rightward (お|前山, この|前山) when 前 belongs to the
            // preceding word. Rebind [prev][前+rest] → [prev+前][rest] when prev+前 and rest are both real
            // words AND the 前-compound's 前 is the KUN reading (まえ/さき, e.g. 前山=サキヤマ) — never the ON
            // reading ゼン, which marks a tight Sino compound (前後=ゼンゴ, 前世=ゼンセイ, 前回) that must stay whole.
            // A 前-compound that is itself in common use (前髪, 前歯, 前置き, 前触れ) keeps its own reading —
            // この|前髪 is a correct Sudachi split, not a theft; only compounds nobody actually uses (前山)
            // exist because Sudachi stole 前 from the preceding word. JMDict priority tags can't make this
            // call (前山's homograph ぜんざん carries news-frequency tags), so gate on frequency rank.
            // A rest that is a verb 連用形 (掛け, 置き, 払い) marks a genuine deverbal 前+V-stem compound
            // (前掛け "apron") rather than a theft — those stay whole even when unranked (この|前掛け).
            if (w1.Text.Length >= 2 && w1.Text[0] == '前'
                && w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && !w1.Reading.StartsWith("ゼン", StringComparison.Ordinal)
                && GetNonNameCompoundFrequencyRank != null
                && GetNonNameCompoundFrequencyRank(w1.Text) is not < 40000
                && newList.Count > 0
                && HasNonNameCompoundLookup?.Invoke(newList[^1].Text + "前") == true
                && HasNonNameCompoundLookup?.Invoke(w1.Text[1..]) == true
                && RenyokeiSurfaceToVerb(w1.Text[1..]) == null)
            {
                var prev = newList[^1];
                string rest = w1.Text[1..];
                int mid = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1;
                newList[^1] = new WordInfo(prev)
                {
                    Text = prev.Text + "前", DictionaryForm = prev.Text + "前", NormalizedForm = prev.Text + "前",
                    Reading = "", EndOffset = mid
                };
                newList.Add(new WordInfo(w1)
                {
                    Text = rest, DictionaryForm = rest, NormalizedForm = rest,
                    Reading = "", StartOffset = mid
                });
                i += 1;
                continue;
            }

            // 親-prefix OOV: Sudachi emits an OOV noun 親X (親ソ, 親米) where 親 is the productive "pro-"
            // prefix (しん, 2256340). Split 親 + X when X is a JMDict word and 親X itself is NOT (so 親友/親指/
            // 親子, which ARE dictionary words, stay whole). The remainder resolves on its own merits (a
            // single-kana abbreviation like ソ may still drop at lookup — acceptable; 親 is recovered).
            // Only a single-character rest is the geopolitical pro- pattern (親ソ, 親米, 親日); a longer rest
            // means "parent" (親ギツネ, 親スレ) — leave that 親 unpinned so it resolves to おや.
            if (w1.Text.Length >= 2 && w1.Text[0] == '親'
                && w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && HasNonNameCompoundLookup?.Invoke(w1.Text) == false
                && HasNonNameCompoundLookup?.Invoke(w1.Text[1..]) == true)
            {
                string rest = w1.Text[1..];
                // Geopolitical abbreviations are katakana (ソ) or kanji (米/日); a hiragana rest is a
                // colloquial fragment where 親 means parent.
                bool isProPrefix = rest.Length == 1 && rest[0] is not (>= 'ぁ' and <= 'ゟ');
                int mid = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1;
                newList.Add(new WordInfo(w1)
                {
                    Text = "親", DictionaryForm = "親", NormalizedForm = "親",
                    PartOfSpeech = isProPrefix ? PartOfSpeech.Prefix : PartOfSpeech.Noun,
                    Reading = isProPrefix ? "シン" : "オヤ",
                    PreMatchedWordId = isProPrefix ? 2256340 : null, EndOffset = mid
                });
                newList.Add(new WordInfo(w1)
                {
                    Text = rest, DictionaryForm = rest, NormalizedForm = rest,
                    Reading = "", StartOffset = mid,
                    // Lone ソ never resolves to the Soviet-Union abbreviation on its own (generic noun
                    // ids come first in its lookup), so pin it; kanji rests resolve via normal lookup.
                    PreMatchedWordId = rest == "ソ" ? 2853158 : null
                });
                i += 1;
                continue;
            }

            // 〜くも emitted as a single OOV token (美しくも, 早くも) is the adj-i adverbial 〜く + the
            // particle も. Split when the 〜く part deconjugates to an i-adjective and the whole token
            // isn't itself a dictionary word.
            if (w1.Text.Length >= 4 && w1.Text.EndsWith("くも", StringComparison.Ordinal)
                && (HasCompoundLookup == null || !HasCompoundLookup(w1.Text))
                && Deconjugator.Instance.Deconjugate(NormalizeToHiragana(w1.Text[..^1]))
                    .Any(f => f.Tags.Any(t => t == "adj-i") && HasNonNameCompoundLookup?.Invoke(f.Text) == true))
            {
                var kuPart = w1.Text[..^1];
                int mid = w1.StartOffset >= 0 ? w1.StartOffset + kuPart.Length : -1;
                newList.Add(new WordInfo(w1)
                {
                    Text = kuPart, DictionaryForm = kuPart, NormalizedForm = kuPart,
                    PartOfSpeech = PartOfSpeech.IAdjective, Reading = "", EndOffset = mid
                });
                newList.Add(new WordInfo
                {
                    Text = "も", DictionaryForm = "も", NormalizedForm = "も",
                    PartOfSpeech = PartOfSpeech.Particle, Reading = "モ",
                    StartOffset = mid, EndOffset = w1.EndOffset
                });
                i += 1;
                continue;
            }

            // Sudachi splits an imperative/te-form like 当たれ into あ (interjection) + たれ (noun 垂れ).
            // Recombine when あ + the next token deconjugates to a real verb, before CombineVerbDependant
            // would otherwise steal the あ into the preceding verb (持って + あ).
            if (w1 is { Text: "あ", PartOfSpeech: PartOfSpeech.Interjection }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { PartOfSpeech: PartOfSpeech.Noun } w2at && w2at.Text.Length >= 2
                && DeconjugatesToVerbInLookup("あ" + w2at.Text))
            {
                newList.Add(new WordInfo(w1)
                {
                    Text = "あ" + w2at.Text, DictionaryForm = "あ" + w2at.Text, NormalizedForm = "あ" + w2at.Text,
                    PartOfSpeech = PartOfSpeech.Verb, Reading = "ア" + w2at.Reading,
                    EndOffset = w2at.EndOffset
                });
                i += 2;
                continue;
            }

            // Sudachi tags a causative stem (やらせ = 遣らせ) as a noun before ない. When (noun + ない) has
            // a causative/passive verb deconjugation it is that verb (やらせない → やる/やらせる), not 遣らせ +
            // 無い — merge so it resolves as the verb. Plain negatives (聞こえない, 仕方ない, 信用ない) lack a
            // causative/passive deconjugation and stay split.
            if (w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "ない"
                && DeconjugatesToCausativeOrPassiveVerb(w1.Text + "ない"))
            {
                var nai = wordInfos[i + 1];
                newList.Add(new WordInfo(w1)
                {
                    Text = w1.Text + "ない", DictionaryForm = w1.Text + "ない", NormalizedForm = w1.Text + "ない",
                    PartOfSpeech = PartOfSpeech.Verb, Reading = w1.Reading + nai.Reading,
                    EndOffset = nai.EndOffset
                });
                i += 2;
                continue;
            }

            // 形状詞可能 noun + か mis-split by Sudachi: a sentence-final particle (静かね) makes the
            // lattice re-cut a な-adjective ending in か (静か) into 静(名詞,形状詞可能)+か(終助詞).
            // Rejoin when the noun carries the 形状詞可能 tag and surface+か is a real (non-name) word —
            // the lookup gate keeps genuine "noun + question particle か" sequences untouched.
            if (w1.PartOfSpeech == PartOfSpeech.Noun
                && w1.HasPartOfSpeechSection(PartOfSpeechSection.PossibleNaAdjective)
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { DictionaryForm: "か", PartOfSpeech: PartOfSpeech.Particle }
                && (HasNonNameCompoundLookup ?? HasCompoundLookup)?.Invoke(w1.Text + "か") == true)
            {
                var ka = wordInfos[i + 1];
                newList.Add(new WordInfo(w1)
                {
                    Text = w1.Text + "か",
                    DictionaryForm = w1.Text + "か",
                    NormalizedForm = w1.Text + "か",
                    PartOfSpeech = PartOfSpeech.NaAdjective,
                    Reading = string.Empty,
                    EndOffset = ka.EndOffset
                });
                i += 2;
                continue;
            }

            // Katakana mora stolen from a name by a following hiragana-dict token: Sudachi cuts
            // カティア|の as カティ|アの (アの = 連体詞 あの) and カティア|と as カティ|アと (アと = 後 あと).
            // A token whose dict form is hiragana but whose surface starts with a katakana mora, with
            // a real particle (の/と/…) as the remainder, means that leading mora belongs to the
            // preceding katakana name — return it and emit the remainder as a particle.
            // (カティ+ア = カティア, kept as an OOV name token.)
            if (w1.PartOfSpeech is PartOfSpeech.PrenounAdjectival or PartOfSpeech.Noun
                && w1.Text.Length >= 2
                && JapaneseTextHelper.IsKatakanaWordChar(w1.Text[0]) && w1.Text[0] != 'ー'
                && w1.Text[1..].All(c => c is >= '぀' and <= 'ゟ')
                && CaseParticles.Contains(w1.Text[1..])
                && KanaConverter.ToHiragana(w1.Text) == w1.DictionaryForm
                && newList.Count > 0
                && newList[^1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.Name
                && JapaneseTextHelper.IsAllKatakana(newList[^1].Text))
            {
                var prev = newList[^1];
                var stolen = w1.Text[0].ToString();
                var remainder = w1.Text[1..];
                var mergedText = prev.Text + stolen;
                int mergedEndOffset = prev.EndOffset >= 0 ? prev.EndOffset + 1 : prev.EndOffset;
                newList[^1] = new WordInfo(prev)
                {
                    Text = mergedText,
                    DictionaryForm = mergedText,
                    NormalizedForm = mergedText,
                    Reading = prev.Reading + stolen,
                    EndOffset = mergedEndOffset
                };
                newList.Add(new WordInfo
                {
                    Text = remainder, DictionaryForm = remainder, NormalizedForm = remainder,
                    PartOfSpeech = PartOfSpeech.Particle,
                    Reading = WanaKanaShaapu.WanaKana.ToKatakana(remainder),
                    StartOffset = mergedEndOffset,
                    EndOffset = w1.EndOffset
                });
                i++;
                continue;
            }

            // A case-particle が cannot follow conjunctive から — the kana verb がなる "to yell"
            // (2101910, uk) is the only grammatical reading (頼むからがなるな). No dictionary
            // entry: a がなる row would steal ベルが鳴る-type splits lattice-wide.
            if (w1 is { Text: "が", PartOfSpeech: PartOfSpeech.Particle }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].DictionaryForm is "成る" or "なる"
                && wordInfos[i + 1].PartOfSpeech == PartOfSpeech.Verb
                && newList.Count > 0 && newList[^1].Text.EndsWith("から", StringComparison.Ordinal))
            {
                var naru = wordInfos[i + 1];
                newList.Add(new WordInfo(naru)
                {
                    Text = "が" + naru.Text,
                    DictionaryForm = "がなる",
                    NormalizedForm = "がなる",
                    Reading = "ガ" + naru.Reading,
                    StartOffset = w1.StartOffset,
                });
                i += 2;
                continue;
            }

            // Sudachi sometimes mis-analyses ちかい as 近い adjective when the preceding noun's full
            // dictionary form ends in ち (e.g. 太鼓持+ちかい → 太鼓持ち+かい). When prev.Text + ち is a
            // valid compound, transfer ち to the noun and emit かい as a sentence-final particle.
            if (w1 is { Text: "ちかい", PartOfSpeech: PartOfSpeech.IAdjective, NormalizedForm: "近い" }
                && newList.Count > 0
                && newList[^1].PartOfSpeech == PartOfSpeech.Noun
                && HasCompoundLookup != null
                && HasCompoundLookup(newList[^1].Text + "ち"))
            {
                var prev = newList[^1];
                prev.Text += "ち";
                prev.DictionaryForm = prev.Text;
                prev.NormalizedForm = prev.Text;
                if (prev.EndOffset >= 0) prev.EndOffset += 1;
                int kaiStart = prev.EndOffset;
                newList.Add(new WordInfo
                {
                    Text = "かい",
                    DictionaryForm = "かい",
                    NormalizedForm = "かい",
                    PartOfSpeech = PartOfSpeech.Particle,
                    Reading = "カイ",
                    StartOffset = kaiStart,
                    EndOffset = w1.EndOffset,
                });
                i++;
                continue;
            }

            // Sudachi lattice picks 私大 (private university) over 私+大X
            // (私大金持ち, 私大好き). Re-cut when 大+next is a real word.
            if (w1.Text == "私大" && i + 1 < wordInfos.Count
                && wordInfos[i + 1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.NaAdjective
                && HasNonNameCompoundLookup?.Invoke("大" + wordInfos[i + 1].Text) == true)
            {
                var nextW = wordInfos[i + 1];
                newList.Add(new WordInfo
                {
                    Text = "私", DictionaryForm = "私", NormalizedForm = "私",
                    PartOfSpeech = PartOfSpeech.Pronoun, Reading = "ワタシ",
                    StartOffset = w1.StartOffset, EndOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1
                });
                newList.Add(new WordInfo(nextW)
                {
                    Text = "大" + nextW.Text,
                    DictionaryForm = "大" + nextW.DictionaryForm,
                    NormalizedForm = "大" + nextW.NormalizedForm,
                    Reading = "ダイ" + nextW.Reading,
                    StartOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1
                });
                i += 2;
                continue;
            }

            // X史|上 → X|史上: 史上 (ichi1) binds tighter than the 史 suffix (人類史上初).
            // 史上+初 then merges into 史上初 via the expression whitelist.
            if (w1.PartOfSpeech == PartOfSpeech.Noun && w1.Text.Length >= 3 && w1.Text.EndsWith('史')
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { Text: "上", PartOfSpeech: PartOfSpeech.Suffix }
                && HasNonNameCompoundLookup?.Invoke(w1.Text[..^1]) == true)
            {
                var up = wordInfos[i + 1];
                newList.Add(new WordInfo(w1)
                {
                    Text = w1.Text[..^1], DictionaryForm = w1.Text[..^1], NormalizedForm = w1.Text[..^1],
                    EndOffset = w1.EndOffset >= 0 ? w1.EndOffset - 1 : -1
                });
                newList.Add(new WordInfo
                {
                    Text = "史上", DictionaryForm = "史上", NormalizedForm = "史上",
                    PartOfSpeech = PartOfSpeech.Noun, Reading = "シジョウ",
                    StartOffset = w1.EndOffset >= 0 ? w1.EndOffset - 1 : -1, EndOffset = up.EndOffset
                });
                i += 2;
                continue;
            }

            // Sudachi's 使いで (usability) steals the で: 魔法|使いで|も|ない → 魔法使い|でもない.
            // Only fires when the previous noun + stem is a real word (魔法使い).
            if (w1.PartOfSpeech == PartOfSpeech.Noun && w1.Text.Length >= 3 && w1.Text.EndsWith('で')
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "も"
                && newList.Count > 0 && newList[^1].PartOfSpeech == PartOfSpeech.Noun
                && HasNonNameCompoundLookup?.Invoke(newList[^1].Text + w1.Text[..^1]) == true)
            {
                var prevNoun = newList[^1];
                newList[^1] = new WordInfo(prevNoun)
                {
                    Text = prevNoun.Text + w1.Text[..^1],
                    DictionaryForm = prevNoun.Text + w1.Text[..^1],
                    NormalizedForm = prevNoun.Text + w1.Text[..^1],
                    EndOffset = w1.EndOffset >= 0 ? w1.EndOffset - 1 : -1
                };
                bool naiFollows = i + 2 < wordInfos.Count && wordInfos[i + 2].Text == "ない";
                newList.Add(new WordInfo
                {
                    Text = naiFollows ? "でもない" : "でも",
                    DictionaryForm = naiFollows ? "でもない" : "でも",
                    NormalizedForm = naiFollows ? "でもない" : "でも",
                    PartOfSpeech = naiFollows ? PartOfSpeech.Expression : PartOfSpeech.Conjunction,
                    Reading = naiFollows ? "デモナイ" : "デモ",
                    StartOffset = w1.EndOffset >= 0 ? w1.EndOffset - 1 : -1,
                    EndOffset = naiFollows ? wordInfos[i + 2].EndOffset : wordInfos[i + 1].EndOffset
                });
                i += naiFollows ? 3 : 2;
                continue;
            }

            // Classical attributive 無き after a noun merges when the noun+無い adjective exists
            // (詮|無き → 詮無き → 詮無い via the attributive-き deconj rule).
            if (w1.PartOfSpeech == PartOfSpeech.Noun && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { Text: "無き" }
                && HasNonNameCompoundLookup?.Invoke(w1.Text + "無い") == true)
            {
                newList.Add(new WordInfo(w1)
                {
                    Text = w1.Text + "無き",
                    DictionaryForm = w1.Text + "無い",
                    NormalizedForm = w1.Text + "無い",
                    PartOfSpeech = PartOfSpeech.IAdjective,
                    Reading = w1.Reading + "ナキ",
                    EndOffset = wordInfos[i + 1].EndOffset
                });
                i += 2;
                continue;
            }

            // Sudachi produces こか (verb こく 未然形) from mis-segmenting e.g.
            // とこかも → と+こか+も. Redistribute: prev+"こ" | "か"+next when both are known words.
            if (w1 is { Text: "こか", DictionaryForm: "こく", PartOfSpeech: PartOfSpeech.Verb }
                && newList.Count > 0
                && i + 1 < wordInfos.Count
                && HasCompoundLookup != null)
            {
                var prev = newList[^1];
                var w2 = wordInfos[i + 1];
                string prevPlusKo = prev.Text + "こ";
                string kaPlusNext = "か" + w2.Text;

                // Use the non-name lookup so we never reclassify the previous token into a coincidental
                // name homograph of prev+"こ"; this rewrite overwrites prev's POS/reading, so it must
                // only fire when prev+"こ" is a genuine (non-name) dictionary word.
                var nonNameLookup = HasNonNameCompoundLookup ?? HasCompoundLookup;
                if (nonNameLookup(prevPlusKo) && nonNameLookup(kaPlusNext))
                {
                    prev.Text = prevPlusKo;
                    prev.DictionaryForm = prevPlusKo;
                    prev.NormalizedForm = prevPlusKo;
                    prev.PartOfSpeech = PartOfSpeech.CommonNoun;
                    prev.Reading += "コ";
                    if (prev.EndOffset >= 0) prev.EndOffset += 1;
                    int kaStart = prev.EndOffset;

                    newList.Add(new WordInfo
                    {
                        Text = kaPlusNext,
                        DictionaryForm = kaPlusNext,
                        NormalizedForm = kaPlusNext,
                        PartOfSpeech = PartOfSpeech.Particle,
                        Reading = "カ" + w2.Reading,
                        StartOffset = kaStart,
                        EndOffset = w2.EndOffset,
                    });
                    i += 2;
                    continue;
                }
            }

            if (w1.PartOfSpeech == PartOfSpeech.IAdjective
                && w1.Text.Length > 1 && w1.Text[0] == 'て'
                && newList.Count > 0
                && newList[^1] is { Text: "なん", PartOfSpeech: PartOfSpeech.Pronoun })
            {
                var remainder = w1.Text[1..];
                int mid = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1;
                newList.Add(new WordInfo
                {
                    Text = "て", DictionaryForm = "て", NormalizedForm = "て",
                    PartOfSpeech = PartOfSpeech.Particle, Reading = "テ",
                    StartOffset = w1.StartOffset, EndOffset = mid,
                });
                newList.Add(new WordInfo
                {
                    Text = remainder, DictionaryForm = remainder, NormalizedForm = remainder,
                    PartOfSpeech = PartOfSpeech.IAdjective, Reading = w1.Reading.Length > 1 ? w1.Reading[1..] : "",
                    StartOffset = mid, EndOffset = w1.EndOffset,
                });
                i++;
                continue;
            }

            if (w1 is { PartOfSpeech: PartOfSpeech.Conjunction or PartOfSpeech.Auxiliary, Text: "で" })
            {
                bool nextIsMo = i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "も";
                if (!nextIsMo)
                {
                    w1.PartOfSpeech = PartOfSpeech.Particle;
                    newList.Add(w1);
                    i++;
                    continue;
                }
            }

            // Sudachi sometimes classifies verb te-forms ending in んで/んだ as 表現 (Expression)
            // when a JMDict expression entry exists (e.g., 飛んで → 2248530 "zero; flying").
            // Reclassify as Verb with the correct DictionaryForm so the parser matches the verb.
            if (w1 is { PartOfSpeech: PartOfSpeech.Expression, Text.Length: >= 3 }
                && (w1.Text.EndsWith("んで", StringComparison.Ordinal) || w1.Text.EndsWith("んだ", StringComparison.Ordinal)))
            {
                var hiragana = NormalizeToHiragana(w1.Text);
                var deconjForms = PipelineCachedDeconjugate(hiragana);
                var verbForm = deconjForms.FirstOrDefault(f =>
                    f.Tags.Any(t => t is "v5b" or "v5m" or "v5n" or "v5g") &&
                    (f.Text.EndsWith('ぶ') || f.Text.EndsWith('む') || f.Text.EndsWith('ぬ') || f.Text.EndsWith('ぐ')));
                if (verbForm != null)
                {
                    var prefix = w1.Text[..^2];
                    w1.PartOfSpeech = PartOfSpeech.Verb;
                    w1.DictionaryForm = prefix + verbForm.Text[^1];
                }
            }

            // Sudachi misclassifies 着 as noun suffix (ギ = clothing suffix) after nouns like 服,
            // but when followed by a particle/auxiliary it's the verb 着る (きる, to wear).
            // Exception: when the preceding noun forms a JMDict compound with 着 (部屋着, 晴れ着),
            // keep the suffix reading so CombineNounCompounds can join them.
            if (w1 is { Text: "着", PartOfSpeech: PartOfSpeech.Suffix, Reading: "ギ" }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary
                && !(i > 0 && wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                     && HasNonNameCompoundLookup?.Invoke(wordInfos[i - 1].Text + "着") == true))
            {
                w1.PartOfSpeech = PartOfSpeech.Verb;
                w1.DictionaryForm = "着る";
                w1.NormalizedForm = "着る";
                w1.Reading = "キ";
            }

            // Sudachi sometimes tags a verb-stem kanji as a Prefix and merges the stem's し 連用形
            // ending into the following compound verb (e.g. 殺し続ける → 殺 [Prefix, サツ] + し続ける [Verb]).
            // When the kanji+す is a valid v5s verb and the following token starts with し,
            // split it into (kanji+し, Verb stem form) + (rest, Verb) so the parser matches the real verb.
            if (w1 is { PartOfSpeech: PartOfSpeech.Prefix, Text.Length: 1 }
                && WanaKana.IsKanji(w1.Text)
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { PartOfSpeech: PartOfSpeech.Verb, Text.Length: >= 2 } next
                && next.Text[0] == 'し'
                && HasCompoundLookup != null
                && HasCompoundLookup(w1.Text + "す")
                && HasCompoundLookup(next.Text[1..]))
            {
                var stemOffsetEnd = next.StartOffset >= 0 ? next.StartOffset + 1 : -1;
                newList.Add(new WordInfo
                {
                    Text = w1.Text + "し",
                    DictionaryForm = w1.Text + "す",
                    NormalizedForm = w1.Text + "す",
                    PartOfSpeech = PartOfSpeech.Verb,
                    Reading = "",
                    StartOffset = w1.StartOffset,
                    EndOffset = stemOffsetEnd,
                });
                newList.Add(new WordInfo
                {
                    Text = next.Text[1..],
                    DictionaryForm = next.DictionaryForm?.StartsWith('し') == true
                        ? next.DictionaryForm[1..]
                        : next.Text[1..],
                    NormalizedForm = next.NormalizedForm?.StartsWith('し') == true
                        ? next.NormalizedForm[1..]
                        : next.Text[1..],
                    PartOfSpeech = PartOfSpeech.Verb,
                    Reading = "",
                    StartOffset = stemOffsetEnd,
                    EndOffset = next.EndOffset,
                });
                i += 2;
                continue;
            }

            // Combine 形状詞的 suffixes (げ) with preceding adjective stem
            // e.g., 幼(adj-stem) + げ(suffix/形状詞的) → 幼げ
            // Keep as IAdjective so な handler doesn't incorrectly merge (的な, がちな stay unchanged)
            if (w1.PartOfSpeech == PartOfSpeech.Suffix
                && w1.HasPartOfSpeechSection(PartOfSpeechSection.NaAdjectiveLike)
                && newList.Count > 0
                && newList[^1].PartOfSpeech == PartOfSpeech.IAdjective
                && !newList[^1].Text.EndsWith('い'))
            {
                newList[^1].Text += w1.Text;
                newList[^1].EndOffset = w1.EndOffset;
                i++;
                continue;
            }

            // のでは is always の+では (contrastive), never ので+は (causal+topic)
            // Emit の separately so CombineParticles can form で+は → では → ではない(か)
            if (w1.Text == "の" && i + 2 < wordInfos.Count
                && wordInfos[i + 1].Text == "で" && wordInfos[i + 2].Text == "は")
            {
                newList.Add(w1);
                i++;
                continue;
            }

            if (i < wordInfos.Count - 2)
            {
                WordInfo w2 = wordInfos[i + 1];
                WordInfo w3 = wordInfos[i + 2];

                // Colloquial 〜ておこう contraction: Sudachi splits [verb-stem] + と(particle) + こう(adverb)
                // e.g., ためとこう = ためておこう (let's save/store for now)
                if (w1.PartOfSpeech == PartOfSpeech.Noun
                    && JapaneseTextHelper.IsAllHiragana(w1.Text)
                    && w2 is { Text: "と", PartOfSpeech: PartOfSpeech.Particle }
                    && w3 is { Text: "こう", PartOfSpeech: PartOfSpeech.Adverb }
                    && HasCompoundLookup != null)
                {
                    var combined = w1.Text + "とこう";
                    var forms = PipelineCachedDeconjugate(combined);
                    var verbForm = forms.FirstOrDefault(f =>
                        f.Tags.Length > 0 && f.Tags.Length <= 6 &&
                        f.Tags.Any(t => t.StartsWith('v')) &&
                        HasCompoundLookup(f.Text));
                    if (verbForm != null)
                    {
                        newList.Add(new WordInfo(w1)
                        {
                            Text = combined,
                            DictionaryForm = verbForm.Text,
                            NormalizedForm = verbForm.Text,
                            PartOfSpeech = PartOfSpeech.Verb,
                            Reading = w1.Reading + "トコウ",
                            EndOffset = w3.EndOffset
                        });
                        i += 3;
                        continue;
                    }
                }

                bool found = false;
                if (SpecialCases3Dict.TryGetValue(w1.Text, out var sc3Candidates) && !IsBoundAuxiliaryNegative(w1, newList))
                {
                    foreach (var sc in sc3Candidates)
                    {
                        // Also match when RepairVowelElongation stripped trailing ー from a particle
                        bool thirdMatch = w3.Text == sc.Third ||
                            (sc.Third.Length > 1 && sc.Third[^1] == 'ー' && w3.Text == sc.Third[..^1]);
                        if (w2.Text == sc.Second && thirdMatch)
                        {
                            if (newList.Count > 0 && HasCompoundLookup != null &&
                                w2.PartOfSpeech == PartOfSpeech.Verb)
                            {
                                var prevWord = newList[^1];
                                var compoundDictForm = prevWord.Text + w1.Text + w2.DictionaryForm;
                                if (HasCompoundLookup(compoundDictForm))
                                {
                                    newList.RemoveAt(newList.Count - 1);
                                    var compoundWord = new WordInfo(prevWord);
                                    compoundWord.Text = prevWord.Text + w1.Text + w2.Text + sc.Third;
                                    compoundWord.EndOffset = w3.EndOffset;
                                    compoundWord.DictionaryForm = compoundDictForm;
                                    compoundWord.PartOfSpeech = PartOfSpeech.Verb;
                                    newList.Add(compoundWord);
                                    i += 3;
                                    found = true;
                                    break;
                                }
                            }

                            var newWord = new WordInfo(w1);
                            newWord.Text = w1.Text + w2.Text + sc.Third;
                            newWord.EndOffset = w3.EndOffset;
                            newWord.DictionaryForm = newWord.Text;

                            if (sc.Pos != null)
                                newWord.PartOfSpeech = sc.Pos.Value;

                            newList.Add(newWord);
                            i += 3;
                            found = true;
                            break;
                        }
                    }
                }

                if (found)
                    continue;

                // Special case: な + ん + だ should become なんだ (explanatory)
                // BUT only when not preceded by AuxiliaryVerbStem (like そう in 泣きそうな)
                // or NaAdjective (like 好き in 好きなんだ)
                if (w1.Text == "な" && w2.Text == "ん" && w3.Text == "だ")
                {
                    bool prevIsAuxVerbStem = i > 0 &&
                                             wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem);
                    bool prevIsNaAdjective = i > 0 &&
                                             wordInfos[i - 1].PartOfSpeech == PartOfSpeech.NaAdjective;
                    if (!prevIsAuxVerbStem && !prevIsNaAdjective)
                    {
                        var newWord = new WordInfo(w1) { Text = "なんだ", EndOffset = w3.EndOffset, DictionaryForm = "なんだ", PartOfSpeech = PartOfSpeech.Auxiliary };
                        newList.Add(newWord);
                        i += 3;
                        continue;
                    }
                }

                // Special case: な + ん (explanatory) when NOT followed by だ
                // e.g., そうなんじゃない → そう + なん + じゃない
                // Only when ん is 準体助詞 (explanatory particle)
                if (w1.Text == "な" && w2.Text == "ん" && w3.Text != "だ" &&
                    w2.HasPartOfSpeechSection(PartOfSpeechSection.Juntaijoushi))
                {
                    bool prevIsAuxVerbStem = i > 0 &&
                                             wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem);
                    bool prevIsNaAdjective = i > 0 &&
                                             wordInfos[i - 1].PartOfSpeech == PartOfSpeech.NaAdjective;
                    if (!prevIsAuxVerbStem && !prevIsNaAdjective)
                    {
                        var newWord = new WordInfo(w1) { Text = "なん", EndOffset = w2.EndOffset, DictionaryForm = "なん", PartOfSpeech = PartOfSpeech.Auxiliary };
                        newList.Add(newWord);
                        i += 2;
                        continue;
                    }
                }
            }

            if (i < wordInfos.Count - 1)
            {
                WordInfo w2 = wordInfos[i + 1];

                // ぶち (adverb intensifier) + verb → compound verb when JMDict entry exists
                // e.g., ぶち + キレ(キレる) → ぶちキレる (2118860)
                if (w1 is { Text: "ぶち", PartOfSpeech: PartOfSpeech.Adverb }
                    && w2.PartOfSpeech == PartOfSpeech.Verb
                    && HasCompoundLookup != null)
                {
                    var compoundDict = "ぶち" + w2.DictionaryForm;
                    if (HasCompoundLookup(compoundDict))
                    {
                        var merged = new WordInfo(w2);
                        merged.Text = "ぶち" + w2.Text;
                        merged.DictionaryForm = compoundDict;
                        merged.NormalizedForm = compoundDict;
                        merged.StartOffset = w1.StartOffset;
                        merged.Reading = "ブチ" + w2.Reading;
                        newList.Add(merged);
                        i += 2;
                        continue;
                    }
                }

                // Special case: ん + だ + DaCompoundSuffix should become ん + だ[suffix]
                // e.g., 飲んだけど → 飲ん + だけど (verb ん)
                // BUT: そうなんだけど → そう + なんだ + けど (explanatory ん - 準体助詞)
                // Only apply this for non-explanatory ん (not a 準体助詞 particle)
                bool isExplanatoryN = w1.PartOfSpeech == PartOfSpeech.Particle &&
                                      w1.HasPartOfSpeechSection(PartOfSpeechSection.Juntaijoushi);
                if (w1.Text == "ん" && w2.Text == "だ" && i + 2 < wordInfos.Count &&
                    DaCompoundSuffixes.Contains(wordInfos[i + 2].Text) &&
                    !isExplanatoryN)
                {
                    var w3 = wordInfos[i + 2];
                    newList.Add(w1); // Keep ん separate
                    var daSuffix = new WordInfo(w2) { Text = w2.Text + w3.Text, EndOffset = w3.EndOffset, PartOfSpeech = PartOfSpeech.Conjunction };
                    newList.Add(daSuffix);
                    i += 3;
                    continue;
                }

                // Sudachi splits はぐれる into は(particle) + ぐれる(verb) after で
                if (w1 is { Text: "は", PartOfSpeech: PartOfSpeech.Particle }
                    && w2 is { PartOfSpeech: PartOfSpeech.Verb, DictionaryForm: "ぐれる" })
                {
                    w2.Text = "は" + w2.Text;
                    w2.StartOffset = w1.StartOffset;
                    w2.DictionaryForm = "はぐれる";
                    w2.NormalizedForm = "はぐれる";
                    w2.Reading = "ハ" + w2.Reading;
                    newList.Add(w2);
                    i += 2;
                    continue;
                }

                // Sudachi splits Vたそう (seemingness of wanting to V) as
                // noun + いたそう (volitional of いたす) when the verb stem kanji is also a standalone noun.
                // e.g., 何か言いたそうな → 言(noun) + いたそう(いたす volitional)
                // Correct: 言い + た + そう = 言う + want + seemingness
                // The pattern only occurs with godan ワ行 verbs (dictionary form = kanji + う)
                if (w1.PartOfSpeech == PartOfSpeech.Noun
                    && w2 is { Text: "いたそう", DictionaryForm: "いたす", PartOfSpeech: PartOfSpeech.Verb })
                {
                    newList.Add(new WordInfo(w1)
                    {
                        Text = w1.Text + w2.Text,
                        EndOffset = w2.EndOffset,
                        PartOfSpeech = PartOfSpeech.Verb,
                        DictionaryForm = w1.Text + "う",
                        NormalizedForm = w1.Text + "う",
                    });
                    i += 2;
                    continue;
                }

                // Sudachi splits 来なすった as 来(動詞) + なすった(動詞/なさる).
                // Combine into a single compound honorific verb.
                if (w1 is { Text: "来", PartOfSpeech: PartOfSpeech.Verb, DictionaryForm: "来る" } &&
                    w2 is { DictionaryForm: "なさる" })
                {
                    newList.Add(new WordInfo(w1)
                    {
                        Text = w1.Text + w2.Text, EndOffset = w2.EndOffset,
                        DictionaryForm = "来なさる", PartOfSpeech = PartOfSpeech.Verb,
                        Reading = "キナスッタ"
                    });
                    i += 2;
                    continue;
                }

                // Sudachi splits そういう/こういう/ああいう/どういう into adverb + verb (いう).
                // Combine them so the parser can match the dictionary entry (e.g., そういう = WordId 1394680).
                // Preserve verb DictionaryForm so CombineInflections can absorb conjugation suffixes
                // (e.g., そういった, そういって).
                // Restrict to kana 言 — the kanji form 言って almost always means the literal verb
                // "to say" (e.g. そう言ってありがたい), while そういう/そういった as "such/that kind of"
                // is conventionally written in kana.
                if ((w1.PartOfSpeech == PartOfSpeech.Adverb
                        || (w1.PartOfSpeech == PartOfSpeech.Interjection && w1.Text is "ああ" or "あー")) &&
                    w1.Text is "そう" or "こう" or "ああ" or "どう"
                             or "そー" or "こー" or "あー" or "どー" &&
                    w2.DictionaryForm is "いう" or "言う"
                    && w2.Text.Length > 0 && w2.Text[0] == 'い')
                {
                    newList.Add(new WordInfo(w1)
                    {
                        Text = w1.Text + w2.Text, EndOffset = w2.EndOffset,
                        Reading = w1.Reading + w2.Reading,
                        DictionaryForm = w1.Text + w2.DictionaryForm,
                        PartOfSpeech = PartOfSpeech.Verb
                    });
                    i += 2;
                    continue;
                }

                bool found = false;
                if (SpecialCases2Dict.TryGetValue(w1.Text, out var sc2Candidates))
                {
                    // か+い / だ+い stay split when the い is the 居る stem claimed by a following
                    // auxiliary (聞こえているのか|いない|のか) — merging would steal the verb stem.
                    bool kaIBlocked = w1.Text is "か" or "だ" && i + 2 < wordInfos.Count
                        && w2.DictionaryForm is "居る" or "いる"
                        && wordInfos[i + 2].Text is "ない" or "なかった" or "ます" or "た" or "て";

                    // ところ+で → ところで (1343110) only sentence-initially or after a past form (〜たところで);
                    // mid-sentence after a non-past stem it is the locative ところ + で (静かなところで, 今のところで).
                    bool tokoroDeBlocked = w1.Text == "ところ" && i > 0 &&
                        !(wordInfos[i - 1].Text.EndsWith("た", StringComparison.Ordinal) ||
                          wordInfos[i - 1].Text.EndsWith("だ", StringComparison.Ordinal));

                    // それ+じゃ stays split before a negative (それじゃない = それ + じゃない), not the conjunction それじゃ.
                    bool soreJaBlocked = w1.Text is "それ" or "そん" or "そい" && w2.Text == "じゃ"
                        && i + 2 < wordInfos.Count && wordInfos[i + 2].Text is "ない" or "なかっ" or "なかった";

                    // 度+に → 度に (たびに, "each time", 1007270) only after a verb attributive (行う度に);
                    // after a numeral it is the counter 度 + に (二度に分けて).
                    bool doNiBlocked = w1.Text == "度" && w2.Text == "に"
                        && !(i > 0 && wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb);

                    // つ+か → つか (つーか) must not steal the counter つ from a preceding numeral
                    // (三つ|か四つ); right after a number the counter reading is the only possible one.
                    bool tsuKaBlocked = w1.Text == "つ" && w2.Text == "か" && i > 0 &&
                        (wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Numeral ||
                         (wordInfos[i - 1].NormalizedForm.Length > 0 &&
                          wordInfos[i - 1].NormalizedForm.All(char.IsAsciiDigit)));

                    foreach (var sc in sc2Candidates)
                    {
                        if (sc.Second == "い" && kaIBlocked) continue;
                        if (sc.Second == "で" && tokoroDeBlocked) continue;
                        if (sc.Second == "じゃ" && soreJaBlocked) continue;
                        if (sc.Second == "に" && doNiBlocked) continue;
                        if (sc.Second == "か" && tsuKaBlocked) continue;
                        if (w2.Text == sc.Second
                            && !(sc.Pos == PartOfSpeech.Verb && w1.PartOfSpeech == PartOfSpeech.Conjunction))
                        {
                            var newWord = new WordInfo(w1) { Text = w1.Text + w2.Text, EndOffset = w2.EndOffset };

                            if (sc.Pos == PartOfSpeech.Verb &&
                                !string.IsNullOrEmpty(w1.DictionaryForm) &&
                                w1.DictionaryForm != w1.Text)
                            {
                                newWord.DictionaryForm = w1.DictionaryForm;
                            }
                            else
                            {
                                newWord.DictionaryForm = newWord.Text;
                            }

                            if (sc.Pos != null)
                                newWord.PartOfSpeech = sc.Pos.Value;

                            newList.Add(newWord);
                            i += 2;
                            found = true;
                            break;
                        }
                    }
                }

                if (found)
                    continue;
            }


            if (w1.Text == "だし" && w1.PartOfSpeech != PartOfSpeech.Verb && newList.Count > 0)
            {
                var da = new WordInfo
                         {
                             Text = "だ", DictionaryForm = "だ", PartOfSpeech = PartOfSpeech.Auxiliary,
                             PartOfSpeechSection1 = PartOfSpeechSection.None, Reading = "だ",
                             StartOffset = w1.StartOffset, EndOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1
                         };
                var shi = new WordInfo
                          {
                              Text = "し", DictionaryForm = "し", PartOfSpeech = PartOfSpeech.Conjunction,
                              PartOfSpeechSection1 = PartOfSpeechSection.None, Reading = "し",
                              StartOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1, EndOffset = w1.EndOffset
                          };

                newList.Add(da);
                newList.Add(shi);
                i++;
                continue;
            }

            // Handle な based on context
            if (w1 is { Text: "な", DictionaryForm: "だ" })
            {
                bool followedByN = i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "ん";

                // If followed by explanatory ん pattern (な + ん + だ), combine into なんだ
                // e.g., 好き + な + ん + だ → 好き + なんだ
                // Also includes quotative particle と: 好き + な + ん + だ + と → 好き + なんだと
                if (newList.Count > 0 && IsNaAdjectiveToken(newList[^1]) && followedByN)
                {
                    // Build "なんだ" by combining な + ん + plain copula だ only
                    // Don't consume conjectural だろ/だろう — those are separate grammar points
                    string combined = "な" + wordInfos[i + 1].Text;
                    int j = i + 2;
                    int lastEndOffset = wordInfos[i + 1].EndOffset;
                    if (j < wordInfos.Count && wordInfos[j].Text == "だ" && wordInfos[j].PartOfSpeech == PartOfSpeech.Auxiliary)
                    {
                        combined += wordInfos[j].Text;
                        lastEndOffset = wordInfos[j].EndOffset;
                        j++;
                    }

                    // Also include quotative particle と if it immediately follows
                    if (j < wordInfos.Count && wordInfos[j].Text == "と" && wordInfos[j].PartOfSpeech == PartOfSpeech.Particle)
                    {
                        combined += wordInfos[j].Text;
                        lastEndOffset = wordInfos[j].EndOffset;
                        j++;
                    }

                    w1.Text = combined;
                    w1.EndOffset = lastEndOffset;
                    w1.DictionaryForm = combined;
                    w1.PartOfSpeech = PartOfSpeech.Auxiliary;
                    newList.Add(w1);
                    i = j;
                    continue;
                }

                // Sudachi splits なし into な(copula) + し(conjunction). When preceded by a na-adjective,
                // this would incorrectly merge noun+な while orphaning し. Detect and recombine as なし.
                bool followedByShi = i + 1 < wordInfos.Count
                    && wordInfos[i + 1] is { Text: "し", PartOfSpeech: PartOfSpeech.Conjunction };
                if (followedByShi && newList.Count > 0 && IsNaAdjectiveToken(newList[^1]))
                {
                    newList.Add(new WordInfo
                    {
                        Text = "なし", DictionaryForm = "なし", NormalizedForm = "無し",
                        PartOfSpeech = PartOfSpeech.Noun, Reading = "ナシ",
                        StartOffset = w1.StartOffset, EndOffset = wordInfos[i + 1].EndOffset
                    });
                    i += 2;
                    continue;
                }

                // If previous token is na-adjective and NOT followed by ん, combine with na-adjective
                // e.g., 大切 + な → 大切な, 静か + な + 部屋 → 静かな + 部屋
                // BUT: Exclude AuxiliaryVerbStem (like そう in 降りそうな) - keep な separate for learning
                if (newList.Count > 0 && IsNaAdjectiveToken(newList[^1]) && !followedByN
                    && !newList[^1].HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem))
                {
                    newList[^1].Text += w1.Text;
                    newList[^1].EndOffset = w1.EndOffset;
                    i++;
                    continue;
                }

                // Otherwise, treat as particle (not the vegetable 菜)
                w1.PartOfSpeech = PartOfSpeech.Particle;
            }
            // Always process に as the particle and not the baggage
            else if (w1.Text == "に")
                w1.PartOfSpeech = PartOfSpeech.Particle;

            newList.Add(w1);
            i++;
        }

        return newList;
    }

    /// <summary>
    /// Repairs orphaned conjugation fragments that follow nouns due to Sudachi incorrectly
    /// merging a noun+verb compound into a single noun token.
    /// Handles two patterns:
    /// 1. Orphaned voice auxiliary: 足蹴(noun) + られた(aux) → 足(noun) + 蹴られた(verb)
    /// 2. Orphaned verb ending: 肉食(noun) + う(filler) → 肉(noun) + 食う(verb)
    /// Uses a backward-looking window on the noun to find a valid verb stem via JMDict lookup.
    /// </summary>
    private List<WordInfo> RepairOrphanedAuxiliary(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2 || HasCompoundLookup == null)
            return wordInfos;

        var result = new List<WordInfo>(wordInfos.Count + 2);
        bool changed = false;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (i == 0)
            {
                result.Add(word);
                continue;
            }

            // A bare 連用形 noun (言い出し 2827230) is really a compound verb's stem when an orphaned past/te
            // auxiliary follows: swap its final 連用形 kana to the godan dict ending (言い出し→言い出す) and, if
            // that's a JMDict verb, reform the WHOLE token as the verb, absorbing the た/て (言い出した). This
            // differs from the noun-SPLIT below (言い+出す would be wrong here).
            // Only た/て: after a 連用形 noun, だ/で is the copula (手伝いだ "it's help"), not a verb past.
            if (word is { PartOfSpeech: PartOfSpeech.Auxiliary, Text: "た" or "て" }
                && result[^1] is { PartOfSpeech: PartOfSpeech.Noun } renyoNoun
                && renyoNoun.Text.Length >= 2 && renyoNoun.DictionaryForm == renyoNoun.Text)
            {
                char dictEnd = renyoNoun.Text[^1] switch
                {
                    'し' => 'す', 'り' => 'る', 'き' => 'く', 'ぎ' => 'ぐ', 'み' => 'む',
                    'び' => 'ぶ', 'に' => 'ぬ', 'ち' => 'つ', 'い' => 'う', _ => '\0'
                };
                if (dictEnd != '\0')
                {
                    string verbDict = renyoNoun.Text[..^1] + dictEnd;
                    if (verbDict.Any(c => c is >= '一' and <= '龯') && HasCompoundLookup(verbDict))
                    {
                        result[^1] = new WordInfo(renyoNoun)
                        {
                            Text = renyoNoun.Text + word.Text,
                            DictionaryForm = verbDict, NormalizedForm = verbDict,
                            PartOfSpeech = PartOfSpeech.Verb,
                            EndOffset = word.EndOffset
                        };
                        changed = true;
                        continue;
                    }
                }
            }

            // Lattice expression units ending in だ (そりゃそうだ) eat the だ of a following だろう,
            // stranding ろう as a noun (蝋). Reattach it: surface+ろう is the presumptive form of
            // the same expression, so the dictionary form stays.
            if (word is { Text: "ろう", PartOfSpeech: PartOfSpeech.Noun }
                && result[^1] is { PartOfSpeech: PartOfSpeech.Expression } prevExpr
                && prevExpr.Text.EndsWith('だ'))
            {
                result[^1] = new WordInfo(prevExpr)
                {
                    Text = prevExpr.Text + "ろう",
                    EndOffset = word.EndOffset
                };
                changed = true;
                continue;
            }

            bool isOrphanedAuxiliary = word.PartOfSpeech == PartOfSpeech.Auxiliary
                                       && VerbIndicatingAuxiliaries.Contains(word.DictionaryForm);
            bool isOrphanedVerbEnding = !isOrphanedAuxiliary
                                        && word.Text.Length == 1
                                        && GodanVerbEndings.Contains(word.Text);

            if (!isOrphanedAuxiliary && !isOrphanedVerbEnding)
            {
                result.Add(word);
                continue;
            }

            var prev = result[^1];
            if (prev.PartOfSpeech != PartOfSpeech.Noun || prev.Text.Length < 2)
            {
                result.Add(word);
                continue;
            }

            int maxWindow = Math.Min(prev.Text.Length - 1, 3);
            bool repaired = false;

            for (int w = 1; w <= maxWindow && !repaired; w++)
            {
                string verbStem = prev.Text[^w..];

                if (!verbStem.Any(c => c is >= '\u4E00' and <= '\u9FAF'))
                    continue;

                if (isOrphanedVerbEnding)
                {
                    string dictForm = verbStem + word.Text;
                    string nounRemainder = prev.Text[..^w];
                    if (HasCompoundLookup(dictForm) && HasCompoundLookup(nounRemainder))
                    {
                        ApplyNounVerbSplit(prev, word, nounRemainder, verbStem, dictForm, result);
                        repaired = true;
                    }
                }
                else
                {
                    foreach (var ending in GodanVerbEndings)
                    {
                        string dictForm = verbStem + ending;
                        string nounRemainder = prev.Text[..^w];
                        if (HasCompoundLookup(dictForm) && HasCompoundLookup(nounRemainder))
                        {
                            ApplyNounVerbSplit(prev, word, nounRemainder, verbStem, dictForm, result);
                            repaired = true;
                            break;
                        }
                    }
                }
            }

            if (repaired) changed = true;

            if (!repaired)
                result.Add(word);
        }

        return changed ? result : wordInfos;
    }

    private static void ApplyNounVerbSplit(
        WordInfo noun, WordInfo orphan, string nounRemainder, string verbStem, string dictForm, List<WordInfo> result)
    {
        int w = noun.Text.Length - nounRemainder.Length;
        int origNounEnd = noun.EndOffset;
        noun.Text = nounRemainder;
        noun.EndOffset = noun.StartOffset >= 0 ? noun.StartOffset + nounRemainder.Length : -1;
        if (noun.DictionaryForm.EndsWith(verbStem, StringComparison.Ordinal))
            noun.DictionaryForm = noun.DictionaryForm[..^w];
        if (noun.NormalizedForm.EndsWith(verbStem, StringComparison.Ordinal))
            noun.NormalizedForm = noun.NormalizedForm[..^w];

        result.Add(new WordInfo
        {
            Text = verbStem + orphan.Text,
            DictionaryForm = dictForm,
            NormalizedForm = dictForm,
            PartOfSpeech = PartOfSpeech.Verb,
            StartOffset = origNounEnd >= 0 ? origNounEnd - w : -1,
            EndOffset = orphan.EndOffset,
        });
    }

    // Sentence-final particles that Sudachi fuses onto interjections.
    private static List<WordInfo> RepairHasaNoun(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count == 0) return wordInfos;

        var result = new List<WordInfo>(wordInfos.Count + 2);

        foreach (var word in wordInfos)
        {
            if (word.Text != "はさ" || word.PartOfSpeech != PartOfSpeech.Noun)
            {
                result.Add(word);
                continue;
            }

            result.Add(new WordInfo
            {
                Text = "は",
                DictionaryForm = "は",
                NormalizedForm = "は",
                PartOfSpeech = PartOfSpeech.Particle,
                StartOffset = word.StartOffset,
                EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
            });
            result.Add(new WordInfo
            {
                Text = "さ",
                DictionaryForm = "さ",
                NormalizedForm = "さ",
                PartOfSpeech = PartOfSpeech.Particle,
                StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1,
                EndOffset = word.EndOffset
            });
        }

        return result;
    }

    private static readonly HashSet<string> SentenceFinalParticles = ["ね", "ねえ", "な", "なあ", "よ", "よね", "さ", "わ", "の", "のよ", "もの"];

    /// <summary>
    /// Splits Sudachi-fused interjection tokens that absorbed a trailing sentence-final particle.
    /// E.g. ごめんなさいね → ごめんなさい (int) + ね (prt).
    /// Sudachi sometimes fuses them into one interjection token with no JMDict match.
    /// </summary>
    private List<WordInfo> RepairFusedInterjectionParticle(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count == 0) return wordInfos;

        var result = new List<WordInfo>(wordInfos.Count + 2);

        foreach (var word in wordInfos)
        {
            if (word.PartOfSpeech != PartOfSpeech.Interjection || word.Text.Length < 3)
            {
                result.Add(word);
                continue;
            }

            if (HasCompoundLookup != null && HasCompoundLookup(word.Text))
            {
                result.Add(word);
                continue;
            }

            bool split = false;
            foreach (var particle in SentenceFinalParticles.OrderByDescending(p => p.Length))
            {
                if (!word.Text.EndsWith(particle, StringComparison.Ordinal)) continue;

                var baseText = word.Text[..^particle.Length];
                if (baseText.Length < 2) continue;

                // Only split when base itself looks like a known interjection (check lookup).
                if (HasCompoundLookup != null && !HasCompoundLookup(baseText)) continue;

                result.Add(new WordInfo
                {
                    Text = baseText,
                    DictionaryForm = baseText,
                    NormalizedForm = baseText,
                    PartOfSpeech = PartOfSpeech.Interjection,
                    StartOffset = word.StartOffset,
                    EndOffset = word.StartOffset >= 0 ? word.StartOffset + baseText.Length : -1
                });
                result.Add(new WordInfo
                {
                    Text = particle,
                    DictionaryForm = particle,
                    NormalizedForm = particle,
                    PartOfSpeech = PartOfSpeech.Particle,
                    StartOffset = word.StartOffset >= 0 ? word.StartOffset + baseText.Length : -1,
                    EndOffset = word.EndOffset
                });
                split = true;
                break;
            }

            if (!split)
                result.Add(word);
        }

        return result;
    }

    // A trailing run of ≥2 identical small vowels (ぇぇぇ, ぁぁ) is expressive elongation, not part
    // of a word. Returns the length of the leading core (≥1) when such a run is present.
    private static bool IsTrailingSmallVowelRun(string text, out int coreLen)
    {
        coreLen = text.Length;
        const string smallVowels = "ぁぃぅぇぉ";
        int i = text.Length - 1;
        char runChar = '\0';
        int runCount = 0;
        while (i >= 0 && smallVowels.IndexOf(text[i]) >= 0 && (runCount == 0 || text[i] == runChar))
        {
            runChar = text[i];
            runCount++;
            i--;
        }
        coreLen = i + 1;
        return runCount >= 2 && coreLen >= 1;
    }

    private static readonly HashSet<string> CaseParticles =
        ["に", "を", "が", "へ", "で", "と", "は", "も", "か", "から", "より", "まで", "の"];

    private static readonly HashSet<string> CommonTeFormVerbs =
        ["なる", "する", "やる", "いる", "ある", "くる", "できる", "おる", "みる", "しまう", "よる"];

    // Te-forms of these are いって — the stolen い may only reattach to the previous token when
    // that actually produces a JMDict word (悪|いっ|て → 悪い ✓, ギシギシ|いっ|て → ギシギシい ✗).
    private static readonly HashSet<string> IuIkuDictForms = ["いう", "言う", "いく", "行く"];

    // Demonstratives whose exclamation homograph (それっ!) Sudachi prefers before って;
    // the stripped form is re-tagged Pronoun so lookup picks the everyday word.
    private static readonly HashSet<string> QuotativeStrippedPronouns = ["それ", "これ", "あれ", "どれ"];

    // Clause-final shapes that can precede sentence-final かな. Nouns and case particles
    // (に/と) are deliberately excluded so どうにかなって/なんとかなって/夢かなって keep
    // their te-form-of-なる/かなう reading.
    private static bool IsClauseFinalBeforeKana(WordInfo w) =>
        (w.PartOfSpeech == PartOfSpeech.Particle && w.Text == "の")
        || w.PartOfSpeech == PartOfSpeech.IAdjective
        || (w.PartOfSpeech is PartOfSpeech.Verb && w.Text == w.DictionaryForm)
        || (w.PartOfSpeech == PartOfSpeech.Auxiliary && w.Text is "ない" or "た" or "だ" or "です" or "ます" or "てる" or "でる");

    // True when text deconjugates in exactly one step to a clause-final conjugation
    // (imperative/volitional) of a real JMDict word. Stem/infinitive chains are rejected so
    // genuine te-forms (信じきって) never match while quotative re-cuts (信じろ+って) do.
    private bool MergesToFinalForm(string text)
    {
        if (HasCompoundLookup == null) return false;
        foreach (var f in Deconjugator.Instance.Deconjugate(text))
        {
            if (f.Process.Length != 1 || string.IsNullOrEmpty(f.Text)) continue;
            var p = f.Process[0];
            if ((p.Contains("imperative", StringComparison.Ordinal) || p.Contains("volitional", StringComparison.Ordinal))
                && HasCompoundLookup(f.Text))
                return true;
        }

        return false;
    }

    private static bool HasOneStepVolitional(string text)
    {
        foreach (var f in Deconjugator.Instance.Deconjugate(text))
            if (f.Process.Length == 1 && f.Process[0].Contains("volitional", StringComparison.Ordinal))
                return true;

        return false;
    }

    // Quotative って stealing the な of an interrogative なに(何): Sudachi reads ってなに as the colloquial
    // tag ってな(=という) + に, or splits it って|な|に. The colloquial ってな only ever takes a noun head
    // (ってな具合/ってな歌), never the bare particle に — so な+に right after って is always 何 (vs the
    // colloquial ってな+noun form). Recombine both shapes to って + なに.
    private List<WordInfo> RepairTteNani(List<WordInfo> wordInfos) =>
        ScanRewrite(wordInfos, static (tokens, i, _, output) =>
        {
            WordInfo Nani(WordInfo basis, int startOffset, int endOffset) => new(basis)
            {
                Text = "なに", DictionaryForm = "なに", NormalizedForm = "何", Reading = "ナニ",
                PartOfSpeech = PartOfSpeech.Noun, PartOfSpeechSection1 = PartOfSpeechSection.None,
                PreMatchedWordId = 1577100,
                StartOffset = startOffset, EndOffset = endOffset
            };

            // Fused: ってな + に  ->  って + なに
            if (tokens[i].Text == "ってな" && i + 1 < tokens.Count && tokens[i + 1].Text == "に")
            {
                var result = output();
                var tteNa = tokens[i];
                var ni = tokens[i + 1];
                int mid = tteNa.StartOffset >= 0 ? tteNa.StartOffset + 2 : -1;
                result.Add(new WordInfo(tteNa)
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って", Reading = "ッテ",
                    PartOfSpeech = PartOfSpeech.Particle, EndOffset = mid
                });
                result.Add(Nani(ni, mid, ni.EndOffset));
                return 2;
            }

            // Split: って + な + に  ->  って + なに
            if (tokens[i].Text == "って" && i + 2 < tokens.Count
                && tokens[i + 1].Text == "な" && tokens[i + 2].Text == "に")
            {
                var result = output();
                var na = tokens[i + 1];
                var ni = tokens[i + 2];
                result.Add(tokens[i]);
                result.Add(Nani(na, na.StartOffset, ni.EndOffset));
                return 3;
            }

            return 0;
        });

    private List<WordInfo> RepairQuotativeTte(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        var deconj = Deconjugator.Instance;
        var result = new List<WordInfo>(wordInfos.Count + 2);
        bool changed = false;

        void AddTte(WordInfo thiefToken, WordInfo teToken)
        {
            result.Add(new WordInfo(teToken)
            {
                Text = "って",
                DictionaryForm = "って",
                NormalizedForm = "って",
                PartOfSpeech = PartOfSpeech.Particle,
                StartOffset = thiefToken.EndOffset >= 0 ? thiefToken.EndOffset - 1 : -1,
                EndOffset = teToken.EndOffset
            });
        }

        // A quotative って can steal the tail mora(s) of a noun/nominalised word, leaving the
        // word shredded across the preceding tokens (寒|さって, 繋|がりっ|て, 婆|さ|んって). Walk back
        // over up to 3 contiguous kana/kanji tokens; return how many, prepended to `add`, form a
        // real non-name JMDict word (寒+さ=寒さ, 繋+がり=繋がり, 婆+さ+ん=婆さん). 0 = no reattachment.
        // The lookup gate is what keeps genuine te-forms out (黙って, 言って never reform a noun).
        // Returns the LONGEST run that resolves, so 繋|が|り reforms 繋がり, not the shorter coincidental がり.
        int WordReattachRunLength(string add)
        {
            if (HasNonNameCompoundLookup == null || add.Length == 0) return 0;
            string acc = add;
            int best = 0;
            for (int k = 1; k <= 3 && k <= result.Count; k++)
            {
                // An auxiliary stem in the run is a verb/polite ending (ませ+ん = ません), not a stolen noun
                // mora — leave it to RepairN/CombineAuxiliary, so わかってませんって is not mis-merged through ません.
                // The case/quotative particles が・と・を・に never belong inside a reattached noun: stop so
                // が+行って/と+いって/に+かぶって keep their verbs (が行, 問い, にかぶ would otherwise attest). Topic は likewise —
                // except clause-initially, where a bare は can only be a shredded word head (は|ずっ|て = はず).
                // A Verb token's tail belongs to the verb machinery (生き|て|い|こうっ|て must
                // reassemble as 生きていこう, not steal いこう as a noun) — never walk into it.
                // An っ-final "verb" (いっ in いっ|しょっ|て) is itself a shred, not a stem — it
                // passes; so does a bare す (a shredded する/すぎ stem: びびり|す|ぎっ). す can also
                // be a genuine contracted する stem (すんだ), so walking through it is safe only
                // because the lookup gate below must still attest the reformed word.
                if ((result[^k].PartOfSpeech == PartOfSpeech.Verb
                        && !result[^k].Text.EndsWith('っ') && result[^k].Text != "す")
                    || result[^k].PartOfSpeech == PartOfSpeech.Auxiliary
                    || result[^k] is { PartOfSpeech: PartOfSpeech.Particle, Text: "が" or "と" or "を" or "に" }) break;
                if (result[^k] is { PartOfSpeech: PartOfSpeech.Particle, Text: "は" })
                {
                    bool clauseInitial = result.Count <= k
                        || result[^(k + 1)].PartOfSpeech is PartOfSpeech.Symbol
                            or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace;
                    if (!clauseInitial) break;
                }
                var t = result[^k].Text;
                if (t.Length == 0) break;
                // The particle で heads exactly one shred — the copula です (で|すっ|て); anything
                // else crossing it is two real tokens meeting (で+も=デモ, で+すく=デスク).
                if (result[^k] is { PartOfSpeech: PartOfSpeech.Particle, Text: "で" } && t + acc != "です") break;
                bool kanaOrKanji = true;
                foreach (var c in t)
                    if (c is not ((>= 'ぁ' and <= 'ゖ') or (>= '゠' and <= 'ヿ') or (>= '一' and <= '鿿'))) { kanaOrKanji = false; break; }
                if (!kanaOrKanji) break;
                acc = t + acc;
                if (HasNonNameCompoundLookup(acc))
                {
                    // A long all-kana result whose run pieces are each complete standalone words
                    // (どう+こう) is two words meeting, not a shred reforming — a real shred run
                    // has at least one fragment with no entry of its own (いっ in いっ+しょ).
                    // An っ-final piece (あっ, いっ) never counts as complete: it is shred-shaped
                    // even when an interjection homograph attests.
                    bool allPiecesAreWords = acc.Length >= 4 && acc.All(c => c is >= 'ぁ' and <= 'ゖ');
                    if (allPiecesAreWords)
                        for (int m = 1; m <= k && allPiecesAreWords; m++)
                            allPiecesAreWords = !result[^m].Text.EndsWith('っ')
                                                && HasNonNameCompoundLookup(result[^m].Text);
                    if (!allPiecesAreWords)
                        best = k;
                }
            }
            // Decline if the chosen run is immediately preceded by a lone single-kana Noun/Symbol token:
            // consolidating the run into a complete word strands that kana, and the downstream stutter
            // filter (FilterOrphanedMisparses) then deletes it, dropping a character. Leaving the run
            // unreattached keeps the kana joinable to it (the baseline combine merges the two shreds).
            // The kana range matches the set that filter treats as droppable (hiragana through ゟ, full
            // katakana), so every kana it would delete is caught here — including the iteration marks ゝ ゞ.
            if (best > 0 && result.Count > best)
            {
                var lead = result[^(best + 1)];
                if (lead.Text.Length == 1
                    && lead.Text[0] is (>= 'ぁ' and <= 'ゟ') or (>= '゠' and <= 'ヿ')
                    && lead.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.Symbol)
                    return 0;
            }
            return best;
        }

        // Replace the trailing `runLen` tokens of result with one Noun token whose text is the run
        // plus the reattached mora(s) `add` (e.g. 繋+がり → 繋がり); the caller then emits って.
        void MergeRunInto(int runLen, string add, int mergedEnd)
        {
            int n = result.Count;
            var basis = result[n - runLen];
            string mergedText = "", mergedReading = "";
            for (int x = n - runLen; x < n; x++) { mergedText += result[x].Text; mergedReading += result[x].Reading; }
            mergedText += add;
            mergedReading += WanaKanaShaapu.WanaKana.ToKatakana(add);
            result.RemoveRange(n - runLen, runLen);
            result.Add(new WordInfo(basis)
            {
                Text = mergedText, DictionaryForm = mergedText, NormalizedForm = mergedText, Reading = mergedReading,
                PartOfSpeech = PartOfSpeech.Noun,
                PartOfSpeechSection1 = PartOfSpeechSection.None,
                PartOfSpeechSection2 = PartOfSpeechSection.None,
                PartOfSpeechSection3 = PartOfSpeechSection.None,
                EndOffset = mergedEnd
            });
        }

        // A stranded-mora reattachment counts as a real verb when it is a dictionary-form godan/
        // ichidan verb (in JMDict, う-row ending) or deconjugates one step to an imperative/
        // volitional (従え → 従う). The う-row ending rejects noun fragments the deconjugator
        // over-tags (はだ/んだ "deconjugate" to verb pasts but never end う-row).
        bool IsVerbReattachment(string s) =>
            (s.Length >= 2
                && s[^1] is 'う' or 'く' or 'ぐ' or 'す' or 'つ' or 'ぬ' or 'ぶ' or 'む' or 'る'
                && HasNonNameCompoundLookup?.Invoke(s) == true
                && DeconjugatesToVerb(s))
            || MergesToFinalForm(s);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            // だって (single Conjunction/Particle token) + ば after a nominal is the copula だ + emphatic
            // ってば (2130420), not the "even/after all" conjunction だって (ダルマザメだってば → だ + ってば).
            if (i + 1 < wordInfos.Count
                && wordInfos[i].Text == "だって"
                && wordInfos[i].PartOfSpeech is PartOfSpeech.Conjunction or PartOfSpeech.Particle
                && wordInfos[i + 1].Text == "ば"
                && result.Count > 0
                && result[^1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    or PartOfSpeech.Pronoun or PartOfSpeech.Name or PartOfSpeech.Suffix)
            {
                var datte = wordInfos[i];
                int mid = datte.StartOffset >= 0 ? datte.StartOffset + 1 : -1;
                result.Add(new WordInfo(datte)
                {
                    Text = "だ", DictionaryForm = "だ", NormalizedForm = "だ", Reading = "ダ",
                    PartOfSpeech = PartOfSpeech.Auxiliary, EndOffset = mid
                });
                result.Add(new WordInfo(datte)
                {
                    Text = "ってば", DictionaryForm = "ってば", NormalizedForm = "ってば", Reading = "ッテバ",
                    PartOfSpeech = PartOfSpeech.Particle, PreMatchedWordId = 2130420,
                    StartOffset = mid, EndOffset = wordInfos[i + 1].EndOffset
                });
                i++;
                changed = true;
                continue;
            }

            // Sudachi mis-splits the colloquial particle だって (誰だって, 将校だって) into だっ (mis-tagged
            // as the verb だつ) + て after a noun/pronoun. The canonical だって handlers don't fire here —
            // SplitTatteParticle ran earlier (Split group) and is gated on a single だって token — so
            // reconstruct the particle, matching 誰だって → 誰 + だって(Particle). After a noun this is the
            // inclusive/emphatic だって ("even"), never a stolen verb mora; left untouched it gets hijacked
            // by the mora-theft block below into a fake verb 将校だ (→ 将校 + past).
            if (i + 1 < wordInfos.Count
                && wordInfos[i] is { Text: "だっ", DictionaryForm: "だつ" }
                && wordInfos[i + 1].Text == "て"
                && result.Count > 0
                && result[^1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    or PartOfSpeech.Pronoun or PartOfSpeech.Name or PartOfSpeech.Suffix)
            {
                var dattsu = wordInfos[i];
                result.Add(new WordInfo(dattsu)
                {
                    Text = "だって", DictionaryForm = "だって", NormalizedForm = "だって", Reading = "ダッテ",
                    PartOfSpeech = PartOfSpeech.Particle,
                    PartOfSpeechSection1 = PartOfSpeechSection.ConjunctionParticle,
                    EndOffset = wordInfos[i + 1].EndOffset
                });
                i++;
                changed = true;
                continue;
            }



            // いたって[Adverb] homograph: before a 言う-form and after a て-form (していた), a case
            // particle or a topic (部署に|いた, 人が|いた) it is いた (past of いる) + quotative って,
            // not the adverb いたって ("extremely") — the adverb modifies a following predicate, never
            // a verb of saying. って is emitted standalone; CombineQuotativeToIu clusters it with いう.
            if (result.Count > 0
                && wordInfos[i] is { Text: "いたって", PartOfSpeech: PartOfSpeech.Adverb }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].DictionaryForm is "いう" or "言う"
                && (result[^1].Text.EndsWith("て", StringComparison.Ordinal)
                    || result[^1].Text.EndsWith("で", StringComparison.Ordinal)
                    || result[^1] is { PartOfSpeech: PartOfSpeech.Particle, Text: "が" or "に" or "は" or "も" }))
            {
                var fused = wordInfos[i];
                int mid = fused.StartOffset >= 0 ? fused.StartOffset + 2 : -1;
                result.Add(new WordInfo(fused)
                {
                    Text = "いた", DictionaryForm = "いる", NormalizedForm = "いる", Reading = "イタ",
                    PartOfSpeech = PartOfSpeech.Verb, EndOffset = mid
                });
                result.Add(new WordInfo(fused)
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って", Reading = "ッテ",
                    PartOfSpeech = PartOfSpeech.Particle, StartOffset = mid
                });
                changed = true;
                continue;
            }



            // Katakana noun whose tail mora(s) a quotative って steals. Sudachi either strands them as a
            // pseudo-verb mora (サナダ|ムシっ[ムシる]|て) or fuses them into an idiom token
            // (エリ|アっての — ア+って+の matched to the idiom あっての). Both leave a leading katakana run K
            // before っ; if prev + K is a real JMDict katakana word the split is spurious, so reattach K
            // and hand って back. The lookup gate keeps genuine katakana verbs (サボってる) and complete
            // words untouched (they never leave a katakana fragment as the previous token).
            if (result.Count > 0 && JapaneseTextHelper.IsAllKatakana(result[^1].Text) && HasNonNameCompoundLookup != null)
            {
                var cur = wordInfos[i].Text;
                int kl = 0;
                while (kl < cur.Length && JapaneseTextHelper.IsKatakanaWordChar(cur[kl])) kl++;

                if (kl >= 1 && kl < cur.Length && cur[kl] == 'っ'
                    && HasNonNameCompoundLookup(result[^1].Text + cur[..kl]))
                {
                    var prevKata = result[^1];
                    var kataWord = prevKata.Text + cur[..kl];
                    var rest = cur[kl..];
                    int kEnd = wordInfos[i].StartOffset >= 0 ? wordInfos[i].StartOffset + kl : -1;

                    // Stranded mora: the て is the following token (サナダ|ムシっ|て).
                    if (rest == "っ" && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "て")
                    {
                        result[^1] = new WordInfo(prevKata)
                        {
                            Text = kataWord, DictionaryForm = kataWord, NormalizedForm = kataWord,
                            PartOfSpeech = PartOfSpeech.Noun, EndOffset = kEnd
                        };
                        AddTte(wordInfos[i], wordInfos[i + 1]);
                        i++;
                        changed = true;
                        continue;
                    }

                    // Fused って with an optional grammatical tail (アっての → って + の).
                    if (rest.StartsWith("って", StringComparison.Ordinal))
                    {
                        result[^1] = new WordInfo(prevKata)
                        {
                            Text = kataWord, DictionaryForm = kataWord, NormalizedForm = kataWord,
                            PartOfSpeech = PartOfSpeech.Noun, EndOffset = kEnd
                        };
                        result.Add(new WordInfo(wordInfos[i])
                        {
                            Text = "って", DictionaryForm = "って", NormalizedForm = "って",
                            PartOfSpeech = PartOfSpeech.Particle,
                            StartOffset = kEnd,
                            EndOffset = kEnd >= 0 ? kEnd + 2 : -1
                        });
                        var tail = rest[2..];
                        if (tail.Length > 0)
                            result.AddRange(TokenizeGrammarRemainder(tail, kEnd >= 0 ? kEnd + 2 : -1));
                        changed = true;
                        continue;
                    }
                }
            }

            // Interjection homograph stealing the っ of a quotative って: それっ|て → それ + って.
            // Sudachi's own NormalizedForm vouches for the stripped form (それっ → それ), so the
            // re-cut is safe; without it CombineTte glues the pair back into a bogus "te form"
            // of the exclamation entry. Demonstrative strips are re-tagged Pronoun so the lookup
            // matches それ/これ the word, not それ! the shout.
            if (i + 1 < wordInfos.Count
                && wordInfos[i].PartOfSpeech == PartOfSpeech.Interjection
                && wordInfos[i].Text.Length >= 2
                && wordInfos[i].Text[^1] == 'っ'
                && wordInfos[i + 1].Text == "て"
                && wordInfos[i].NormalizedForm == wordInfos[i].Text[..^1])
            {
                var interjThief = wordInfos[i];
                var interjStripped = interjThief.Text[..^1];
                result.Add(new WordInfo(interjThief)
                {
                    Text = interjStripped,
                    DictionaryForm = interjStripped,
                    NormalizedForm = interjStripped,
                    PartOfSpeech = QuotativeStrippedPronouns.Contains(interjStripped)
                        ? PartOfSpeech.Pronoun
                        : interjThief.PartOfSpeech,
                    EndOffset = interjThief.EndOffset >= 0 ? interjThief.EndOffset - 1 : -1
                });
                AddTte(interjThief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // がる suffix misread before a quotative って: 何|がっ|て → 何 + が + って.
            // がる attaches to adjective stems (怖がる), never to a pronoun, so after a pronoun
            // the only grammatical cut is case-particle が + quotative って.
            if (i + 1 < wordInfos.Count
                && wordInfos[i] is { Text: "がっ", PartOfSpeech: PartOfSpeech.Suffix, DictionaryForm: "がる" }
                && wordInfos[i + 1].Text == "て"
                && result.Count > 0 && result[^1].PartOfSpeech == PartOfSpeech.Pronoun)
            {
                var gaThief = wordInfos[i];
                result.Add(new WordInfo
                {
                    Text = "が",
                    DictionaryForm = "が",
                    NormalizedForm = "が",
                    PartOfSpeech = PartOfSpeech.Particle,
                    Reading = "ガ",
                    StartOffset = gaThief.StartOffset,
                    EndOffset = gaThief.StartOffset >= 0 ? gaThief.StartOffset + 1 : -1
                });
                AddTte(gaThief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // Sudachi strands a verb's final mora on a thief token ending っ, mis-tagging the thief
            // as an interjection/adverb/auxiliary/verb: 読|むっ|て, 泳|ぐっ|て, 行|くっ|て, 待|つっ|て,
            // 話|すっ|て, 従|えっ|て. The thief's POS guess is noise — the reliable signal is that the
            // stranded mora reattaches to the preceding stem to make a real verb, its dictionary form
            // (読む) or a one-step imperative/volitional (従え, 殴り合え). Prefer folding a 連用形 stem +
            // suffix (殴り+合) over the lone previous token. A genuine interjection (あっ|て) leaves no
            // verb behind it and is left untouched.
            if (i + 1 < wordInfos.Count
                && wordInfos[i].Text.Length == 2
                && wordInfos[i].Text[^1] == 'っ'
                && wordInfos[i].Text[0] is >= 'ぁ' and <= 'ゖ'
                && wordInfos[i].Text[0] != 'だ'   // copula だ is never a stranded verb-final mora (だって ≠ a verb)
                && wordInfos[i + 1].Text == "て"
                && wordInfos[i].PartOfSpeech is not (PartOfSpeech.Particle or PartOfSpeech.Pronoun)
                && result.Count > 0)
            {
                var thiefMora = wordInfos[i].Text[0];

                int stemBack = 0;
                if (result.Count >= 2 && result[^1].PartOfSpeech == PartOfSpeech.Suffix
                    && IsVerbReattachment(result[^2].Text + result[^1].Text + thiefMora))
                    stemBack = 2;
                else if (result[^1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                             or PartOfSpeech.Verb or PartOfSpeech.Prefix or PartOfSpeech.Suffix
                         && IsVerbReattachment(result[^1].Text + thiefMora))
                    stemBack = 1;

                if (stemBack > 0)
                {
                    var moraThief = wordInfos[i];
                    var stemHead = result[result.Count - stemBack];
                    var verbText = "";
                    for (int k = result.Count - stemBack; k < result.Count; k++) verbText += result[k].Text;
                    verbText += thiefMora;
                    result.RemoveRange(result.Count - stemBack, stemBack);
                    result.Add(new WordInfo(stemHead)
                    {
                        Text = verbText,
                        DictionaryForm = verbText,
                        NormalizedForm = verbText,
                        PartOfSpeech = PartOfSpeech.Verb,
                        EndOffset = moraThief.StartOffset >= 0 ? moraThief.StartOffset + 1 : -1
                    });
                    AddTte(moraThief, wordInfos[i + 1]);
                    i++;
                    changed = true;
                    continue;
                }
            }

            // A stranded mora that reattaches to a NON-verb word — ありがと|うっ|て → ありがとう (interjection),
            // where the mora isn't a verb ending so the verb block above passes it by. Tight: only the
            // interjection mora うっ (dict うっ, not the verb うつ that おはよう/そう already reform through),
            // a hiragana-ending interjection/noun prev, and prev+う forming a real non-name JMDict word.
            if (i + 1 < wordInfos.Count
                && wordInfos[i] is { Text: "うっ", DictionaryForm: "うっ" }
                && wordInfos[i + 1].Text == "て"
                && result.Count > 0
                && result[^1].PartOfSpeech is PartOfSpeech.Interjection or PartOfSpeech.Noun
                    or PartOfSpeech.CommonNoun or PartOfSpeech.Filler
                && result[^1].Text[^1] is >= 'ぁ' and <= 'ゖ'
                && HasNonNameCompoundLookup?.Invoke(result[^1].Text + "う") == true)
            {
                var moraThief = wordInfos[i];
                var reattached = result[^1].Text + "う";
                result[^1] = new WordInfo(result[^1])
                {
                    Text = reattached, DictionaryForm = reattached, NormalizedForm = reattached,
                    EndOffset = moraThief.StartOffset >= 0 ? moraThief.StartOffset + 1 : -1
                };
                AddTte(moraThief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // Sudachi splits a compound noun whose final kanji absorbs って's っ into a real-but-
            // here-wrong verb te-form (自意識 → 自|意|識っ[識る], 認識 → 認|識っ; 識る "to know" is a
            // genuine verb, just not the reading inside a noun compound). That reading breaks the
            // noun run so the lookup's compound matcher can't reform 自意識. Hand the stolen っ back to
            // って and re-tag the kanji as a noun, so the matcher reforms the compound and っていう
            // clusters. Gated on the kanji plus its preceding kanji compound-part — Noun/Suffix/Prefix
            // (the 接尾辞 認 in 認識), or an adjective/verb stem Sudachi mis-tags the compound's leading
            // bare kanji as (若造 → 若[若い 形容詞]|造っ[造る 動詞]) — forming a real JMDict compound noun.
            // The bare-kanji predecessor gate is what keeps the broadened POS safe: a genuine 若い/識る
            // carries okurigana (若い, 識って) so its predecessor is never a lone kanji, and Parser.cs
            // CombineNounCompounds already reforms an adj/verb-stem + noun compound (でかぶつ, 飛び道具).
            if (i + 1 < wordInfos.Count
                && wordInfos[i] is { PartOfSpeech: PartOfSpeech.Verb }
                && wordInfos[i].Text.Length == 2
                && wordInfos[i].Text[^1] == 'っ'
                && wordInfos[i].Text[0] is >= '一' and <= '鿿'
                && wordInfos[i + 1].Text == "て"
                && result.Count > 0
                && result[^1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    or PartOfSpeech.Suffix or PartOfSpeech.Prefix
                    or PartOfSpeech.IAdjective or PartOfSpeech.Verb
                && result[^1].Text[^1] is >= '一' and <= '鿿')
            {
                var kanjiThief = wordInfos[i];
                var tailKanji = kanjiThief.Text[..^1];
                bool formsNoun =
                    HasNonNameCompoundLookup?.Invoke(result[^1].Text + tailKanji) == true
                    || (result.Count >= 2
                        && result[^2].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                            or PartOfSpeech.Suffix or PartOfSpeech.Prefix
                        && result[^2].Text[^1] is >= '一' and <= '鿿'
                        && HasNonNameCompoundLookup?.Invoke(result[^2].Text + result[^1].Text + tailKanji) == true);

                if (formsNoun)
                {
                    result.Add(new WordInfo(kanjiThief)
                    {
                        Text = tailKanji,
                        DictionaryForm = tailKanji,
                        NormalizedForm = tailKanji,
                        PartOfSpeech = PartOfSpeech.Noun,
                        EndOffset = kanjiThief.StartOffset >= 0 ? kanjiThief.StartOffset + 1 : -1
                    });
                    AddTte(kanjiThief, wordInfos[i + 1]);
                    i++;
                    changed = true;
                    continue;
                }
            }

            // 達 (たち) mis-split by Sudachi as past-た + ちう-auxiliary before a quotative って:
            // 貴方|た|ちっ|て → 貴方 + たち + って. A pronoun/noun cannot take the past auxiliary た,
            // so this sequence is only ever the pluralising suffix 達 followed by quotative って.
            if (i + 1 < wordInfos.Count
                && wordInfos[i] is { Text: "ちっ", PartOfSpeech: PartOfSpeech.Auxiliary, DictionaryForm: "ちう" }
                && wordInfos[i + 1].Text == "て"
                && result.Count >= 2
                && result[^1] is { Text: "た", PartOfSpeech: PartOfSpeech.Auxiliary }
                && result[^2].PartOfSpeech is PartOfSpeech.Pronoun or PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
            {
                var chiThief = wordInfos[i];
                result[^1] = new WordInfo(result[^1])
                {
                    Text = "たち",
                    DictionaryForm = "たち",
                    NormalizedForm = "達",
                    Reading = "タチ",
                    PartOfSpeech = PartOfSpeech.Suffix,
                    EndOffset = chiThief.StartOffset >= 0 ? chiThief.StartOffset + 1 : -1
                };
                AddTte(chiThief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // んっ[Interjection] + て — the split shape of the ん-mora theft when a particle follows
            // (婆さんってね → 婆|さ|んっ|て|ね). The Interjection POS is filtered out by the gate below, so
            // reattach ん via the lookup here: さ+ん → さん, which 婆/おじい then reform downstream.
            if (i + 1 < wordInfos.Count
                && wordInfos[i] is { Text: "んっ", PartOfSpeech: PartOfSpeech.Interjection }
                && wordInfos[i + 1].Text == "て"
                && result.Count > 0)
            {
                int nRun = WordReattachRunLength("ん");
                if (nRun > 0)
                {
                    MergeRunInto(nRun, "ん", wordInfos[i].EndOffset >= 0 ? wordInfos[i].EndOffset - 1 : -1);
                    AddTte(wordInfos[i], wordInfos[i + 1]);
                    i++;
                    changed = true;
                    continue;
                }
            }

            // Auxiliary/Interjection thieves are Sudachi's lemmatisations of stolen morae too
            // (育|ちっ[Auxiliary]|て = 育ち+って, た|めっ[Interjection]|て = ため+って); the genuine
            // auxiliary shapes (だっ|て, ちゃっ|て) are covered by the だって block above and the
            // reattach attestation gates.
            // The っつー contraction family steals morae exactly like って (びびり|す|ぎっ|つーか:
            // the ぎ belongs to すぎ, the っ to っつーか). Reattach through the same run and hand
            // the っ back to the contraction; only the noun-run applies — the contraction never
            // continues a genuine te-form.
            if (wordInfos[i].Text.Length > 1 && wordInfos[i].Text[^1] == 'っ'
                && i + 1 < wordInfos.Count
                && (wordInfos[i + 1].Text.StartsWith("つー", StringComparison.Ordinal)
                    || wordInfos[i + 1].Text.StartsWith("つう", StringComparison.Ordinal)
                    || wordInfos[i + 1].Text.StartsWith("ちゅう", StringComparison.Ordinal))
                && result.Count > 0)
            {
                var strippedMora = wordInfos[i].Text[..^1];
                if (strippedMora.All(c => c is (>= 'ぁ' and <= 'ゖ') or (>= '一' and <= '鿿')))
                {
                    int tsuRun = WordReattachRunLength(strippedMora);
                    if (tsuRun > 0)
                    {
                        MergeRunInto(tsuRun, strippedMora, wordInfos[i].EndOffset >= 0 ? wordInfos[i].EndOffset - 1 : -1);
                        var contraction = wordInfos[i + 1];
                        result.Add(new WordInfo(contraction)
                        {
                            Text = "っ" + contraction.Text,
                            Reading = "ッ" + contraction.Reading,
                            StartOffset = wordInfos[i].EndOffset >= 0 ? wordInfos[i].EndOffset - 1 : -1,
                        });
                        i++;
                        changed = true;
                        continue;
                    }
                }
            }

            // Fused theft, kanji head: Sudachi occasionally swallows the whole thing into one
            // token (勉|強って with 強って lemmatised as たって). A kanji head before って whose
            // reattach run reforms a real word (勉+強=勉強) is the same theft one merge earlier.
            if (wordInfos[i].Text.Length >= 3
                && wordInfos[i].Text.EndsWith("って", StringComparison.Ordinal)
                && wordInfos[i].Text[..^2].All(JapaneseTextHelper.IsKanji)
                && result.Count > 0)
            {
                var head = wordInfos[i].Text[..^2];
                int headRun = WordReattachRunLength(head);
                if (headRun > 0)
                {
                    MergeRunInto(headRun, head, wordInfos[i].StartOffset >= 0 ? wordInfos[i].StartOffset + head.Length : -1);
                    result.Add(new WordInfo(wordInfos[i])
                    {
                        Text = "って", DictionaryForm = "って", NormalizedForm = "って", Reading = "ッテ",
                        PartOfSpeech = PartOfSpeech.Particle,
                        StartOffset = wordInfos[i].StartOffset >= 0 ? wordInfos[i].StartOffset + head.Length : -1
                    });
                    changed = true;
                    continue;
                }
            }

            if (i + 1 >= wordInfos.Count
                || wordInfos[i].Text.Length < 2
                || wordInfos[i].Text[^1] != 'っ'
                || wordInfos[i + 1].Text != "て"
                || wordInfos[i].PartOfSpeech is not (PartOfSpeech.Verb or PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    or PartOfSpeech.Adverb or PartOfSpeech.Auxiliary or PartOfSpeech.Interjection
                    or PartOfSpeech.Particle))
            {
                result.Add(wordInfos[i]);
                continue;
            }

            var thief = wordInfos[i];
            var stripped = thief.Text[..^1];
            var prev = result.Count > 0 ? result[^1] : null;

            // のかなって: clause + か|なっ|て → clause + かな + って. Gated on a clause-final
            // token before か so the question particle reading is certain.
            if (thief.Text == "なっ" && prev is { PartOfSpeech: PartOfSpeech.Particle, Text: "か" }
                && result.Count >= 2 && IsClauseFinalBeforeKana(result[^2]))
            {
                result[^1] = new WordInfo(prev)
                {
                    Text = "かな",
                    DictionaryForm = "かな",
                    NormalizedForm = "かな",
                    EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                };
                AddTte(thief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // Prohibitive/exclamatory な quoted by って: 言うな|って, すごいな|って, そうだな|って,
            // 取りたいな|って. Terminal form + なる te-form is ungrammatical, so this re-cut is safe
            // (たい/ない + なる would be たくなって/なくなって, never たいなって/ないなって).
            if (thief.Text == "なっ" && prev != null
                && ((prev.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective && prev.Text == prev.DictionaryForm)
                    || (prev.PartOfSpeech == PartOfSpeech.Auxiliary && prev.Text is "だ" or "た" or "です" or "ます" or "たい" or "ない")))
            {
                result.Add(new WordInfo
                {
                    Text = "な",
                    DictionaryForm = "な",
                    NormalizedForm = "な",
                    PartOfSpeech = PartOfSpeech.Particle,
                    StartOffset = thief.StartOffset,
                    EndOffset = thief.StartOffset >= 0 ? thief.StartOffset + 1 : -1
                });
                AddTte(thief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // Volitional う stolen by うなっ: 言|うなっ|て → 言う + な + って, だろ|うなっ|て →
            // だろう + な + って. Genuine うなる (growl) always follows a particle, which the
            // prev-POS check excludes.
            if (thief.Text == "うなっ" && prev != null
                && prev.PartOfSpeech is not (PartOfSpeech.Particle or PartOfSpeech.Auxiliary
                    or PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol or PartOfSpeech.BlankSpace)
                && (HasCompoundLookup?.Invoke(prev.Text + "う") == true || HasOneStepVolitional(prev.Text + "う")))
            {
                result[^1] = new WordInfo(prev)
                {
                    Text = prev.Text + "う",
                    EndOffset = prev.EndOffset >= 0 ? prev.EndOffset + 1 : -1
                };
                result.Add(new WordInfo
                {
                    Text = "な",
                    DictionaryForm = "な",
                    NormalizedForm = "な",
                    PartOfSpeech = PartOfSpeech.Particle,
                    StartOffset = thief.StartOffset >= 0 ? thief.StartOffset + 1 : -1,
                    EndOffset = thief.StartOffset >= 0 ? thief.StartOffset + 2 : -1
                });
                AddTte(thief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // って eating the final mora of a katakana word: デー|トっ|て → デート + って.
            // A mid-katakana cut before って is never a real boundary, so shape alone suffices
            // (covers OOV names too).
            if (stripped.Length == 1 && stripped[0] != 'ー' && JapaneseTextHelper.IsKatakanaWordChar(stripped[0])
                && prev != null && JapaneseTextHelper.IsAllKatakana(prev.Text))
            {
                var mergedKatakana = prev.Text + stripped;
                result[^1] = new WordInfo(prev)
                {
                    Text = mergedKatakana,
                    DictionaryForm = mergedKatakana,
                    NormalizedForm = mergedKatakana,
                    PartOfSpeech = PartOfSpeech.Noun,
                    EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                };
                AddTte(thief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // Quotative って glued onto the 連用形 く of an i-adjective: Sudachi reads すご|くっ[くる]|て
            // (and 早|くっ|て, よ|くっ|て) as the verb 来る instead of the adverbial すごく + って. The く is
            // the i-adjective's adverbial ending, not 来る, so reattach it to the stem and hand って back.
            // Gated on prev being a real i-adjective stem/prefix (dictionary form ends い and is in JMDict),
            // which a genuine 来る te-form never follows — so it must run before the CommonTeFormVerbs skip.
            if (stripped == "く" && thief.DictionaryForm == "くる" && prev != null
                && (
                    // Sudachi tags the stem as the i-adjective itself: すご[すごい], 嬉し[嬉しい], 寒[寒い].
                    (prev.PartOfSpeech is PartOfSpeech.IAdjective or PartOfSpeech.Prefix
                        && prev.DictionaryForm.EndsWith("い", StringComparison.Ordinal)
                        && HasNonNameCompoundLookup?.Invoke(prev.DictionaryForm) == true)
                    // …or as a bare-kanji adverb whose stem+い is a real i-adjective: 早[早い], 安[安い].
                    // All-kanji keeps the genuine 送る te-form (おくって kana) out, whose stem is お/hiragana.
                    || (prev.PartOfSpeech == PartOfSpeech.Adverb
                        && prev.Text.Length > 0 && prev.Text.All(c => c is >= '一' and <= '鿿')
                        && HasNonNameCompoundLookup?.Invoke(prev.Text + "い") == true)
                   ))
            {
                result[^1] = new WordInfo(prev)
                {
                    Text = prev.Text + "く",
                    PartOfSpeech = PartOfSpeech.IAdjective,
                    EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                };
                AddTte(thief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // と + かっ[買う/かう] + て from とか+って ("…とか、って" — "...or something," quotative): the か is
            // the particle in とか, not the verb 買う/飼う. Split into か(Particle) + って. Gated on prev being
            // the particle と (forming とか): a genuine 飼って/買って follows an object (prev = を/noun, never と),
            // and 勝手(かって) carries dictionary form 勝手, not 買う — so both are left untouched.
            if (stripped == "か"
                && thief.PartOfSpeech == PartOfSpeech.Verb
                && thief.DictionaryForm is "買う" or "飼う" or "かう"
                && prev is { Text: "と", PartOfSpeech: PartOfSpeech.Particle })
            {
                result.Add(new WordInfo(thief)
                {
                    Text = "か", DictionaryForm = "か", NormalizedForm = "か", Reading = "カ",
                    PartOfSpeech = PartOfSpeech.Particle,
                    EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                });
                AddTte(thief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            // する has no すっ stem (its te-form is して) — a すっ "thief" lemmatised as する is
            // always a shredded word (で|すっ|て = です+って), so it doesn't earn the common-verb skip.
            if (thief.PartOfSpeech is PartOfSpeech.Verb && CommonTeFormVerbs.Contains(thief.DictionaryForm)
                && !(thief.DictionaryForm == "する" && thief.Text == "すっ"))
            {
                result.Add(wordInfos[i]);
                continue;
            }

            // Noun-mora theft, pair shape: 繋|がりっ[Adverb]|て → 繋がり + って. The generic verb-reattach
            // below only accepts う-row verb endings, so a renyoukei/nominalised noun (繋がり) needs this.
            // Verb-tagged thieves enter too (育|ちっ[散る]|て — Sudachi lemmatises the stolen mora
            // as a verb): genuine te-forms are protected by the CommonTeFormVerbs skip above, the
            // が/と/を/は particle stops, and the reattach attestation itself. A bare う never
            // reattaches as a noun — it is the volitional of the preceding verb (生きて|いこ|うっ|て),
            // which the verb machinery owns. いっ/だっ thieves are owned by their dedicated blocks
            // (IuIkuDictForms, だって) — the generic run would reattach their morae onto particles
            // (て+い=弟, に+だ=荷駄).
            if (stripped.Length >= 1 && stripped != "う"
                && !IuIkuDictForms.Contains(thief.DictionaryForm)
                && thief.DictionaryForm != "だつ"
                && stripped.All(c => c is >= 'ぁ' and <= 'ゖ'))
            {
                int runLen = WordReattachRunLength(stripped);
                if (runLen > 0)
                {
                    MergeRunInto(runLen, stripped, thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1);
                    AddTte(thief, wordInfos[i + 1]);
                    i++;
                    changed = true;
                    continue;
                }
            }

            // Kanji-noun mora theft, same shape one script over: って steals the tail kanji's mora and
            // Sudachi re-reads the stem as a verb (必|要っ[要る]|て → 必要 + って; 重|要っ|て → 重要 + って).
            // The hiragana pair-shape above is gated to a non-Verb thief and an all-hiragana stripped, so a
            // kanji stripped reforms here via the same lookback. The WordReattachRunLength lookup gate is
            // what keeps genuine kanji te-forms out: 家に帰って leaves prev = に, and に+帰 is not a JMDict
            // word, so nothing reattaches; it only fires when prev + stripped is a real non-name compound.
            if (stripped.Any(c => c is >= '一' and <= '鿿'))
            {
                int kanjiRun = WordReattachRunLength(stripped);
                if (kanjiRun > 0)
                {
                    MergeRunInto(kanjiRun, stripped, thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1);
                    AddTte(thief, wordInfos[i + 1]);
                    i++;
                    changed = true;
                    continue;
                }

                // After a numeral nothing can reattach (１０ is not kana/kanji), but a counter
                // single kanji standing alone is the word itself + quotative (１０|度っ|て → 度+って).
                // Gated on an actual counter sense: an attested non-counter kanji after numeric-like
                // material (何|言っ|て) is a genuine te-form stem, not a stolen counter.
                if (stripped.Length == 1 && prev != null
                    && Scoring.AdjacentWordScorer.IsNumericSurface(prev.Text)
                    && HasCounterSenseLookup?.Invoke(stripped) == true)
                {
                    result.Add(new WordInfo(thief)
                    {
                        Text = stripped, DictionaryForm = stripped, NormalizedForm = stripped,
                        Reading = thief.Reading is { Length: > 1 } r ? r[..^1] : thief.Reading,
                        PartOfSpeech = PartOfSpeech.Noun,
                        EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                    });
                    AddTte(thief, wordInfos[i + 1]);
                    i++;
                    changed = true;
                    continue;
                }
            }

            // An Auxiliary/Interjection/Particle thief only ever repairs through a successful
            // reattach — the later heuristic branches would split genuine auxiliary te-forms
            // (食べ|ちゃっ|て) and particle fusions.
            if (thief.PartOfSpeech is PartOfSpeech.Auxiliary or PartOfSpeech.Interjection or PartOfSpeech.Particle)
            {
                result.Add(wordInfos[i]);
                continue;
            }

            bool shouldRepair = false;
            bool mergeIntoPrev = false;
            bool mergeAsVerb = false;

            if (stripped.Length == 1 && stripped[0] is >= 'ぁ' and <= 'ゖ' && thief.PartOfSpeech != PartOfSpeech.Adverb)
            {
                if (prev != null)
                {
                    if (prev.PartOfSpeech == PartOfSpeech.Particle && CaseParticles.Contains(prev.Text))
                    {
                        result.Add(wordInfos[i]);
                        continue;
                    }

                    var merged = prev.Text + stripped;
                    var forms = deconj.Deconjugate(merged);
                    // Require a real deconjugation step: RunBfs always returns the identity form
                    // with Process.Count == 0, so accepting <= 1 made this check vacuous and merged
                    // unconditionally.
                    bool hasRealDeconjStep = false;
                    foreach (var f in forms)
                    {
                        if (f.Process.Length == 1) { hasRealDeconjStep = true; break; }
                    }
                    // いっ+て is usually 言って/行って mid-verb: only steal the い when the
                    // reattachment makes a real word (悪い), not a deconj-shaped fragment
                    // (ギシギシい would "deconjugate" too).
                    if (hasRealDeconjStep && IuIkuDictForms.Contains(thief.DictionaryForm)
                        && HasNonNameCompoundLookup?.Invoke(merged) != true)
                        hasRealDeconjStep = false;
                    // きっ etc. is the 促音便 te-stem of an auxiliary compound verb (信じ|きっ|て =
                    // 信じきって, the te-form of 信じ切る). When Sudachi splits the stem off (it keeps
                    // 思いきっ whole but shreds 信じ/疲れ), the stranded mora belongs to the compound
                    // verb きる/切る, not to prev — so prev + thief's dictionary form is a real JMDict
                    // compound. Leave the sequence intact for CombineInflections/CombineCompounds to
                    // reform 信じきる, rather than mis-cutting a quotative って off a fake 信じき stem.
                    if (hasRealDeconjStep && thief.PartOfSpeech == PartOfSpeech.Verb
                        && HasCompoundLookup?.Invoke(prev.Text + thief.DictionaryForm) == true)
                        hasRealDeconjStep = false;
                    if (hasRealDeconjStep)
                    {
                        shouldRepair = true;
                        mergeIntoPrev = true;
                    }
                }
            }
            else if (stripped.Length >= 2)
            {
                bool allKana = true;
                foreach (var c in stripped)
                    if (c is not ((>= 'ぁ' and <= 'ゖ') or (>= '゠' and <= 'ヿ'))) { allKana = false; break; }

                if (allKana && !CommonTeFormVerbs.Contains(thief.DictionaryForm))
                {
                    // Prefer reattaching the kana to a content-word prev when that yields a
                    // clause-final conjugation: 信|じろっ|て → 信じろ + って, い|こうっ|て → いこう + って.
                    if (prev != null
                        && prev.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                        && MergesToFinalForm(prev.Text + stripped))
                    {
                        shouldRepair = true;
                        mergeIntoPrev = true;
                        mergeAsVerb = true;
                    }
                    else if (thief.PartOfSpeech != PartOfSpeech.Adverb)
                    {
                        // A thief that is itself a genuine te-stem — its surface + て deconjugates
                        // back to its own dictionary form (わかっ+て → わかる) — is a verb Sudachi
                        // got right, not a theft. The curated CommonTeFormVerbs skip only covers
                        // the common spellings, so kana verbs outside it landed here and lost
                        // their stem to a coincidental homograph (わか = 和歌).
                        // …unless a quote VERB follows the て: there the quotative frame outranks
                        // the te-form reading (かなっ|て|思ったら is かな + って + 思ったら, even
                        // though かなって is also 叶う's te-form). The POS gate keeps nouns that
                        // merely start with a quote-verb kanji from opening the frame (わかって
                        // 言葉を失った must stay a te-form). The frame is deliberately broad: a
                        // kana te-form directly governing a quote verb (わかって思った) loses its
                        // boundary here, but narrowing it to reattachable thefts costs more real
                        // 〜かなって思う spans than it recovers.
                        bool quoteVerbFollows = i + 2 < wordInfos.Count
                            && wordInfos[i + 2].PartOfSpeech == PartOfSpeech.Verb
                            && wordInfos[i + 2].Text.Length > 0
                            && QuoteVerbHeads.Contains(wordInfos[i + 2].Text[0]);

                        bool selfTeForm = false;
                        if (!quoteVerbFollows
                            && thief.PartOfSpeech == PartOfSpeech.Verb && thief.DictionaryForm.Length >= 2
                            && thief.DictionaryForm != thief.Text
                            && HasNonNameCompoundLookup?.Invoke(thief.DictionaryForm) == true)
                        {
                            var dictHira = KanaConverter.ToHiragana(thief.DictionaryForm);
                            foreach (var f in deconj.Deconjugate(KanaConverter.ToHiragana(thief.Text) + "て"))
                            {
                                if (f.Text == dictHira) { selfTeForm = true; break; }
                            }
                        }

                        if (!selfTeForm)
                        {
                            var forms = deconj.Deconjugate(stripped);
                            shouldRepair = forms.Count > 0;
                        }
                    }
                    // A thief Sudachi mis-tags as an adverb but whose stripped form is itself a
                    // dictionary verb is the same stem theft one mora wider (するっ|て → する + って,
                    // from 勉強するっていう). The う-row gate in IsVerbReattachment rejects noun
                    // homographs the deconjugator over-tags (ことっ → こと=事 stays a noun).
                    else if (IsVerbReattachment(stripped))
                    {
                        shouldRepair = true;
                    }
                }
            }

            if (shouldRepair)
            {
                if (mergeIntoPrev && prev != null)
                {
                    result[^1] = new WordInfo(prev)
                    {
                        Text = prev.Text + stripped,
                        PartOfSpeech = mergeAsVerb ? PartOfSpeech.Verb : prev.PartOfSpeech,
                        // Offsets are char indices; the って particle takes only the trailing っ
                        EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                    };
                }
                // Reduplicated interjection quoted by って: やれ|やれっ|て → やれやれ + って.
                // Without this the stripped copy is dropped as a kana stutter downstream.
                else if (prev != null && prev.Text == stripped
                         && (HasNonNameCompoundLookup ?? HasCompoundLookup)?.Invoke(prev.Text + stripped) == true)
                {
                    result[^1] = new WordInfo(prev)
                    {
                        Text = prev.Text + stripped,
                        DictionaryForm = prev.Text + stripped,
                        NormalizedForm = prev.Text + stripped,
                        EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                    };
                }
                else
                {
                    result.Add(new WordInfo(thief)
                    {
                        Text = stripped, PartOfSpeech = PartOfSpeech.Verb,
                        EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                    });
                }

                AddTte(thief, wordInfos[i + 1]);
                i++;
                changed = true;
                continue;
            }

            result.Add(wordInfos[i]);
        }

        return changed ? result : wordInfos;
    }

    // Clipped exclamatory i-adjective: the colloquial stem + っ pattern (足短っ, 高っ, 寒っ)
    // drops the い entirely, so the っ ends up a stranded symbol and the stem resolves as an
    // unrelated noun homograph (短 → "fault; defect"). A kanji-final stem whose +い form is an
    // attested verb or adjective is the adjective; merge and carry the dictionary form so lookup
    // lands on it. Kana stems stay out (や|ば never forms a stem to work from).
    private List<WordInfo> RepairClippedAdjective(List<WordInfo> wordInfos)
    {
        if (HasNonNameCompoundLookup == null || HasVerbOrAdjectiveLookup == null)
            return wordInfos;
        return ScanRewrite(wordInfos, TryRepairClippedAdjective);
    }

    private int TryRepairClippedAdjective(List<WordInfo> tokens, int i, List<WordInfo>? _, Func<List<WordInfo>> output)
    {
        var current = tokens[i];

        // The っ may be separated from the stem by a forced-boundary blank.
        int tsuIdx = -1;
        for (int j = i + 1; j < tokens.Count && j <= i + 2; j++)
        {
            // the forced sokuon boundary leaves a stop token between stem and っ
            if (tokens[j].PartOfSpeech == PartOfSpeech.BlankSpace || tokens[j].Text == _stopToken) continue;
            if (tokens[j].Text is "っ" or "ッ") tsuIdx = j;
            break;
        }

        if (tsuIdx < 0
            || current.Text.Length == 0 || current.Text.Length > 4
            || current.PreMatchedWordId != null
            || current.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Particle
                or PartOfSpeech.Auxiliary or PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol
            // Kanji-final stems only: a kana stem + い hits unrelated kana keys (がんっ →
            // がん+い = 含意) and would resurrect SFX fragments the drop gates handle.
            || !JapaneseTextHelper.IsKanji(current.Text[^1])
            // POS-aware: the +い form must be a verb/adjective entry, not any word — keeps
            // noun collisions (兄い vocative) from converting a clause-initial noun + っ.
            || !HasVerbOrAdjectiveLookup!(current.Text + "い")
            // A vocative っ after a noun whose halves form an attested compound is not a
            // clipped adjective (隊長っ: 隊+長 must re-fuse to 隊長, not become 長い).
            || (i > 0 && HasNonNameCompoundLookup!(tokens[i - 1].Text + current.Text)))
            return 0;

        output().Add(new WordInfo(current)
        {
            Text = current.Text + "っ",
            DictionaryForm = current.Text + "い",
            NormalizedForm = current.Text + "い",
            Reading = "",
            PartOfSpeech = PartOfSpeech.IAdjective,
            EndOffset = tokens[tsuIdx].EndOffset
        });
        return tsuIdx - i + 1;
    }


    /// <summary>
    /// Classical attributive き fused into the following noun by the lattice: 白|き尾 → 白き|尾.
    /// Fires when the previous token is a bare kanji noun whose stem+い is a JMDict i-adjective,
    /// the current OOV-ish noun starts with き, and the remainder resolves on its own. The
    /// re-attached Xき form deconjugates through the existing "classical attributive" rule.
    /// </summary>
    private List<WordInfo> RepairClassicalKiAdjective(List<WordInfo> wordInfos)
    {
        if (HasNonNameCompoundLookup == null || HasCompoundLookup == null)
            return wordInfos;
        return ScanRewrite(wordInfos, TryRepairClassicalKiAdjective);
    }

    private int TryRepairClassicalKiAdjective(List<WordInfo> tokens, int i, List<WordInfo>? result, Func<List<WordInfo>> output)
    {
        var current = tokens[i];
        // The previous token may already have been rewritten this pass, so peek the accumulator.
        var prev = i > 0 ? (result != null ? result[^1] : tokens[i - 1]) : null;

        if (prev == null
            || current.PartOfSpeech != PartOfSpeech.Noun
            || current.Text.Length < 2 || current.Text[0] != 'き'
            || prev.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.NaAdjective)
            || prev.Text.Length is < 1 or > 2
            || !prev.Text.All(c => c is >= '一' and <= '鿿')
            || HasCompoundLookup!(current.Text)
            || !HasNonNameCompoundLookup!(prev.Text + "い")
            || !HasNonNameCompoundLookup(current.Text[1..]))
            return 0;

        var list = output();
        list[^1] = new WordInfo(prev)
        {
            Text = prev.Text + 'き',
            DictionaryForm = prev.Text + "い",
            NormalizedForm = prev.Text + "い",
            PartOfSpeech = PartOfSpeech.IAdjective,
            EndOffset = prev.EndOffset >= 0 ? prev.EndOffset + 1 : -1
        };
        list.Add(new WordInfo(current)
        {
            Text = current.Text[1..],
            DictionaryForm = current.Text[1..],
            NormalizedForm = current.Text[1..],
            StartOffset = current.StartOffset >= 0 ? current.StartOffset + 1 : -1
        });
        return 1;
    }

    private List<WordInfo> RecombineHiraganaTokens(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2 || HasCompoundLookup == null)
            return wordInfos;

        var hasNonNameLookup = HasNonNameCompoundLookup ?? HasCompoundLookup;
        var hasPrioritizedLookup = HasPrioritizedNonNameCompoundLookup ?? hasNonNameLookup;
        // The merged surface is pure hiragana, so the lookup hit must be a word plausibly written
        // in kana — otherwise reading-key collisions merge shards into rare kanji words
        // (はい+そう → 配送, どう+なん → 童男, うん+ま → ウンマ).
        var hasKanaAppropriateLookup = HasKanaAppropriateCompoundLookup ?? hasNonNameLookup;

        var deconjugator = Deconjugator.Instance;
        var result = new List<WordInfo>(wordInfos.Count);
        bool changed = false;

        int i = 0;
        while (i < wordInfos.Count)
        {
            bool combined = false;
            int maxSpan = Math.Min(4, wordInfos.Count - i);

            for (int spanLen = maxSpan; spanLen >= 2 && !combined; spanLen--)
            {
                bool allValid = true;
                int totalLen = 0;

                for (int j = i; j < i + spanLen; j++)
                {
                    var w = wordInfos[j];
                    if (!JapaneseTextHelper.IsAllHiragana(w.Text) ||
                        PosMapper.IsInflectableBase(w.PartOfSpeech) ||
                        w.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary
                            or PartOfSpeech.Prefix or PartOfSpeech.Suffix or PartOfSpeech.NounSuffix
                            or PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol
                            or PartOfSpeech.BlankSpace or PartOfSpeech.Conjunction
                        || (w.PartOfSpeech == PartOfSpeech.Expression && w.Text is "だった" or "だったら"))
                    {
                        allValid = false;
                        break;
                    }

                    totalLen += w.Text.Length;
                }

                if (!allValid || totalLen < 3 || totalLen > 12)
                    continue;

                bool allSameInterjection = wordInfos[i].PartOfSpeech == PartOfSpeech.Interjection;
                if (allSameInterjection)
                {
                    var firstText = wordInfos[i].Text;
                    for (int j = i + 1; j < i + spanLen; j++)
                    {
                        if (wordInfos[j].Text != firstText || wordInfos[j].PartOfSpeech != PartOfSpeech.Interjection)
                        {
                            allSameInterjection = false;
                            break;
                        }
                    }
                }
                if (allSameInterjection) continue;

                // An interjection followed by the standalone adverb そう (えっ+そう, あっ+そう =
                // "huh — I see") is two utterance units. そう never fuses into a preceding
                // interjection: neither the kana-noun key (えっそう) nor the colloquial verb
                // deconjugation (えっそう→得る) is valid. ふん+ばったり (→踏ん張る -tari) is
                // unaffected — its second token is ばったり, not そう.
                if (wordInfos[i].PartOfSpeech == PartOfSpeech.Interjection
                    && wordInfos[i + 1].Text == "そう")
                    continue;

                string combinedText = spanLen switch
                {
                    2 => wordInfos[i].Text + wordInfos[i + 1].Text,
                    3 => wordInfos[i].Text + wordInfos[i + 1].Text + wordInfos[i + 2].Text,
                    _ => wordInfos[i].Text + wordInfos[i + 1].Text + wordInfos[i + 2].Text + wordInfos[i + 3].Text,
                };

                if (hasKanaAppropriateLookup(combinedText))
                {
                    result.Add(BuildMergedHiraganaToken(wordInfos, i, spanLen, combinedText, combinedText, PartOfSpeech.CommonNoun));
                    i += spanLen;
                    combined = true;
                    break;
                }

                bool allTokensPrioritized = true;
                for (int j = i; j < i + spanLen; j++)
                {
                    if (!hasPrioritizedLookup(wordInfos[j].Text))
                    {
                        allTokensPrioritized = false;
                        break;
                    }
                }

                if (allTokensPrioritized)
                    continue;

                var hiragana = KanaConverter.ToNormalizedHiragana(combinedText);
                var forms = deconjugator.Deconjugate(hiragana);

                foreach (var form in forms)
                {
                    if (form.Tags.Length == 0 || form.Tags.Length > 5) continue;
                    var lastTag = form.Tags[^1];
                    if (!lastTag.StartsWith('v') && lastTag is not "adj-i" and not "adj-na")
                        continue;
                    if (!hasNonNameLookup(form.Text)) continue;

                    var pos = lastTag switch
                    {
                        "adj-i" => PartOfSpeech.IAdjective,
                        "adj-na" => PartOfSpeech.NaAdjective,
                        _ => PartOfSpeech.Verb
                    };

                    result.Add(BuildMergedHiraganaToken(wordInfos, i, spanLen, combinedText, form.Text, pos));
                    i += spanLen;
                    combined = true;
                    break;
                }
            }

            if (combined)
                changed = true;
            else
            {
                result.Add(wordInfos[i]);
                i++;
            }
        }

        return changed ? result : wordInfos;
    }

    private static WordInfo BuildMergedHiraganaToken(
        List<WordInfo> tokens, int start, int count,
        string surface, string dictForm, PartOfSpeech pos)
    {
        var first = tokens[start];
        var last = tokens[start + count - 1];

        string reading = count switch
        {
            2 => tokens[start].Reading + tokens[start + 1].Reading,
            3 => tokens[start].Reading + tokens[start + 1].Reading + tokens[start + 2].Reading,
            _ => tokens[start].Reading + tokens[start + 1].Reading + tokens[start + 2].Reading + tokens[start + 3].Reading,
        };

        return new WordInfo
        {
            Text = surface,
            DictionaryForm = dictForm,
            NormalizedForm = dictForm,
            PartOfSpeech = pos,
            StartOffset = first.StartOffset,
            EndOffset = last.EndOffset,
            Reading = reading,
        };
    }

}
