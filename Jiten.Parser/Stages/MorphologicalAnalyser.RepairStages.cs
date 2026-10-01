using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Utils;
using WanaKanaShaapu;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    // Verb heads a quotative って attaches to (言う/思う/聞く/考える/感じる); the datte-quote rewrite rule shares this set.
    internal static readonly char[] QuoteVerbHeads = ['言', '思', '聞', '考', '感'];

    // Sudachi bleeds the っ of 〜っぱなし/っぷり/っぽい into the stem (流|しっ|ぱなし, しっ→知る); っ must head its suffix.
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

        // Extends only onto an attested renyoukei (上がり) or orphan kanji (失+い); ワクワク+し stays split so し resolves as する.
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

            // Host must end in kanji or i/e-row kana; an a-row final (やっ|ぱなし = やっぱ+なし) never hosts the suffix.
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

            // Lexicalised 女っぷり stranding a compound's first kanji (巫|女っぷり); いい女っぷり stays whole.
            bool rejoinsPrevKanji = false;
            if (stemText == null && t.Text.Length > 3 && t.Text.EndsWith("っぷり", StringComparison.Ordinal) && i > 0
                && toks[i - 1].Text.Length == 1 && JapaneseTextHelper.IsKanji(toks[i - 1].Text[0])
                && HasNonNameCompoundLookup?.Invoke(toks[i - 1].Text + t.Text[..^3]) == true)
            {
                stemText = t.Text[..^3];
                suffixText = "っぷり";
                consumed = 1;
                rejoinsPrevKanji = true;
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

            // Rejoins leftward while it deconjugates to an attested verb (上+が+り→上がる); を+飛ばし fails.
            int merges = 0;
            int stemIdx = i;
            while (stemIdx > 0 && merges < 3)
            {
                var prev = list[stemIdx - 1];
                var cand = prev.Text + list[stemIdx].Text;
                if (!(rejoinsPrevKanji && merges == 0) && !StemExtends(prev, cand)) break;
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

    // A blob that cuts into two attested words (ハロー|ワーク) belongs to the resegmentation lattice, which scores the cut.
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

    // Sudachi shreds OOV katakana into junk-attested fragments (ホロ|グラフ); merge if any piece is fragment-shaped (システム|エラー stays).
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

            if (!IsKatakanaRunToken(toks[i]) && !IsNameShredOfMixedToken(toks, i)) continue;

            int end = i;
            while (end + 1 < toks.Count && end - i < 4 && IsKatakanaRunToken(toks[end + 1])
                   && Contiguous(toks[end], toks[end + 1]))
                end++;

            // A katakana-headed mixed token continues the run (ブロー|アップされる); its hiragana tail splits back off.
            string mixedTail = "";
            int mixedIdx = -1;
            if (end + 1 < toks.Count && end - i < 4 && toks[end + 1].PreMatchedWordId == null
                && Contiguous(toks[end], toks[end + 1]))
            {
                var cand = toks[end + 1].Text;
                int kl = 0;
                while (kl < cand.Length && JapaneseTextHelper.IsKatakanaWordChar(cand[kl])) kl++;
                // A kanji tail means the token is its own compound (リア充), not a shredded head plus grammar.
                if (kl >= 1 && kl < cand.Length && cand[kl..].All(c => c is >= 'ぁ' and <= 'ゖ'))
                {
                    mixedIdx = end + 1;
                    mixedTail = cand[kl..];
                }
            }

            // Splits a real loanword tail off an unattested blob (OOV name+レベル); heads under 4 chars defer to the lattice.
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

            // An attested ≥3-char tail (レベル) stays out of the merge, but a two-piece run still merges whole as name material.
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

        // Loanword head tagged a proper noun with its last mora fused into the particle (ドラ|マで[まで]).
        bool IsNameShredOfMixedToken(List<WordInfo> toks, int i)
        {
            if (i + 1 >= toks.Count) return false;
            var head = toks[i];
            var next = toks[i + 1];
            if (head.PreMatchedWordId != null || head.IsPersonNameContext || next.PreMatchedWordId != null
                || head.Text.Length == 0 || !head.Text.All(JapaneseTextHelper.IsKatakanaWordChar)
                || !Contiguous(head, next))
                return false;
            int kl = 0;
            while (kl < next.Text.Length && JapaneseTextHelper.IsKatakanaWordChar(next.Text[kl])) kl++;
            return kl >= 1 && kl < next.Text.Length
                   && next.Text[kl..].All(c => c is >= 'ぁ' and <= 'ゖ')
                   && HasNonNameCompoundLookup?.Invoke(head.Text + next.Text[..kl]) == true;
        }

        // Stripped markup leaves unrelated tokens adjacent ([name]アリサ[line]“リア充); merging across the gap makes アリサリア.
        static bool Contiguous(WordInfo a, WordInfo b) =>
            a.EndOffset >= 0 && b.StartOffset >= 0 && a.EndOffset == b.StartOffset;
    }

    // These attach to the FULL preceding compound (滑走路+上, 消去法+的), so 滑走|路上 re-cuts to 滑走路|上.
    private static readonly HashSet<char> LocationSuffixChars =
        ['上', '中', '内', '外', '前', '後', '間', '下', '先', '際', '的'];

    // Tuned cutoff: 日本|国内 stays, 滑走|路上 re-cuts.
    private const int BoundaryTheftRankDominanceFactor = 4;

    private static readonly char[] ExpressionSplitParticles = ['を', 'が', 'に'];

    // 当薬 (267598) vs 薬局 (21272) re-cuts; common heads (大学|生, 日本|国) never qualify.
    private const int PrefixTheftMinHeadRank = 50000;
    private const int PrefixTheftRankDominanceFactor = 8;

    // Tightest attested case: 司る (8655) vs 主 (1844).
    private const int RareVerbShredMinRank = 5000;
    private const int RareVerbShredRankRatio = 4;

    private static readonly string[] DemonstrativeHeads = ["その", "この", "あの", "どの"];

    // Sudachi splits a conjugated verb's kanji head from its kana tail as unrelated words (会|おう → 会+王).
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
            // 何 heads no verb; its kana continuations are their own words (何|かって).
            if (k.Text == "何") continue;

            if (TryShiftStrandedOkurigana(ref list, wordInfos, i, ref indexDelta)) continue;

            toks = list ?? wordInfos;
            k = toks[i];
            tail = toks[i + 1];
            if (tail.PreMatchedWordId != null) continue;
            if (k.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun)) continue;
            // A clause-initial single kanji is a speaker name in script dumps ([name]至[line]“そうなん?” is not 至る).
            if (i == 0 || toks[i - 1].PartOfSpeech is PartOfSpeech.Symbol
                or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace) continue;
            if (tail.Text.Length is < 1 or > 4 || !tail.Text.All(c => c is >= 'ぁ' and <= 'ゖ')) continue;
            if (tail.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary
                or PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol) continue;
            // A copula tail belongs to the sentence (目|だった is not 目立った); a complete dictionary-form verb stays (今|いる).
            if (tail.DictionaryForm is "だ" or "です"
                || tail.Text is "だ" or "だっ" or "だった" or "だったら" or "で" or "です" or "でし"
                    or "でした" or "だろ" or "だろう" or "じゃ" or "じゃない" or "じゃなく"
                    // Noun+する-negative (話|しないで), never a shred.
                    or "しない" or "しないで" or "しなく" or "せず") continue;
            // Same する-negative split one token earlier (話|し|ないで).
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

    // A sokuon contraction after a kanji verb stem shifts every cut one char early (探|しっス → 探し|っス); run[1] っ marks it.
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
        // A small kana leading the run means the sokuon is the stem's own (真|っ赤), not a stolen boundary.
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
                // A shifted って can only be the quotative.
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

    // Accepts a dictionary-form verb (探す) or one deconjugation step off one (探し → 探す).
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
        int shift = 0;
        foreach (int candidate in candidates)
        {
            var toks = list ?? wordInfos;
            int i = candidate + shift;
            if (i < 0 || i + 1 >= toks.Count) continue;
            var a = toks[i];
            var b = toks[i + 1];
            if (a.PreMatchedWordId != null || b.PreMatchedWordId != null) continue;

            // An N+particle+V expression swallowed a compound's tail: [素][振りをする].
            int particleAt = b.PartOfSpeech == PartOfSpeech.Expression && b.Text.Length > 2
                ? b.Text.IndexOfAny(ExpressionSplitParticles) : -1;
            if (a.Text.Length == 1 && JapaneseTextHelper.IsKanji(a.Text[0])
                && a.PartOfSpeech is PartOfSpeech.Suffix or PartOfSpeech.Prefix
                    or PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && particleAt >= 1 && particleAt < b.Text.Length - 1
                && JapaneseTextHelper.IsKanji(b.Text[0])
                && HasNonNameCompoundLookup?.Invoke(a.Text + b.Text[..particleAt]) == true)
            {
                var noun = a.Text + b.Text[..particleAt];
                var particle = b.Text[particleAt..(particleAt + 1)];
                var verbText = b.Text[(particleAt + 1)..];
                var verbDict = b.DictionaryForm.StartsWith(b.Text[..(particleAt + 1)], StringComparison.Ordinal)
                    ? b.DictionaryForm[(particleAt + 1)..]
                    : verbText;
                int nounEnd = a.StartOffset >= 0 ? a.StartOffset + noun.Length : -1;

                list ??= new List<WordInfo>(wordInfos);
                list[i] = new WordInfo(a)
                {
                    Text = noun, DictionaryForm = noun, NormalizedForm = noun, Reading = "",
                    PartOfSpeech = PartOfSpeech.Noun, PartOfSpeechSection1 = PartOfSpeechSection.None,
                    EndOffset = nounEnd,
                };
                list[i + 1] = new WordInfo(b)
                {
                    Text = particle, DictionaryForm = particle, NormalizedForm = particle,
                    Reading = WanaKana.ToKatakana(particle),
                    PartOfSpeech = PartOfSpeech.Particle, PartOfSpeechSection1 = PartOfSpeechSection.CaseMarkingParticle,
                    StartOffset = nounEnd, EndOffset = nounEnd >= 0 ? nounEnd + 1 : -1,
                };
                list.Insert(i + 2, new WordInfo(b)
                {
                    Text = verbText, DictionaryForm = verbDict, NormalizedForm = verbDict == "する" ? "為る" : verbDict,
                    Reading = verbText == verbDict && verbText.All(JapaneseTextHelper.IsHiragana)
                        ? WanaKana.ToKatakana(verbText) : "",
                    PartOfSpeech = PartOfSpeech.Verb, PartOfSpeechSection1 = PartOfSpeechSection.None,
                    StartOffset = nounEnd >= 0 ? nounEnd + 1 : -1,
                });
                shift++;
                continue;
            }

            // [不時][着し] → [不時着][し]: a suru-noun's last kanji misread as a verb stem (着す).
            if (a.Text.All(JapaneseTextHelper.IsKanji)
                && a.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && b.PartOfSpeech == PartOfSpeech.Verb && b.Text.Length >= 2 && b.Text[1] == 'し'
                && JapaneseTextHelper.IsKanji(b.Text[0])
                && HasSuruVerbCompoundLookup?.Invoke(a.Text + b.Text[0]) == true
                && HasNonNameCompoundLookup?.Invoke(a.Text + b.Text[0]) == true)
            {
                var noun = a.Text + b.Text[0];
                int cut = a.EndOffset >= 0 ? a.EndOffset + 1 : -1;
                list ??= new List<WordInfo>(wordInfos);
                list[i] = new WordInfo(a)
                {
                    Text = noun, DictionaryForm = noun, NormalizedForm = noun, Reading = "",
                    PartOfSpeech = PartOfSpeech.Noun, PartOfSpeechSection1 = PartOfSpeechSection.None,
                    EndOffset = cut,
                };
                list[i + 1] = new WordInfo(b)
                {
                    Text = b.Text[1..], DictionaryForm = "する", NormalizedForm = "為る",
                    Reading = WanaKana.ToKatakana(b.Text[1..]),
                    PartOfSpeech = PartOfSpeech.Verb, PartOfSpeechSection1 = PartOfSpeechSection.None,
                    StartOffset = cut,
                };
                continue;
            }

            // [ちびっ][こども] → [ちびっこ][ども]: the っ-final word ends one mora into the next token.
            if (a.Text.Length >= 2 && a.Text[^1] == 'っ' && b.Text.Length >= 3
                && a.PartOfSpeech is not (PartOfSpeech.Verb or PartOfSpeech.Auxiliary or PartOfSpeech.IAdjective)
                && JapaneseTextHelper.IsKana(b.Text[0])
                && HasNonNameCompoundLookup?.Invoke(a.Text + b.Text[0]) == true
                && HasNonNameCompoundLookup?.Invoke(b.Text[1..]) == true
                && HasNonNameCompoundLookup?.Invoke(a.Text) != true)
            {
                var head = a.Text + b.Text[0];
                int cut = a.EndOffset >= 0 ? a.EndOffset + 1 : -1;
                list ??= new List<WordInfo>(wordInfos);
                list[i] = new WordInfo(a)
                {
                    Text = head, DictionaryForm = head, NormalizedForm = head, Reading = WanaKana.ToKatakana(head),
                    PartOfSpeech = PartOfSpeech.Noun, PartOfSpeechSection1 = PartOfSpeechSection.None,
                    EndOffset = cut,
                };
                list[i + 1] = new WordInfo(b)
                {
                    Text = b.Text[1..], DictionaryForm = b.Text[1..], NormalizedForm = b.Text[1..],
                    Reading = b.Reading is { Length: > 1 } br ? br[1..] : "",
                    StartOffset = cut,
                };
                continue;
            }

            // [当薬][局] → [当][薬局]: a prefix kanji paired into a rare word stole a frequent compound's head.
            if (a.Text.Length == 2 && a.Text.All(JapaneseTextHelper.IsKanji)
                && b.Text.Length == 1 && JapaneseTextHelper.IsKanji(b.Text[0])
                && a.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && b.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Suffix
                && HasNonNameCompoundLookup?.Invoke(a.Text[..1]) == true
                && GetNonNameCompoundFrequencyRank?.Invoke(a.Text[1..] + b.Text) is int tailRank
                && (GetNonNameCompoundFrequencyRank(a.Text) ?? int.MaxValue) is var headRank
                && headRank >= PrefixTheftMinHeadRank
                && headRank / PrefixTheftRankDominanceFactor > tailRank)
            {
                var tail = a.Text[1..] + b.Text;
                int cut = a.StartOffset >= 0 ? a.StartOffset + 1 : -1;
                list ??= new List<WordInfo>(wordInfos);
                list[i] = new WordInfo(a)
                {
                    Text = a.Text[..1], DictionaryForm = a.Text[..1], NormalizedForm = a.Text[..1], Reading = "",
                    PartOfSpeech = PartOfSpeech.Prefix, PartOfSpeechSection1 = PartOfSpeechSection.None,
                    EndOffset = cut,
                };
                list[i + 1] = new WordInfo(b)
                {
                    Text = tail, DictionaryForm = tail, NormalizedForm = tail, Reading = "",
                    PartOfSpeech = PartOfSpeech.Noun, PartOfSpeechSection1 = PartOfSpeechSection.None,
                    StartOffset = cut,
                };
                continue;
            }

            // [滑走][路上] → [滑走路][上], [小][動物的] → [小動物][的].
            if (b.Text.Length is 2 or 3 && b.Text != "時間" && LocationSuffixChars.Contains(b.Text[^1])
                && b.Text[..^1].All(JapaneseTextHelper.IsKanji)
                && a.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Prefix
                && b.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.NaAdjective
                && a.Text.Length >= 1 && a.Text.All(JapaneseTextHelper.IsKanji)
                && (a.Text.Length >= 2 || a.PartOfSpeech == PartOfSpeech.Prefix)
                // A numeral head means B is a duration/counter word (二十|時間), never a theft.
                && !a.Text.All(JapaneseTextHelper.IsNumeralChar)
                && HasNonNameCompoundLookup?.Invoke(a.Text + b.Text[..^1]) == true
                // B heading its own compound with what follows is genuine (人類|史上|初); 滑走|路上|で is not.
                && !(i + 2 < toks.Count
                     && HasNonNameCompoundLookup?.Invoke(b.Text + toks[i + 2].Text) == true)
                // A much more frequent B is its own word (国内 far outranks 日本国, so 日本|国内 stays).
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

            // [その時][系列] → [その][時系列].
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
                    // Keep the first two kana; the stolen kanji's reading length varies (時=トキ, 日=ヒ).
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

    // An OOV intensifier X返る (のさばり返る) drops whole; split to head verb + 返る. Attested 静まり返る fails the lookup gate.
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

        // The head is the normalised form minus 返る, a kana-stable renyoukei (のさばり → のさばる).
        var headStem = w.NormalizedForm[..^2];
        string? headDict = null;
        foreach (var f in Deconjugator.Instance.Deconjugate(headStem))
            if (f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) && HasNonNameCompoundLookup(f.Text))
            { headDict = f.Text; break; }

        // Surface must open with the same kana head (のさばりかえっ).
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
        // Folds て/た into 返って so it is read neither as quotative って nor as 却って.
        if (suffixSurface.EndsWith("っ", StringComparison.Ordinal)
            && i + 1 < tokens.Count && tokens[i + 1].Text is "て" or "た")
        {
            result.Add(new WordInfo(w)
            {
                Text = suffixSurface + tokens[i + 1].Text, DictionaryForm = "返る", NormalizedForm = "返る",
                Reading = "", PartOfSpeech = PartOfSpeech.Verb, PreMatchedWordId = 1512150,
                // Deconjugation is kana-side, so the chain is recovered against かえる, not 返る.
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

    // A numeral claims 段 (一段|飛ばし), stranding 飛ばし on the fraud noun; reform 段飛ばし. A blanket 段+X rule would split 三段跳び.
    private List<WordInfo> RepairDanTobashi(List<WordInfo> wordInfos) =>
        ScanRewrite(wordInfos, static (tokens, i, _, output) =>
        {
            var w = tokens[i];
            if (!(i + 1 < tokens.Count && tokens[i + 1] is { Text: "飛ばし" } b
                  && w.PreMatchedWordId == null && b.PreMatchedWordId == null
                  && w.Text.EndsWith("段", StringComparison.Ordinal)))
                return 0;

            var head = w.Text[..^1];
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
            // 何段/数段 ("how many / several steps") count as numeral heads.
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

            // Noun たか after a verb stem is past た + か (言い過ぎ|たか); 鷹/高 after を/の never forms a past tense.
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

            if (word.PartOfSpeech != PartOfSpeech.Noun || word.Text != "たんか")
            {
                result.Add(word);
                continue;
            }

            // An adjacent を or a possessive の marks the real noun (たんかを吐く, お島の方のたんか).
            if (i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "を")
            {
                result.Add(word);
                continue;
            }

            if (result.Count > 0 && result[^1].Text == "を")
            {
                result.Add(word);
                continue;
            }

            if (result.Count > 0 && result[^1].Text == "の")
            {
                result.Add(word);
                continue;
            }

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

            bool shouldSplit = false;
            var prev = GetPrevToken(1);

            if (prev != null)
            {
                // 云う|たんか → 云うた|ん|か.
                if (prev.PartOfSpeech == PartOfSpeech.Verb)
                {
                    var candidateText = prev.Text + "た";
                    var forms = deconj.Deconjugate(NormalizeToHiragana(candidateText));
                    if (forms.Any(f => f.Tags.Any(t => t.StartsWith('v'))))
                        shouldSplit = true;
                }

                // Any て/で ending takes the た, even one Sudachi tags IAdjective (怖がって|たんか → 怖がってた).
                if (!shouldSplit && (prev.Text.EndsWith('て') || prev.Text.EndsWith('で')))
                {
                    shouldSplit = true;
                }

                // Kansai てもうた (ハズレて|もう|たんか → ハズレてもうた|ん|か); matched by text since もう's POS varies.
                if (prev.Text == "もう")
                {
                    var verbBefore = GetPrevToken(2);
                    if (verbBefore != null && (verbBefore.Text.EndsWith('て') || verbBefore.Text.EndsWith('で')))
                    {
                        var combinedText = verbBefore.Text + "もうた";
                        var prevIdx = GetPrevTokenIndex(1);
                        var verbIdx = GetPrevTokenIndex(2);
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

                // Kansai てしもた (言うて|し|も|たんか → 言うてしもた|ん|か).
                if (prev.Text == "も")
                {
                    var shiToken = GetPrevToken(2);
                    if (shiToken is { Text: "し" })
                    {
                        var verbBefore = GetPrevToken(3);
                        if (verbBefore != null && (verbBefore.Text.EndsWith('て') || verbBefore.Text.EndsWith('で') ||
                                                   verbBefore.PartOfSpeech == PartOfSpeech.Expression))
                        {
                            var combinedText = verbBefore.Text + "しもた";
                            var moIdx = GetPrevTokenIndex(1);
                            var shiIdx = GetPrevTokenIndex(2);
                            var verbIdx = GetPrevTokenIndex(3);
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

            // Sudachi splits negative ねえ into ね|え after a te-form (入って|ね|え|のに).
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

    /// <summary>Sudachi tags らん an adverb, blocking CombineInflections; らんない as an auxiliary reaches the deconjugator.</summary>
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

        // A small vowel from a different row is a digraph (ファ, ティ), not a stretch.
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

            // ケドー otherwise matches the KEDO organisation instead of けど.
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

            // A trailing ー makes Sudachi read んー as a filler (総|ちゃ|んー → 総|ちゃん); ー is discarded.
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

            // Sudachi shreds kana reciprocal 〜あう into interjections (微笑み|あ|う).
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

            // Intensifying っ-prefix verb (すっ|とぼける → すっとぼける).
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

            // An elongating ー splits る-verbs (来るー → 来|る|ー, おいしすぎるー → おいし|すぎ|る|ー).
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

            // Godan volitional with う lengthened to ー (泳|ご|ー → 泳ごう).
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

            // Same volitional with ー fused into the kana token (遊|ぼー → 遊ぼう).
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

            // An elongating ー splits i-adjective forms (早くー → 早|くー).
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

            // る-verb plus elongating う (ぶつ|かるう → ぶつかる|う).
            if (current.Text.EndsWith("るう", StringComparison.Ordinal) && current.Text.Length >= 2)
            {
                var verbCandidate = prev.Text + current.Text[..^1];
                var verbHiragana = NormalizeToHiragana(verbCandidate);

                // Probes both negatives (食べない for v1, ぶつからない for v5r) and requires the exact candidate back.
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
                    var interjection = MakeInterjection("う");
                    interjection.StartOffset = current.EndOffset >= 0 ? current.EndOffset - 1 : -1;
                    interjection.EndOffset = current.EndOffset;
                    result.Add(interjection);
                    changed = true;
                    continue;
                }
            }

            // たあ is misread as the particle と (おき|たあ → おきた|あ).
            if (current.Text == "たあ")
            {
                var pastCandidate = prev.Text + "た";
                var pastHiragana = NormalizeToHiragana(pastCandidate);

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

            // A past tense before ああ gets misread as a na-adjective (いきた|ああ).
            if (current.Text == "ああ")
            {
                var prevHiragana = NormalizeToHiragana(prev.Text);

                if (prevHiragana.EndsWith("た", StringComparison.Ordinal) || prevHiragana.EndsWith("だ", StringComparison.Ordinal))
                {
                    if (IsVerbPast(deconjugator.Deconjugate(prevHiragana)) && prev.PartOfSpeech != PartOfSpeech.Verb)
                    {
                        result[^1] = new WordInfo(prev) { PartOfSpeech = PartOfSpeech.Verb };
                        changed = true;
                    }
                }
            }

            // Small-vowel run fused onto a conjugation ending (来|やが|れぇぇぇ → やがれ); a noun stem (移|れぇぇぇ) is left.
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

        List<WordInfo>? split = null;
        for (int idx = 0; idx < wordInfos.Count; idx++)
        {
            var word = wordInfos[idx];

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

            // Appearance/hearsay そうだ must be split for the combine stages.
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

        var source = split ?? wordInfos;
        List<WordInfo>? result = null;
        bool changed = false;
        var deconj = Deconjugator.Instance;

        for (int i = 0; i < source.Count; i++)
        {
            var current = source[i];

            // 飲ん|だ is past, but たくさん|で and さん|だ take the copula.
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

            if (current.Text == "ん" && (result != null ? result.Count > 0 : i > 0))
            {
                result ??= CopyAccumulatorUpTo(source, i);
                bool combined = false;

                // Explanatory のだ needs the 連体形 (するんだ), so kana し+んだ is 死んだ (なかまもすべてしんだ).
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

                // Sudachi can tag the verb stem a noun and ん explanatory (喜|んだだろうね).
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

                // Sudachi tags an ichidan-stem negative (足り+ん) explanatory, impossible since that ん needs the 連体形.
                bool misreadStemNegative = current.DictionaryForm is "の" or "ん"
                    && result.Count > 0
                    && result[^1].PartOfSpeech == PartOfSpeech.Verb
                    && result[^1].Text.Length > 0
                    && result[^1].Text != result[^1].DictionaryForm
                    // After a past/te-form (死んだ+ん, 読んで+ん) ん is explanatory or a slurred ている.
                    && result[^1].Text[^1] is not ('た' or 'だ' or 'て' or 'で')
                    // する's negative is せん; しん is 死ぬ material and すんだ is するんだ.
                    && !(result[^1].Text is "し" or "す" && result[^1].DictionaryForm is "する" or "為る")
                    // ある's negative is never あん; あんだ is あるんだ.
                    && !(result[^1].Text == "あ" && result[^1].DictionaryForm is "ある" or "有る" or "在る");

                if (!combined && (current.DictionaryForm == "ぬ" || misreadStemNegative) &&
                    TryCombineWithLookback(result, "ん", "ん", deconj, IsAnyVerbForm, out var negativeWord))
                {
                    negativeWord!.EndOffset = current.EndOffset;
                    // The deconjugator alone prefers the shorter slurred-る path (してん), so chain selection needs this.
                    negativeWord.IsSlurredNegative = true;

                    // [し, ませ] + ん → しません.
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

                    // Lookback stops at られん and strands the stem (認め|られん); loops for chains (食べ+させられん).
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

    private bool DeconjugatesToVerbInLookup(string surface)
    {
        if (HasVerbOrAdjectiveLookup == null) return false;
        foreach (var f in Deconjugator.Instance.Deconjugate(NormalizeToHiragana(surface)))
            if (f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) && HasVerbOrAdjectiveLookup(f.Text))
                return true;
        return false;
    }

    // Stricter than DeconjugatesToVerbInLookup so plain ichidan negatives (聞こえない, 信用ない) stay split.
    private bool DeconjugatesToCausativeOrPassiveVerb(string surface)
    {
        if (HasVerbOrAdjectiveLookup == null) return false;
        foreach (var f in Deconjugator.Instance.Deconjugate(NormalizeToHiragana(surface)))
            if (f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal)) && HasVerbOrAdjectiveLookup(f.Text)
                && f.Process.Any(p => p.Contains("causative") || p.Contains("passive")))
                return true;
        return false;
    }

    /// <summary>さっき loses its き to a following きみ (さっきみごと → さっ|きみ|ごと); み rejoins the next token when that attests.</summary>
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
                // Drops inherited match state; a partial reading is worse than none.
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

    /// <summary>Pins a 3×+ mimetic run (ごろごろごろ) to its 2× entry; DictionaryForm holds the 2× key for reading index.</summary>
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

        // Repeated interjections (はい|はい|はい) are emphasis, and their 2× can be an unrelated word (はいはい → 這い這い).
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

    private static bool IsKanaUnitRepetition(string s, out string unit)
    {
        unit = "";
        if (s.Length < 2 || s.Length % 2 != 0 || !JapaneseTextHelper.IsAllKana(s)) return false;
        unit = s[..2];
        return IsRepetitionOf(s, unit);
    }

    private static bool IsRepetitionOf(string s, string unit)
    {
        if (s.Length == 0 || s.Length % 2 != 0 || !JapaneseTextHelper.IsAllKana(s)) return false;
        for (int k = 0; k < s.Length; k += 2)
            if (s[k] != unit[0] || s[k + 1] != unit[1]) return false;
        return true;
    }

    // Lattice pressure blobs hiragana runs into one OOV noun (ってもんじゃねえのかよ) that segments cleanly in isolation.
    private List<WordInfo> RetokeniseOovBlobs(List<WordInfo> wordInfos)
    {
        if (_sudachiConfigPath == null || _sudachiDicPath == null || HasNonNameCompoundLookup == null
            || _retokeniseOovDisabled || !SudachiInterop.StreamingAvailable)
            return wordInfos;

        List<string>? blobs = null;
        HashSet<string>? seen = null;
        foreach (var w in wordInfos)
        {
            if (!IsOovBlobCandidate(w)) continue;
            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (seen.Add(w.Text))
                (blobs ??= []).Add(w.Text);
        }
        if (blobs == null)
            return wordInfos;

        Dictionary<string, List<WordInfo>?> memo;
        try
        {
            memo = RetokeniseBlobs(blobs);
        }
        catch (Exception ex)
        {
            // A broken native context will not recover for the next blob.
            Console.WriteLine($"[Warning] RetokeniseOovBlobs: Sudachi FFI failed, disabling retokenisation for this parse: {ex.Message}");
            _retokeniseOovDisabled = true;
            return wordInfos;
        }

        List<WordInfo>? result = null;
        for (int i = 0; i < wordInfos.Count; i++)
        {
            var w = wordInfos[i];
            if (memo.TryGetValue(w.Text, out var retok) && retok is { Count: > 1 } && IsOovBlobCandidate(w))
            {
                result ??= CopyAccumulatorUpTo(wordInfos, i);
                int off = w.StartOffset;
                foreach (var rt in retok)
                {
                    // Memoized pieces are spliced at every occurrence and later stages mutate tokens in place.
                    var piece = new WordInfo(rt);
                    piece.StartOffset = off >= 0 ? off : -1;
                    off = off >= 0 ? off + piece.Text.Length : -1;
                    piece.EndOffset = off >= 0 ? off : -1;
                    result.Add(piece);
                }
                continue;
            }
            result?.Add(w);
        }
        return result ?? wordInfos;
    }

    private bool IsOovBlobCandidate(WordInfo w) =>
        w.Text.Length >= 5
        && w.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
        && w.Text.All(c => c is >= 'ぁ' and <= 'ゟ' or 'ー')
        && !HasNonNameCompoundLookup!(w.Text)
        // A resolvable, different dictionary form marks an earlier repair's merge (わがままな → わがまま), not an OOV blob.
        && (w.DictionaryForm == w.Text || string.IsNullOrEmpty(w.DictionaryForm)
            || !HasNonNameCompoundLookup(w.DictionaryForm));

    // One call, one blob per line: the native side analyses lines in isolation, so this equals per-blob calls.
    private Dictionary<string, List<WordInfo>?> RetokeniseBlobs(List<string> blobs)
    {
        var memo = new Dictionary<string, List<WordInfo>?>(blobs.Count, StringComparer.Ordinal);
        var tokens = SudachiInterop.ProcessTextStreaming(_sudachiConfigPath!, string.Join("\n", blobs), _sudachiDicPath!,
                                                         mode: _sudachiMode, userDictCsv: _sudachiUserDictCsv,
                                                         interactive: Interactive);

        // On a length mismatch the rest fall back to per-blob calls rather than shifting neighbours.
        int t = 0;
        for (int b = 0; b < blobs.Count; b++)
        {
            var blob = blobs[b];
            var pieces = new List<WordInfo>();
            int len = 0;
            while (t < tokens.Count && len < blob.Length)
            {
                pieces.Add(tokens[t]);
                len += tokens[t].Text.Length;
                t++;
            }
            if (len == blob.Length)
            {
                memo[blob] = pieces;
                continue;
            }
            for (int r = b; r < blobs.Count; r++)
                memo[blobs[r]] = SudachiInterop.ProcessTextStreaming(_sudachiConfigPath!, blobs[r], _sudachiDicPath!,
                                                                     mode: _sudachiMode, userDictCsv: _sudachiUserDictCsv,
                                                                     interactive: Interactive);
            break;
        }
        return memo;
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

            // Kana したんだ with dict form したむ is Sudachi's misread of した+んだ; the dated verb 湑む is written in kanji.
            if (w1.DictionaryForm == "したむ" && w1.Text.StartsWith("した", StringComparison.Ordinal)
                && w1.Text.Length >= 3)
            {
                string rest = w1.Text[2..];
                int mid = w1.StartOffset >= 0 ? w1.StartOffset + 2 : -1;
                // Unpinned, CombineAuxiliary re-merges したんだ and re-derives 湑む; pins bypass deconjugation, hence the chain.
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

            // それっぽく → そ|れっぽく… blob; the remainder re-tokenises in RetokeniseOovBlobs. The OOV gate spares real そ+れX.
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

            // 募|るってもんじゃねえ: a colloquial tail blobs the る; the OOV gate spares genuine 名詞+る words.
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

            // 四つ目 / みっつめ is numeral + ordinal 目, not "four-eyed" or 見詰める; kana numerals are a closed set.
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

            // 部|下達 → 部下|達 needs a 1-char X (事実|上達 stays) and a common compound, not a coincidental lookup key.
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

            // お|前山 → お前|山; ゼン compounds (前後), common ones (この|前髪) and 前+連用形 (前掛け) stay whole.
            // Gated on rank because JMDict priority tags mislead (前山's homograph ぜんざん carries news tags).
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

            // OOV 親X: a 1-char rest is the "pro-" prefix (親ソ, 親米); a longer rest means parent (親ギツネ).
            if (w1.Text.Length >= 2 && w1.Text[0] == '親'
                && w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && HasNonNameCompoundLookup?.Invoke(w1.Text) == false
                && HasNonNameCompoundLookup?.Invoke(w1.Text[1..]) == true)
            {
                string rest = w1.Text[1..];
                // A hiragana rest is a colloquial fragment where 親 means parent.
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
                    // Generic nouns come first in ソ's lookup, so the Soviet Union abbreviation needs a pin.
                    PreMatchedWordId = rest == "ソ" ? 2853158 : null
                });
                i += 1;
                continue;
            }

            // A single OOV 〜くも token (美しくも) is adverbial 〜く + も.
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

            // 当たれ → あ|たれ(垂れ); must run before CombineVerbDependant steals the あ (持って+あ).
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

            // Causative stem tagged a noun before ない (やらせ|ない); plain 仕方|ない stays split.
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

            // A following ね re-cuts 静か into 静|か(終助詞); the lookup gate spares real noun + question か.
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

            // A hiragana-dict token steals a katakana name's last mora (カティア|の → カティ|アの, アの read as あの).
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

            // Case が can't follow conjunctive から, so it is がなる "to yell" (頼むからがなるな).
            // Not a user_dic row: a がなる entry would steal ベルが鳴る-type splits lattice-wide.
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

            // 太鼓持|ちかい(近い) → 太鼓持ち|かい.
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

            // Sudachi picks 私大 "private university" over 私|大X (私大好き).
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

            // X史|上 → X|史上: 史上 binds tighter than the 史 suffix (人類史上初).
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

            // 使いで "usability" steals the で: 魔法|使いで|も|ない → 魔法使い|でもない.
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

            // Classical attributive 詮|無き → 詮無き (詮無い via the attributive-き deconj rule).
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

            // とこかも → と|こか(こく)|も; redistribute to とこ|かも.
            if (w1 is { Text: "こか", DictionaryForm: "こく", PartOfSpeech: PartOfSpeech.Verb }
                && newList.Count > 0
                && i + 1 < wordInfos.Count
                && HasCompoundLookup != null)
            {
                var prev = newList[^1];
                var w2 = wordInfos[i + 1];
                string prevPlusKo = prev.Text + "こ";
                string kaPlusNext = "か" + w2.Text;

                // Overwrites prev's POS/reading, so a coincidental name homograph of prev+こ must not qualify.
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

            // Sudachi tags んで/んだ te-forms 表現 when an expression entry exists (飛んで → 2248530 "zero; flying").
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

            // Suffix 着(ギ) before a particle is the verb 着る, unless it forms a compound (部屋着) for CombineNounCompounds.
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

            // 殺し続ける → 殺(Prefix)|し続ける; split to 殺し|続ける.
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

            // 幼|げ → 幼げ; stays IAdjective so the な handler leaves 的な/がちな alone.
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

            // のでは is の+では, never ので+は; CombineParticles then forms ではない(か).
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

                // Contracted 〜ておこう split as stem|と|こう (ためとこう).
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
                        // RepairVowelElongation may have stripped the particle's trailing ー.
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

                // Explanatory なんだ, except after an aux stem (泣きそうな) or na-adjective (好きなんだ).
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

                // そうなんじゃない → そう|なん|じゃない.
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

                // ぶち|キレ → ぶちキレる.
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

                // Verb ん: 飲ん|だけど; explanatory ん stays そう|なんだ|けど.
                bool isExplanatoryN = w1.PartOfSpeech == PartOfSpeech.Particle &&
                                      w1.HasPartOfSpeechSection(PartOfSpeechSection.Juntaijoushi);
                if (w1.Text == "ん" && w2.Text == "だ" && i + 2 < wordInfos.Count &&
                    DaCompoundSuffixes.Contains(wordInfos[i + 2].Text) &&
                    !isExplanatoryN)
                {
                    var w3 = wordInfos[i + 2];
                    newList.Add(w1);
                    var daSuffix = new WordInfo(w2) { Text = w2.Text + w3.Text, EndOffset = w3.EndOffset, PartOfSpeech = PartOfSpeech.Conjunction };
                    newList.Add(daSuffix);
                    i += 3;
                    continue;
                }

                // Sudachi splits はぐれる as は|ぐれる after で.
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

                // 言いたそう → 言|いたそう(いたす); only godan ワ行 verbs (kanji + う) hit this.
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

                // 来|なすった → 来なさる.
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

                // そう|いう → そういう (verb dict form kept for そういった); kanji 言う is literal "say" (そう言って).
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
                    // か|い stays split when い is a 居る stem with an auxiliary (聞こえているのか|いない).
                    bool kaIBlocked = w1.Text is "か" or "だ" && i + 2 < wordInfos.Count
                        && w2.DictionaryForm is "居る" or "いる"
                        && wordInfos[i + 2].Text is "ない" or "なかった" or "ます" or "た" or "て";

                    // ところで only sentence-initially or after a past (〜たところで); else locative (静かなところで).
                    bool tokoroDeBlocked = w1.Text == "ところ" && i > 0 &&
                        !(wordInfos[i - 1].Text.EndsWith("た", StringComparison.Ordinal) ||
                          wordInfos[i - 1].Text.EndsWith("だ", StringComparison.Ordinal));

                    // それじゃない is それ|じゃない, not the conjunction それじゃ.
                    bool soreJaBlocked = w1.Text is "それ" or "そん" or "そい" && w2.Text == "じゃ"
                        && i + 2 < wordInfos.Count && wordInfos[i + 2].Text is "ない" or "なかっ" or "なかった";

                    // 度に "each time" only after a verb (行う度に); after a numeral it is the counter (二度に分けて).
                    bool doNiBlocked = w1.Text == "度" && w2.Text == "に"
                        && !(i > 0 && wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb);

                    // つか must not steal the counter つ after a numeral (三つ|か四つ).
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

            if (w1 is { Text: "な", DictionaryForm: "だ" })
            {
                bool followedByN = i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "ん";

                // 好き|な|ん|だ|と → 好き|なんだと.
                if (newList.Count > 0 && IsNaAdjectiveToken(newList[^1]) && followedByN)
                {
                    // Plain だ only; conjectural だろ/だろう is a separate grammar point.
                    string combined = "な" + wordInfos[i + 1].Text;
                    int j = i + 2;
                    int lastEndOffset = wordInfos[i + 1].EndOffset;
                    if (j < wordInfos.Count && wordInfos[j].Text == "だ" && wordInfos[j].PartOfSpeech == PartOfSpeech.Auxiliary)
                    {
                        combined += wordInfos[j].Text;
                        lastEndOffset = wordInfos[j].EndOffset;
                        j++;
                    }

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

                // な|し after a na-adjective is なし; otherwise noun+な merges and orphans し.
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

                // 大切|な → 大切な; な stays separate after an aux stem (降りそうな) for learners.
                if (newList.Count > 0 && IsNaAdjectiveToken(newList[^1]) && !followedByN
                    && !newList[^1].HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem))
                {
                    newList[^1].Text += w1.Text;
                    newList[^1].EndOffset = w1.EndOffset;
                    i++;
                    continue;
                }

                // Not the vegetable 菜.
                w1.PartOfSpeech = PartOfSpeech.Particle;
            }
            // Not the baggage 荷.
            else if (w1.Text == "に")
                w1.PartOfSpeech = PartOfSpeech.Particle;

            newList.Add(w1);
            i++;
        }

        return newList;
    }

    /// <summary>Sudachi fuses noun+verb stem into one noun, orphaning the ending (足蹴|られた → 足|蹴られた, 肉食|う → 肉|食う).</summary>
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

            // 連用形 noun + た/て is the whole verb (言い出し|た → 言い出した), not the split below; だ/で is copula (手伝いだ).
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

            // そりゃそうだ eats だろう's だ, stranding ろう as 蝋; reattach keeping the expression's dict form.
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

    /// <summary>Splits an unmatched interjection that absorbed a sentence-final particle (ごめんなさいね).</summary>
    private List<WordInfo> RepairFusedInterjectionParticle(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count == 0) return wordInfos;

        List<WordInfo>? result = null;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];
            if (word.PartOfSpeech != PartOfSpeech.Interjection || word.Text.Length < 3)
            {
                result?.Add(word);
                continue;
            }

            if (HasCompoundLookup != null && HasCompoundLookup(word.Text))
            {
                result?.Add(word);
                continue;
            }

            bool split = false;
            foreach (var particle in SentenceFinalParticles.OrderByDescending(p => p.Length))
            {
                if (!word.Text.EndsWith(particle, StringComparison.Ordinal)) continue;

                var baseText = word.Text[..^particle.Length];
                if (baseText.Length < 2) continue;

                if (HasCompoundLookup != null && !HasCompoundLookup(baseText)) continue;

                result ??= CopyAccumulatorUpTo(wordInfos, i);
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
                result?.Add(word);
        }

        return result ?? wordInfos;
    }

    // A trailing run of ≥2 identical small vowels (ぇぇぇ) is expressive elongation, not part of a word.
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

    // Te-form いって: the い reattaches only when that attests (悪|いっ|て → 悪い; ギシギシい does not).
    private static readonly HashSet<string> IuIkuDictForms = ["いう", "言う", "いく", "行く"];

    // Sudachi prefers the exclamation homograph (それっ!) before って; re-tagged Pronoun so lookup picks the everyday word.
    private static readonly HashSet<string> QuotativeStrippedPronouns = ["それ", "これ", "あれ", "どれ"];

    // Nouns and に/と are excluded so どうにかなって/夢かなって keep their なる/かなう te-form reading.
    private static bool IsClauseFinalBeforeKana(WordInfo w) =>
        (w.PartOfSpeech == PartOfSpeech.Particle && w.Text == "の")
        || w.PartOfSpeech == PartOfSpeech.IAdjective
        || (w.PartOfSpeech is PartOfSpeech.Verb && w.Text == w.DictionaryForm)
        || (w.PartOfSpeech == PartOfSpeech.Auxiliary && w.Text is "ない" or "た" or "だ" or "です" or "ます" or "てる" or "でる");

    // One-step imperative/volitional only, so 信じろ+って matches and a genuine te-form (信じきって) never does.
    private bool MergesToFinalForm(string text) => OneStepFinalFormBase(text, includeVolitional: true) != null;

    private string? OneStepFinalFormBase(string text, bool includeVolitional)
    {
        if (HasCompoundLookup == null) return null;
        foreach (var f in Deconjugator.Instance.Deconjugate(text))
        {
            if (f.Process.Length != 1 || string.IsNullOrEmpty(f.Text)) continue;
            var p = f.Process[0];
            if ((p.Contains("imperative", StringComparison.Ordinal)
                 || (includeVolitional && p.Contains("volitional", StringComparison.Ordinal)))
                && HasCompoundLookup(f.Text))
                return f.Text;
        }

        return null;
    }

    private static bool HasOneStepVolitional(string text)
    {
        foreach (var f in Deconjugator.Instance.Deconjugate(text))
            if (f.Process.Length == 1 && f.Process[0].Contains("volitional", StringComparison.Ordinal))
                return true;

        return false;
    }

    // ってな(=という) takes a noun head (ってな具合), never bare に, so ってな|に and って|な|に are って + なに.
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

        // って steals a noun's tail mora (寒|さって); returns the LONGEST attested run (繋|が|り → 繋がり, not がり), 0 if none.
        int WordReattachRunLength(string add)
        {
            if (HasNonNameCompoundLookup == null || add.Length == 0) return 0;
            string acc = add;
            int best = 0;
            for (int k = 1; k <= 3 && k <= result.Count; k++)
            {
                // Stops at auxiliaries (ませ+ん), verbs (生き|て|い|こうっ|て) and が/と/を/に/か (と+いって attests 問い).
                // っ-final and bare す "verbs" are shreds (いっ|しょっ|て, びびり|す|ぎっ); the lookup gate still applies.
                if ((result[^k].PartOfSpeech == PartOfSpeech.Verb
                        && !result[^k].Text.EndsWith('っ') && result[^k].Text != "す")
                    || result[^k].PartOfSpeech == PartOfSpeech.Auxiliary
                    || result[^k] is { PartOfSpeech: PartOfSpeech.Particle, Text: "が" or "と" or "を" or "に" or "か" }) break;
                // Topic は stops too, except clause-initially where it can only be a word head (は|ずっ|て = はず).
                if (result[^k] is { PartOfSpeech: PartOfSpeech.Particle, Text: "は" })
                {
                    bool clauseInitial = result.Count <= k
                        || result[^(k + 1)].PartOfSpeech is PartOfSpeech.Symbol
                            or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace;
                    if (!clauseInitial) break;
                }
                var t = result[^k].Text;
                if (t.Length == 0) break;
                // で heads only the shred です (で|すっ|て); anything else is two real tokens (で+すく=デスク).
                if (result[^k] is { PartOfSpeech: PartOfSpeech.Particle, Text: "で" } && t + acc != "です") break;
                bool kanaOrKanji = true;
                foreach (var c in t)
                    if (c is not ((>= 'ぁ' and <= 'ゖ') or (>= '゠' and <= 'ヿ') or (>= '一' and <= '鿿'))) { kanaOrKanji = false; break; }
                if (!kanaOrKanji) break;
                acc = t + acc;
                if (HasNonNameCompoundLookup(acc))
                {
                    // All-word kana pieces (どう+こう) are two words meeting; っ-final pieces (いっ) count as shreds regardless.
                    bool allPiecesAreWords = acc.Length >= 4 && acc.All(c => c is >= 'ぁ' and <= 'ゖ');
                    if (allPiecesAreWords)
                        for (int m = 1; m <= k && allPiecesAreWords; m++)
                            allPiecesAreWords = !result[^m].Text.EndsWith('っ')
                                                && HasNonNameCompoundLookup(result[^m].Text);
                    if (!allPiecesAreWords)
                        best = k;
                }
            }
            // A stranded lone kana before the run would be deleted by FilterOrphanedMisparses; range must match its set.
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

        // The う-row check rejects fragments the deconjugator over-tags as verb pasts (はだ, んだ).
        bool IsVerbReattachment(string s) =>
            (s.Length >= 2
                && s[^1] is 'う' or 'く' or 'ぐ' or 'す' or 'つ' or 'ぬ' or 'ぶ' or 'む' or 'る'
                && HasNonNameCompoundLookup?.Invoke(s) == true
                && DeconjugatesToVerb(s))
            || MergesToFinalForm(s);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            // Nominal + だって|ば is copula だ + emphatic ってば (ダルマザメだってば), not the conjunction だって.
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

            // 将校|だっ(だつ)|て is particle だって; SplitTatteParticle already ran, and the mora-theft block would fake 将校だ.
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



            // Adverb いたって before 言う after a て-form or particle (部署に|いたって言う) is いた + quotative って.
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



            // って steals a katakana noun's tail (サナダ|ムシっ|て, エリ|アっての); the lookup gate spares サボってる.
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

            // それっ|て → それ|って, vouched by Sudachi's NormalizedForm; else CombineTte builds a bogus exclamation te-form.
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

            // がる never follows a pronoun, so 何|がっ|て is 何|が|って.
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

            // 読|むっ|て: the thief's POS is noise; the signal is the mora rebuilding a verb. あっ|て rebuilds none.
            if (i + 1 < wordInfos.Count
                && wordInfos[i].Text.Length == 2
                && wordInfos[i].Text[^1] == 'っ'
                && wordInfos[i].Text[0] is >= 'ぁ' and <= 'ゖ'
                && wordInfos[i].Text[0] != 'だ'
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
                    // An imperative Sudachi agrees on keeps its verb lemma (来い → 来る, not the interjection); 待ち合わせ is no 待ち合わす imperative.
                    var imperativeBase = OneStepFinalFormBase(verbText, includeVolitional: false);
                    if (imperativeBase != null && KanaConverter.ToHiragana(imperativeBase) != KanaConverter.ToHiragana(stemHead.DictionaryForm))
                        imperativeBase = null;
                    result.RemoveRange(result.Count - stemBack, stemBack);
                    result.Add(new WordInfo(stemHead)
                    {
                        Text = verbText,
                        DictionaryForm = imperativeBase ?? verbText,
                        NormalizedForm = imperativeBase ?? verbText,
                        PartOfSpeech = PartOfSpeech.Verb,
                        IsImperative = imperativeBase != null,
                        EndOffset = moraThief.StartOffset >= 0 ? moraThief.StartOffset + 1 : -1
                    });
                    AddTte(moraThief, wordInfos[i + 1]);
                    i++;
                    changed = true;
                    continue;
                }
            }

            // Non-verb reattachment ありがと|うっ|て → ありがとう; only interjection うっ (verb うつ reforms above).
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

            // A noun's final kanji takes って's っ as a verb (自|意|識っ[識る]|て, 若|造っ|て), blocking compound matching.
            // Adj/verb predecessors are safe only as bare kanji; a genuine 若い/識って carries okurigana.
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

            // Kanji noun read as a far rarer verb (番っ[番う]|て, 主っ[司る]|て) is noun + って/っす; 言う, 打つ outrank their nouns.
            if (i + 1 < wordInfos.Count
                && wordInfos[i] is { PartOfSpeech: PartOfSpeech.Verb }
                && wordInfos[i].Text.Length == 2
                && wordInfos[i].Text[^1] is 'っ' or 'ッ'
                && wordInfos[i].Text[0] is >= '一' and <= '鿿'
                && wordInfos[i + 1].Text is "て" or "す" or "ス"
                && GetNonNameCompoundFrequencyRank?.Invoke(wordInfos[i].Text[..1]) is { } nounRank
                && GetNonNameCompoundFrequencyRank(wordInfos[i].DictionaryForm) is { } verbRank
                && verbRank >= RareVerbShredMinRank
                && verbRank >= nounRank * RareVerbShredRankRatio)
            {
                var shred = wordInfos[i];
                var next = wordInfos[i + 1];
                var noun = shred.Text[..1];
                result.Add(new WordInfo(shred)
                {
                    Text = noun, DictionaryForm = noun, NormalizedForm = noun,
                    Reading = "",
                    PartOfSpeech = PartOfSpeech.Noun,
                    PartOfSpeechSection1 = PartOfSpeechSection.None,
                    EndOffset = shred.StartOffset >= 0 ? shred.StartOffset + 1 : -1
                });
                if (next.Text == "て")
                    AddTte(shred, next);
                else
                {
                    var tail = shred.Text[1..] + next.Text;
                    result.Add(new WordInfo(next)
                    {
                        Text = tail, DictionaryForm = "です", NormalizedForm = "です",
                        Reading = WanaKanaShaapu.WanaKana.ToKatakana(tail),
                        PartOfSpeech = PartOfSpeech.Auxiliary,
                        StartOffset = shred.EndOffset >= 0 ? shred.EndOffset - 1 : -1
                    });
                }
                i++;
                changed = true;
                continue;
            }

            // A noun can't take past た, so 貴方|た|ちっ|て is 貴方|たち|って.
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

            // 婆|さ|んっ|て|ね: the gate below filters Interjection, so ん reattaches here.
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

            // っつー steals morae like って (びびり|す|ぎっ|つーか); only the noun run applies, never a te-form.
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

            // Fused theft: 勉|強って (強って lemmatised as たって).
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

            // Auxiliary/Interjection thieves are lemmatised stolen morae too (育|ちっ|て, た|めっ|て).
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

            // のか|なっ|て → のかな|って; a clause-final token before か makes the question reading certain.
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

            // 言うな|って: a terminal form never takes なる (that is たくなって), so な is sentence-final.
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

            // 言|うなっ|て → 言う|な|って; genuine うなる "growl" follows a particle, which the prev-POS check excludes.
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

            // デー|トっ|て: a mid-katakana cut before って is never real, so shape alone suffices (OOV names too).
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

            // すご|くっ[来る]|て is すごく|って; must run before the CommonTeFormVerbs skip, which would keep くる.
            if (stripped == "く" && thief.DictionaryForm == "くる" && prev != null
                && (
                    (prev.PartOfSpeech is PartOfSpeech.IAdjective or PartOfSpeech.Prefix
                        && prev.DictionaryForm.EndsWith("い", StringComparison.Ordinal)
                        && HasNonNameCompoundLookup?.Invoke(prev.DictionaryForm) == true)
                    // Bare-kanji adverb stem (早[早い]); all-kanji keeps kana お|くっ|て (送る) out.
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

            // と|かっ[買う]|て is とか|って; a genuine 買って follows を or a noun, never と.
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

            // する has no すっ stem, so a すっ thief is always a shred (で|すっ|て = です+って).
            if (thief.PartOfSpeech is PartOfSpeech.Verb && CommonTeFormVerbs.Contains(thief.DictionaryForm)
                && !(thief.DictionaryForm == "する" && thief.Text == "すっ"))
            {
                result.Add(wordInfos[i]);
                continue;
            }

            // 繋|がりっ|て → 繋がり|って; bare う is a volitional (いこ|うっ|て) and いっ/だっ would hit particles (て+い=弟).
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

            // 必|要っ[要る]|て → 必要|って; the lookup gate keeps 家に帰って out (に+帰 does not attest).
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

                // １０|度っ|て → 度|って; needs a counter sense, since 何|言っ|て is a genuine te-form.
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

            // Later heuristics would split genuine auxiliary te-forms (食べ|ちゃっ|て), so these repair only via reattach.
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
                    // RunBfs always returns the identity form (0 steps), so exactly one step is required.
                    bool hasRealDeconjStep = false;
                    foreach (var f in forms)
                    {
                        if (f.Process.Length == 1) { hasRealDeconjStep = true; break; }
                    }
                    // いっ|て is usually 言って/行って; steal い only for a real word (悪い, not ギシギシい).
                    if (hasRealDeconjStep && IuIkuDictForms.Contains(thief.DictionaryForm)
                        && HasNonNameCompoundLookup?.Invoke(merged) != true)
                        hasRealDeconjStep = false;
                    // 信じ|きっ|て is 信じきる's te-form; leave it for CombineCompounds, not a fake 信じき + って.
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
                    // Prefer a clause-final reattachment: 信|じろっ|て → 信じろ|って.
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
                        // A genuine te-stem (わかっ+て → わかる) is no theft, unless a quote verb follows (かなっ|て|思ったら).
                        // The frame is deliberately broad: わかって思った loses its boundary, but narrowing costs more.
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
                    // Adverb-tagged するっ|て is する|って; the う-row gate keeps ことっ a noun.
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
                        EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 1 : -1
                    };
                }
                // やれ|やれっ|て → やれやれ|って; otherwise the copy is dropped as a kana stutter.
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

    // Clipped exclamatory adjective (足短っ, 寒っ); unrepaired, 短 resolves to the noun "fault; defect".
    private List<WordInfo> RepairClippedAdjective(List<WordInfo> wordInfos)
    {
        if (HasNonNameCompoundLookup == null || HasVerbOrAdjectiveLookup == null)
            return wordInfos;
        return ScanRewrite(wordInfos, TryRepairClippedAdjective);
    }

    private int TryRepairClippedAdjective(List<WordInfo> tokens, int i, List<WordInfo>? _, Func<List<WordInfo>> output)
    {
        var current = tokens[i];

        int tsuIdx = -1;
        for (int j = i + 1; j < tokens.Count && j <= i + 2; j++)
        {
            // The forced sokuon boundary leaves a blank or stop token between stem and っ.
            if (tokens[j].PartOfSpeech == PartOfSpeech.BlankSpace || tokens[j].Text == _stopToken) continue;
            if (tokens[j].Text is "っ" or "ッ") tsuIdx = j;
            break;
        }

        if (tsuIdx < 0
            || current.Text.Length == 0 || current.Text.Length > 4
            || current.PreMatchedWordId != null
            || current.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Particle
                or PartOfSpeech.Auxiliary or PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol
            // A kana stem + い hits unrelated keys (がん+い = 含意) and resurrects SFX fragments.
            || !JapaneseTextHelper.IsKanji(current.Text[^1])
            // Verb/adjective entries only, so noun collisions (兄い) don't qualify.
            || !HasVerbOrAdjectiveLookup!(current.Text + "い")
            // Vocative 隊|長っ must re-fuse to 隊長, not become 長い.
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


    /// <summary>Classical attributive き fused into the next noun (白|き尾 → 白き|尾).</summary>
    private List<WordInfo> RepairClassicalKiAdjective(List<WordInfo> wordInfos)
    {
        if (HasNonNameCompoundLookup == null || HasCompoundLookup == null)
            return wordInfos;
        return ScanRewrite(wordInfos, TryRepairClassicalKiAdjective);
    }

    private int TryRepairClassicalKiAdjective(List<WordInfo> tokens, int i, List<WordInfo>? result, Func<List<WordInfo>> output)
    {
        var current = tokens[i];
        // prev may already be rewritten this pass, so read the accumulator.
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
        // A hiragana merge must hit a kana-plausible word, else reading keys collide (はい+そう → 配送).
        var hasKanaAppropriateLookup = HasKanaAppropriateCompoundLookup ?? hasNonNameLookup;

        var deconjugator = Deconjugator.Instance;
        List<WordInfo>? result = null;

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

                // えっ|そう is two utterances; neither えっそう nor its 得る deconjugation is valid.
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
                    result ??= CopyAccumulatorUpTo(wordInfos, i);
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

                    result ??= CopyAccumulatorUpTo(wordInfos, i);
                    result.Add(BuildMergedHiraganaToken(wordInfos, i, spanLen, combinedText, form.Text, pos));
                    i += spanLen;
                    combined = true;
                    break;
                }
            }

            if (!combined)
            {
                result?.Add(wordInfos[i]);
                i++;
            }
        }

        return result ?? wordInfos;
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
