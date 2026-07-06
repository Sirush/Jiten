using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Utils;
using WanaKanaShaapu;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
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
                    if (forms.Any(f => f.Tags.Any(t => t.StartsWith("v"))))
                        shouldSplit = true;
                }

                // Pattern 1b: Te-form ending + たんか → combine with た
                // Handles cases like 怖がって + たんか where 怖がって is classified as IAdjective
                // If prev ends with て/で, adding た creates てた/でた (past progressive/resultative)
                if (!shouldSplit && (prev.Text.EndsWith("て") || prev.Text.EndsWith("で")))
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
                    if (verbBefore != null && (verbBefore.Text.EndsWith("て") || verbBefore.Text.EndsWith("で")))
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
                        if (verbBefore != null && (verbBefore.Text.EndsWith("て") || verbBefore.Text.EndsWith("で") ||
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
                  (result[^2].Text.EndsWith("て") || result[^2].Text.EndsWith("で")))))
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
                 (result[^1].Text.EndsWith("て") || result[^1].Text.EndsWith("で"))))
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

    private List<WordInfo> RepairVowelElongation(List<WordInfo> wordInfos)
    {
        bool changed = false;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var w = wordInfos[i];

            // Strip trailing ー from particles/conjunctions (colloquial elongation like けどー)
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
                !prev.Text.EndsWith("ん"))
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
                if (prevEmitted != null && (prevEmitted.Text == "ん" || prevEmitted.Text.EndsWith("ん")))
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
            if (current.Text.EndsWith("ん") && current.Text.Length > 1 && current.Text != "ん" &&
                !IsNaAdjectiveToken(current) &&
                current.PartOfSpeech != PartOfSpeech.Suffix &&
                !NormalizeToHiragana(current.DictionaryForm).EndsWith("ん") &&
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
                // Only for actual negative auxiliary (DictionaryForm = "ぬ"), not explanatory ん
                if (!combined && current.DictionaryForm == "ぬ" &&
                    TryCombineWithLookback(result, "ん", "ん", deconj, IsAnyVerbForm, out var negativeWord))
                {
                    negativeWord!.EndOffset = current.EndOffset;
                    // Sudachi identified this ん as the negative auxiliary ぬ. The deconjugator
                    // alone can't tell it from a slurred る (してん) and prefers that shorter
                    // path, so record the diagnosis for chain selection.
                    negativeWord.IsSlurredNegative = true;

                    // After combining ませ+ん→ません, try to combine preceding verb stem with ません
                    // e.g., [し, ませ] + ん → [しません]
                    if (negativeWord.Text.EndsWith("ません") && result.Count > 0)
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
    private List<WordInfo> RepairSakkiMoraTheft(List<WordInfo> wordInfos)
    {
        if (HasCompoundLookup == null) return wordInfos;

        List<WordInfo>? result = null;
        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];
            if (word is { Text: "さっ" }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].Text == "きみ")
            {
                var stolen = wordInfos[i + 1];
                result ??= [..wordInfos[..i]];
                result.Add(new WordInfo(word)
                {
                    Text = "さっき", DictionaryForm = "さっき", NormalizedForm = "さっき",
                    Reading = "サッキ", PartOfSpeech = PartOfSpeech.Noun,
                    EndOffset = word.StartOffset >= 0 ? word.StartOffset + 3 : -1
                });

                var following = i + 2 < wordInfos.Count ? wordInfos[i + 2] : null;
                if (following != null && HasCompoundLookup("み" + following.Text))
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
                    i += 2;
                }
                else
                {
                    result.Add(new WordInfo(stolen)
                    {
                        Text = "み", DictionaryForm = "み", NormalizedForm = "み",
                        Reading = "ミ", PartOfSpeech = PartOfSpeech.Noun,
                        PreMatchedWordId = null,
                        PreMatchedConjugations = null,
                        StartOffset = stolen.StartOffset >= 0 ? stolen.StartOffset + 1 : -1
                    });
                    i++;
                }
                continue;
            }

            result?.Add(word);
        }

        return result ?? wordInfos;
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
    private List<WordInfo> CollapseReduplicatedMimetic(List<WordInfo> wordInfos)
    {
        if (GetNonNameCompoundWordId == null || wordInfos.Count == 0) return wordInfos;

        List<WordInfo>? result = null;
        int i = 0;
        while (i < wordInfos.Count)
        {
            var first = wordInfos[i];
            if (IsKanaUnitRepetition(first.Text, out var unit))
            {
                int j = i + 1;
                int unitCount = first.Text.Length / 2;
                bool allInterjections = first.PartOfSpeech == PartOfSpeech.Interjection;
                while (j < wordInfos.Count && IsRepetitionOf(wordInfos[j].Text, unit))
                {
                    unitCount += wordInfos[j].Text.Length / 2;
                    allInterjections &= wordInfos[j].PartOfSpeech == PartOfSpeech.Interjection;
                    j++;
                }

                // A run of repeated interjection tokens (はい|はい|はい, まあ|まあ|まあ) is deliberate emphatic
                // speech, not a mimetic — and its 2× surface can be an unrelated word (はいはい → 這い這い
                // "crawling"). Genuine mimetics reach here as Adverb/Noun tokens, so leave interjection runs
                // to resolve token-by-token.
                if (unitCount >= 3 && !allInterjections && GetNonNameCompoundWordId(unit + unit) is { } twoXId)
                {
                    result ??= [..wordInfos[..i]];
                    string text = "", reading = "";
                    for (int k = i; k < j; k++) { text += wordInfos[k].Text; reading += wordInfos[k].Reading; }
                    result.Add(new WordInfo(first)
                    {
                        Text = text, Reading = reading,
                        DictionaryForm = unit + unit, NormalizedForm = unit + unit,
                        PartOfSpeech = PartOfSpeech.Adverb,
                        EndOffset = wordInfos[j - 1].EndOffset,
                        PreMatchedWordId = twoXId
                    });
                    i = j;
                    continue;
                }
            }

            result?.Add(first);
            i++;
        }
        return result ?? wordInfos;
    }

    // A kana string that is its own leading 2-mora unit repeated a whole number of times (ごろ, ごろごろ,
    // ぐるぐるぐる); outputs the 2-char unit.
    private static bool IsKanaUnitRepetition(string s, out string unit)
    {
        unit = "";
        if (s.Length < 2 || s.Length % 2 != 0 || !WanaKanaShaapu.WanaKana.IsKana(s)) return false;
        unit = s[..2];
        return IsRepetitionOf(s, unit);
    }

    // True if s is the 2-char unit repeated a whole number of times (kana only).
    private static bool IsRepetitionOf(string s, string unit)
    {
        if (s.Length == 0 || s.Length % 2 != 0 || !WanaKanaShaapu.WanaKana.IsKana(s)) return false;
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
                    result ??= [..wordInfos[..i]];
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

    private List<WordInfo> ProcessSpecialCases(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count == 0)
            return wordInfos;

        List<WordInfo> newList = new List<WordInfo>(wordInfos.Count);


        for (int i = 0; i < wordInfos.Count;)
        {
            WordInfo w1 = wordInfos[i];

            // そうかいそうかい ("is that so, is that so") — Sudachi mashes the middle into an OOV noun かいそうか
            // that then matches the rare 階層化 "stratification" (2345760) via its kana reading. Re-cut the
            // OOV blob into かい + そう + か (all particles/adverb), so the reduplicated そう+かい survives.
            if (w1.Text == "かいそうか" && w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun)
            {
                int o0 = w1.StartOffset;
                newList.Add(new WordInfo(w1)
                {
                    Text = "かい", DictionaryForm = "かい", NormalizedForm = "かい", Reading = "カイ",
                    PartOfSpeech = PartOfSpeech.Particle, EndOffset = o0 >= 0 ? o0 + 2 : -1
                });
                newList.Add(new WordInfo(w1)
                {
                    Text = "そう", DictionaryForm = "そう", NormalizedForm = "そう", Reading = "ソウ",
                    PartOfSpeech = PartOfSpeech.Adverb,
                    StartOffset = o0 >= 0 ? o0 + 2 : -1, EndOffset = o0 >= 0 ? o0 + 4 : -1
                });
                newList.Add(new WordInfo(w1)
                {
                    Text = "か", DictionaryForm = "か", NormalizedForm = "か", Reading = "カ",
                    PartOfSpeech = PartOfSpeech.Particle, StartOffset = o0 >= 0 ? o0 + 4 : -1
                });
                i += 1;
                continue;
            }

            // [noun]がないって: before a quotative って, Sudachi mis-lattices が+ない into がな(仮名)+いっ(言う)
            // (the whole then resolves to the rare 我鳴る "to yell" 2101910). In isolation 分がない segments
            // correctly (分|が|ない) — it's the following って that flips the lattice. Regroup the chars back to
            // が(particle) + ない(adjective 1529520) + って, gated on the exact mis-segmentation shape after a noun.
            if (w1 is { Text: "がな", NormalizedForm: "仮名" }
                && i + 2 < wordInfos.Count
                && wordInfos[i + 1] is { Text: "いっ" } igai && igai.NormalizedForm == "言う"
                && wordInfos[i + 2].Text == "て"
                && newList.Count > 0
                && newList[^1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    or PartOfSpeech.Pronoun or PartOfSpeech.Counter)
            {
                int o0 = w1.StartOffset;
                newList.Add(new WordInfo(w1)
                {
                    Text = "が", DictionaryForm = "が", NormalizedForm = "が", Reading = "ガ",
                    PartOfSpeech = PartOfSpeech.Particle, PreMatchedWordId = 2028930,
                    EndOffset = o0 >= 0 ? o0 + 1 : -1
                });
                newList.Add(new WordInfo(w1)
                {
                    Text = "ない", DictionaryForm = "ない", NormalizedForm = "無い", Reading = "ナイ",
                    PartOfSpeech = PartOfSpeech.IAdjective, PreMatchedWordId = 1529520,
                    StartOffset = o0 >= 0 ? o0 + 1 : -1, EndOffset = o0 >= 0 ? o0 + 3 : -1
                });
                newList.Add(new WordInfo(wordInfos[i + 2])
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って", Reading = "ッテ",
                    PartOfSpeech = PartOfSpeech.Particle, PreMatchedWordId = 2086960,
                    StartOffset = o0 >= 0 ? o0 + 3 : -1, EndOffset = wordInfos[i + 2].EndOffset
                });
                i += 3;
                continue;
            }

            // [predicate]ときっての: the きっての entry ("foremost of") steals とき's き after a relative
            // clause (しょうがねぇとき + って + の). きっての genuinely follows a noun (学校きっての秀才) —
            // after a clause-final predicate the reading is とき + quotative って + の. Re-cut accordingly.
            if (w1.Text == "と" && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "きっての"
                && newList.Count > 0
                && newList[^1].PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective
                    or PartOfSpeech.Auxiliary or PartOfSpeech.Expression)
            {
                var kitteno = wordInfos[i + 1];
                int o0 = w1.StartOffset;
                newList.Add(new WordInfo(w1)
                {
                    Text = "とき", DictionaryForm = "とき", NormalizedForm = "時", Reading = "トキ",
                    PartOfSpeech = PartOfSpeech.Noun, EndOffset = o0 >= 0 ? o0 + 2 : -1
                });
                newList.Add(new WordInfo(kitteno)
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って", Reading = "ッテ",
                    PartOfSpeech = PartOfSpeech.Particle, PreMatchedWordId = 2086960,
                    StartOffset = o0 >= 0 ? o0 + 2 : -1, EndOffset = o0 >= 0 ? o0 + 4 : -1
                });
                newList.Add(new WordInfo(kitteno)
                {
                    Text = "の", DictionaryForm = "の", NormalizedForm = "の", Reading = "ノ",
                    PartOfSpeech = PartOfSpeech.Particle,
                    StartOffset = o0 >= 0 ? o0 + 4 : -1, EndOffset = kitteno.EndOffset
                });
                i += 2;
                continue;
            }

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

            // ありがてぇ (colloquial 有り難い / ありがたい 1541560, the ai→ee casual form): Sudachi shreds it
            // あり|が|てぇ. Most ai→ee adjectives (すげぇ/あぶねぇ/うるせぇ) Sudachi normalises whole, but this
            // fixed compound shreds — recombine and pin.
            if (w1.Text == "あり" && i + 2 < wordInfos.Count
                && wordInfos[i + 1].Text == "が" && wordInfos[i + 2].Text == "てぇ")
            {
                newList.Add(new WordInfo(w1)
                {
                    Text = "ありがてぇ", DictionaryForm = "ありがたい", NormalizedForm = "ありがたい",
                    PartOfSpeech = PartOfSpeech.IAdjective, Reading = "アリガテェ",
                    PreMatchedWordId = 1541560,
                    PreMatchedConjugations = PinnedConjugationProcess("ありがてぇ", "ありがたい"),
                    EndOffset = wordInfos[i + 2].EndOffset
                });
                i += 3;
                continue;
            }

            // 貸り (non-standard spelling of 借り, entry 貸りる 1323560): Sudachi tags 貸 as a Noun + り(Aux).
            // Merge to the verb renyokei 貸り so the inflection (貸りたい) combines and resolves.
            if (w1.Text == "貸" && w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "り"
                && HasNonNameCompoundLookup?.Invoke("貸りる") == true)
            {
                newList.Add(new WordInfo(w1)
                {
                    Text = "貸り", DictionaryForm = "貸りる", NormalizedForm = "貸りる",
                    PartOfSpeech = PartOfSpeech.Verb, Reading = "カリ",
                    EndOffset = wordInfos[i + 1].EndOffset
                });
                i += 2;
                continue;
            }

            // こんな/そんな/あんな/どんな + の: Sudachi cuts the 連体詞 as こん|なの (and the scorer then
            // mismatches こん to 紺 "navy"). Re-cut to こんな + の when こんな etc. is a real word. Gated on
            // the 連体詞 POS so the genuine noun reading (色は紺なの "it's navy") is left untouched.
            if (w1.PartOfSpeech == PartOfSpeech.PrenounAdjectival
                && w1.Text is "こん" or "そん" or "あん" or "どん"
                && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "なの"
                && HasCompoundLookup != null && HasCompoundLookup(w1.Text + "な"))
            {
                var nano = wordInfos[i + 1];
                int naEnd = nano.StartOffset >= 0 ? nano.StartOffset + 1 : w1.EndOffset;
                newList.Add(new WordInfo(w1)
                {
                    Text = w1.Text + "な", DictionaryForm = w1.Text + "な", NormalizedForm = w1.Text + "な",
                    Reading = w1.Reading + "ナ", EndOffset = naEnd
                });
                newList.Add(new WordInfo
                {
                    Text = "の", DictionaryForm = "の", NormalizedForm = "の",
                    PartOfSpeech = PartOfSpeech.Particle, Reading = "ノ",
                    StartOffset = naEnd, EndOffset = nano.EndOffset
                });
                i += 2;
                continue;
            }

            // 〜つ目 that Sudachi keeps as one token (四つ目) is number + つ + the ordinal suffix 目 "-th"
            // (1604890), not the noun "four-eyed". Split so it matches the split 三つ目.
            if (w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                && w1.Text.Length >= 3 && w1.Text.EndsWith("つ目", StringComparison.Ordinal)
                && TakesOrdinalMeAfterTsu(w1.Text[0]))
            {
                var numPart = w1.Text[..^1];   // 四つ
                int mid = w1.StartOffset >= 0 ? w1.StartOffset + numPart.Length : -1;
                newList.Add(new WordInfo(w1)
                {
                    Text = numPart, DictionaryForm = numPart, NormalizedForm = numPart,
                    Reading = w1.Reading.Length > 0 ? w1.Reading[..^1] : "", EndOffset = mid
                });
                newList.Add(new WordInfo
                {
                    Text = "目", DictionaryForm = "目", NormalizedForm = "目",
                    PartOfSpeech = PartOfSpeech.Suffix, Reading = "メ",
                    PreMatchedWordId = 1604890, PreMatchedReadingIndex = 0,
                    StartOffset = mid, EndOffset = w1.EndOffset
                });
                i += 1;
                continue;
            }

            // 化け物どもめ etc.: Sudachi cuts the plural+derogatory suffix run ども+め as prefix ど +
            // noun もめ (揉め). After a content noun, recut to ども (plural suffix) + め (derogatory suffix).
            if (w1 is { Text: "ど", PartOfSpeech: PartOfSpeech.Prefix }
                && i + 1 < wordInfos.Count && wordInfos[i + 1] is { Text: "もめ" } w2dm
                && newList.Count > 0 && newList[^1].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    or PartOfSpeech.Name or PartOfSpeech.Pronoun)
            {
                int mid = w1.EndOffset >= 0 ? w1.EndOffset + 1 : -1;
                newList.Add(new WordInfo(w1)
                {
                    Text = "ども", DictionaryForm = "ども", NormalizedForm = "ども",
                    PartOfSpeech = PartOfSpeech.Suffix, Reading = "ドモ", EndOffset = mid
                });
                newList.Add(new WordInfo(w2dm)
                {
                    Text = "め", DictionaryForm = "め", NormalizedForm = "め",
                    PartOfSpeech = PartOfSpeech.Suffix, Reading = "メ", StartOffset = mid
                });
                i += 2;
                continue;
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
                && IsKatakanaTextChar(w1.Text[0]) && w1.Text[0] != 'ー'
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

            // Sudachi misclassifies katakana particle sequences as nouns (e.g. ノヨ → name 乃代).
            // Split into individual particles: ノ + ヨ, ノネ, ノサ, etc.
            if (w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.NaAdjective && w1.Text == "ノヨ")
            {
                newList.Add(new WordInfo { Text = "ノ", DictionaryForm = "の", NormalizedForm = "の", PartOfSpeech = PartOfSpeech.Particle, Reading = "ノ", StartOffset = w1.StartOffset, EndOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1 });
                newList.Add(new WordInfo { Text = "ヨ", DictionaryForm = "よ", NormalizedForm = "よ", PartOfSpeech = PartOfSpeech.Particle, Reading = "ヨ", StartOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1, EndOffset = w1.EndOffset });
                i++;
                continue;
            }

            // Clause-initial いいか、/いいか! is the "Listen!" interjection (JMDict 2555520), not
            // adjective いい + question か. Gated on clause position AND a following 、/! so the
            // genuine question reading keeps its split everywhere else (行っていいか分からない,
            // standalone いいか?).
            if (w1 is { Text: "いい", PartOfSpeech: PartOfSpeech.IAdjective }
                && (newList.Count == 0 || newList[^1].PartOfSpeech is PartOfSpeech.SupplementarySymbol
                    or PartOfSpeech.Symbol or PartOfSpeech.BlankSpace)
                && i + 2 < wordInfos.Count
                && wordInfos[i + 1] is { Text: "か", PartOfSpeech: PartOfSpeech.Particle }
                && wordInfos[i + 2].Text is "、" or "!" or "！")
            {
                var ka = wordInfos[i + 1];
                newList.Add(new WordInfo
                {
                    Text = "いいか",
                    DictionaryForm = "いいか",
                    NormalizedForm = "いいか",
                    PartOfSpeech = PartOfSpeech.Expression,
                    Reading = "イイカ",
                    PreMatchedWordId = 2555520,
                    StartOffset = w1.StartOffset,
                    EndOffset = ka.EndOffset
                });
                i += 2;
                continue;
            }

            // A case-particle が cannot follow conjunctive から — the kana verb がなる "to yell"
            // (2101910, uk) is the only grammatical reading (頼むからがなるな). No dictionary
            // entry: a がなる row would steal ベルが鳴る-type splits lattice-wide.
            if (w1 is { Text: "が", PartOfSpeech: PartOfSpeech.Particle }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].DictionaryForm is "成る" or "なる"
                && wordInfos[i + 1].PartOfSpeech == PartOfSpeech.Verb
                && newList.Count > 0 && newList[^1].Text.EndsWith("から"))
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

            // Standalone ひと tagged with Sudachi's 一-lexeme is almost always 人 — the 一 lemma
            // otherwise feeds the scorer a +50 lemma bonus toward the bound-prefix entry
            // (しょうがないひと must be 人, not 一). Genuine prefix usages (ひと月) are tagged
            // 接頭辞 or kept fused by Sudachi.
            if (w1 is { Text: "ひと", PartOfSpeech: PartOfSpeech.Noun }
                && (w1.DictionaryForm == "一" || w1.NormalizedForm == "一"))
            {
                newList.Add(new WordInfo(w1) { DictionaryForm = "人", NormalizedForm = "人" });
                i++;
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
            if (w1.PartOfSpeech == PartOfSpeech.Noun && w1.Text.Length >= 3 && w1.Text.EndsWith("史")
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
            if (w1.PartOfSpeech == PartOfSpeech.Noun && w1.Text.Length >= 3 && w1.Text.EndsWith("で")
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

            // Suffix かねる + negative = the lexicalized かねない "quite capable of" (破りかねない,
            // 破りかねなかった). Sudachi's df=かねる makes the gate exact (金 is impossible here).
            if (w1 is { PartOfSpeech: PartOfSpeech.Suffix, DictionaryForm: "かねる" } && w1.Text == "かね"
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].Text is "ない" or "なかっ")
            {
                bool hasTa = wordInfos[i + 1].Text == "なかっ" && i + 2 < wordInfos.Count && wordInfos[i + 2].Text == "た";
                string negText = hasTa ? "なかった" : wordInfos[i + 1].Text;
                if (negText != "なかっ")
                {
                    var lastTok = hasTa ? wordInfos[i + 2] : wordInfos[i + 1];
                    newList.Add(new WordInfo
                    {
                        Text = "かね" + negText,
                        DictionaryForm = "かねない",
                        NormalizedForm = "かねない",
                        PartOfSpeech = PartOfSpeech.Expression,
                        Reading = "カネ" + (hasTa ? "ナカッタ" : "ナイ"),
                        StartOffset = w1.StartOffset, EndOffset = lastTok.EndOffset
                    });
                    i += hasTa ? 3 : 2;
                    continue;
                }
            }

            // Imperative たまえ after a renyokei verb: 気をつけ|た|まえ → 気をつけ + たまえ
            // (×た前 is ungrammatical — "before X-ing" is る前/の前).
            if (w1 is { Text: "た", PartOfSpeech: PartOfSpeech.Auxiliary } && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { Text: "まえ", PartOfSpeech: PartOfSpeech.Noun }
                && newList.Count > 0 && newList[^1].PartOfSpeech == PartOfSpeech.Verb)
            {
                newList.Add(new WordInfo
                {
                    Text = "たまえ", DictionaryForm = "たまえ", NormalizedForm = "たまえ",
                    PartOfSpeech = PartOfSpeech.Suffix, Reading = "タマエ",
                    StartOffset = w1.StartOffset, EndOffset = wordInfos[i + 1].EndOffset
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

            if (w1 is { Text: "っけ", PartOfSpeech: PartOfSpeech.Suffix })
            {
                newList.Add(new WordInfo(w1) { PartOfSpeech = PartOfSpeech.Particle, PartOfSpeechSection1 = PartOfSpeechSection.SentenceEndingParticle });
                i++;
                continue;
            }

            // Sudachi fuses 私+戦 → 私戦(しせん "private war") from system dict.
            // When followed by いたく (Sudachi: adverb 痛く), it's 私(わたし)+戦いたく(want to fight).
            if (w1 is { Text: "私戦", PartOfSpeech: PartOfSpeech.Noun or PartOfSpeech.CommonNoun }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { Text: "いたく", NormalizedForm: "痛く" })
            {
                var w2 = wordInfos[i + 1];
                int mid = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1;
                newList.Add(new WordInfo
                {
                    Text = "私", DictionaryForm = "私", NormalizedForm = "私",
                    PartOfSpeech = PartOfSpeech.Pronoun, Reading = "ワタシ",
                    StartOffset = w1.StartOffset, EndOffset = mid,
                });
                newList.Add(new WordInfo
                {
                    Text = "戦いたく", DictionaryForm = "戦う", NormalizedForm = "戦う",
                    PartOfSpeech = PartOfSpeech.Verb, Reading = "タタカイタク",
                    StartOffset = mid, EndOffset = w2.EndOffset,
                });
                i += 2;
                continue;
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

            // Sudachi sometimes glues a trailing particle/aux into a 表現 token (e.g. 人前で, 様に, おけばいい).
            // Split them back so the parser can match the underlying noun/verb + particle separately.
            if (w1.PartOfSpeech == PartOfSpeech.Expression)
            {
                (string head, string tail, PartOfSpeech headPos, PartOfSpeech tailPos, string tailReading)? split = w1.Text switch
                {
                    "人前で" => ("人前", "で", PartOfSpeech.Noun, PartOfSpeech.Particle, "デ"),
                    "様に" => ("様", "に", PartOfSpeech.Noun, PartOfSpeech.Particle, "ニ"),
                    "誰だって" => ("誰", "だって", PartOfSpeech.Pronoun, PartOfSpeech.Particle, "ダッテ"),
                    _ => null,
                };
                if (split is { } s)
                {
                    int mid = w1.StartOffset >= 0 ? w1.StartOffset + s.head.Length : -1;
                    newList.Add(new WordInfo
                    {
                        Text = s.head,
                        DictionaryForm = s.head,
                        NormalizedForm = s.head,
                        PartOfSpeech = s.headPos,
                        Reading = "",
                        StartOffset = w1.StartOffset,
                        EndOffset = mid,
                    });
                    newList.Add(new WordInfo
                    {
                        Text = s.tail,
                        DictionaryForm = s.tail,
                        NormalizedForm = s.tail,
                        PartOfSpeech = s.tailPos,
                        Reading = s.tailReading,
                        StartOffset = mid,
                        EndOffset = w1.EndOffset,
                    });
                    i++;
                    continue;
                }

                // Split おけばいい into おけ + ば + いい so the preceding 放って can form compound 放っておく.
                if (w1.Text == "おけばいい")
                {
                    int o1 = w1.StartOffset >= 0 ? w1.StartOffset + 2 : -1;
                    int o2 = w1.StartOffset >= 0 ? w1.StartOffset + 3 : -1;
                    newList.Add(new WordInfo
                    {
                        Text = "おけ", DictionaryForm = "おく", NormalizedForm = "おく",
                        PartOfSpeech = PartOfSpeech.Verb, Reading = "オケ",
                        StartOffset = w1.StartOffset, EndOffset = o1,
                    });
                    newList.Add(new WordInfo
                    {
                        Text = "ば", DictionaryForm = "ば", NormalizedForm = "ば",
                        PartOfSpeech = PartOfSpeech.Particle, Reading = "バ",
                        StartOffset = o1, EndOffset = o2,
                    });
                    newList.Add(new WordInfo
                    {
                        Text = "いい", DictionaryForm = "いい", NormalizedForm = "いい",
                        PartOfSpeech = PartOfSpeech.IAdjective, Reading = "イイ",
                        StartOffset = o2, EndOffset = w1.EndOffset,
                    });
                    i++;
                    continue;
                }
            }

            if (w1 is { PartOfSpeech: PartOfSpeech.Conjunction or PartOfSpeech.Auxiliary, Text: "で" })
            {
                if (i + 1 < wordInfos.Count && wordInfos[i + 1] is { Text: "しょう", PartOfSpeech: PartOfSpeech.Noun })
                {
                    var w2 = wordInfos[i + 1];
                    newList.Add(new WordInfo(w1)
                    {
                        Text = "でしょう", EndOffset = w2.EndOffset,
                        DictionaryForm = "でしょう", NormalizedForm = "です",
                        PartOfSpeech = PartOfSpeech.Expression,
                        PartOfSpeechSection1 = PartOfSpeechSection.None,
                        Reading = "デショウ"
                    });
                    i += 2;
                    continue;
                }

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
                && (w1.Text.EndsWith("んで") || w1.Text.EndsWith("んだ")))
            {
                var hiragana = NormalizeToHiragana(w1.Text);
                var deconjForms = PipelineCachedDeconjugate(hiragana);
                var verbForm = deconjForms.FirstOrDefault(f =>
                    f.Tags.Any(t => t is "v5b" or "v5m" or "v5n" or "v5g") &&
                    (f.Text.EndsWith("ぶ") || f.Text.EndsWith("む") || f.Text.EndsWith("ぬ") || f.Text.EndsWith("ぐ")));
                if (verbForm != null)
                {
                    var prefix = w1.Text[..^2];
                    w1.PartOfSpeech = PartOfSpeech.Verb;
                    w1.DictionaryForm = prefix + verbForm.Text[^1];
                }
            }

            // Sudachi misclassifies standalone ぬ as the archaic verb 寝(ぬ) (文語下二段-ナ行)
            // instead of the classical negative auxiliary ぬ (助動詞-ヌ, NormalizedForm ず)
            if (w1 is { Text: "ぬ", PartOfSpeech: PartOfSpeech.Verb, NormalizedForm: "寝る" })
            {
                w1.PartOfSpeech = PartOfSpeech.Auxiliary;
                w1.NormalizedForm = "ず";
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

            // Sudachi always tags なれ as 代名詞 with dictForm=汝 (archaic pronoun "thou"),
            // but in modern text なれ is almost always the 命令形 or 可能形 stem of 成る.
            // Reclassify as Verb so the parser matches WID 1375610 (成る) instead of 2174460 (汝).
            if (w1 is { Text: "なれ", PartOfSpeech: PartOfSpeech.Pronoun, NormalizedForm: "汝" })
            {
                w1.PartOfSpeech = PartOfSpeech.Verb;
                w1.DictionaryForm = "なる";
                w1.NormalizedForm = "なる";
                w1.Reading = "ナレ";
                newList.Add(w1);
                i++;
                continue;
            }

            if (w1 is { PartOfSpeech: PartOfSpeech.Prefix, Text: "今" })
            {
                w1.PartOfSpeech = PartOfSpeech.Adverb;
                newList.Add(w1);
                i++;
                continue;
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
                    DictionaryForm = next.DictionaryForm?.StartsWith("し") == true
                        ? next.DictionaryForm[1..]
                        : next.Text[1..],
                    NormalizedForm = next.NormalizedForm?.StartsWith("し") == true
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

            // 空 as 形状詞/ウツロ (utsuro) → noun/カラ (kara, "empty")
            // Sudachi misclassifies 空 as na-adjective うつろ, but kanji 空 in modern Japanese
            // almost always reads から (empty) — うつろ is typically written 虚ろ
            if (w1 is { Text: "空", PartOfSpeech: PartOfSpeech.NaAdjective, Reading: "ウツロ" })
            {
                w1.PartOfSpeech = PartOfSpeech.Noun;
                w1.Reading = "カラ";
                w1.NormalizedForm = "空";
                newList.Add(w1);
                i++;
                continue;
            }

            // Combine 形状詞的 suffixes (げ) with preceding adjective stem
            // e.g., 幼(adj-stem) + げ(suffix/形状詞的) → 幼げ
            // Keep as IAdjective so な handler doesn't incorrectly merge (的な, がちな stay unchanged)
            if (w1.PartOfSpeech == PartOfSpeech.Suffix
                && w1.HasPartOfSpeechSection(PartOfSpeechSection.NaAdjectiveLike)
                && newList.Count > 0
                && newList[^1].PartOfSpeech == PartOfSpeech.IAdjective
                && !newList[^1].Text.EndsWith("い"))
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
                        f.Tags.Count > 0 && f.Tags.Count <= 6 &&
                        f.Tags.Any(t => t.StartsWith("v")) &&
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
                if (SpecialCases3Dict.TryGetValue(w1.Text, out var sc3Candidates))
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

                // Sudachi parses 何でしょう as 何で(noun) + しょう(noun).
                // Split into 何(pronoun) + でしょう(expression).
                if (w1 is { Text: "何で", PartOfSpeech: PartOfSpeech.Noun } && w2 is { Text: "しょう", PartOfSpeech: PartOfSpeech.Noun })
                {
                    int mid = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1;
                    newList.Add(new WordInfo(w1)
                    {
                        Text = "何", DictionaryForm = "何", NormalizedForm = "何",
                        PartOfSpeech = PartOfSpeech.Pronoun, Reading = "ナニ",
                        EndOffset = mid
                    });
                    newList.Add(new WordInfo(w2)
                    {
                        Text = "でしょう", DictionaryForm = "でしょう", NormalizedForm = "です",
                        PartOfSpeech = PartOfSpeech.Expression,
                        PartOfSpeechSection1 = PartOfSpeechSection.None,
                        Reading = "デショウ",
                        StartOffset = mid, EndOffset = w2.EndOffset
                    });
                    i += 2;
                    continue;
                }

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

                // に + しろ → にしろ (particle "even if") only when preceded by a verb/adjective/auxiliary
                // e.g., 行くにしろ → 行く + にしろ (whether one goes...)
                // Skip when preceded by a noun: 大概にしろ → 大概 + に + しろ (imperative of 大概にする)
                if (w1.Text == "に" && w2.Text == "しろ")
                {
                    bool prevIsNoun = i > 0 && wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Noun
                        or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun;
                    if (!prevIsNoun)
                    {
                        var newWord = new WordInfo(w1) { Text = "にしろ", EndOffset = w2.EndOffset, DictionaryForm = "にしろ", PartOfSpeech = PartOfSpeech.Expression };
                        newList.Add(newWord);
                        i += 2;
                        continue;
                    }
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

                // Sudachi misparsing verb stem + とくと as verb + 篤と (adverb)
                // Should be verb + とく (ておく contraction, auxiliary) + と (particle)
                // e.g., 見とくと → 見 + とく + と, 食べとくと → 食べ + とく + と
                if (w1.PartOfSpeech == PartOfSpeech.Verb &&
                    w2 is { PartOfSpeech: PartOfSpeech.Adverb, Text: "とくと", NormalizedForm: "篤と" })
                {
                    newList.Add(w1);
                    newList.Add(new WordInfo
                    {
                        Text = "とく", DictionaryForm = "とく", NormalizedForm = "とく",
                        PartOfSpeech = PartOfSpeech.Auxiliary, Reading = "トク",
                        StartOffset = w2.StartOffset,
                        EndOffset = w2.StartOffset >= 0 ? w2.StartOffset + 2 : -1
                    });
                    newList.Add(new WordInfo
                    {
                        Text = "と", DictionaryForm = "と", NormalizedForm = "と",
                        PartOfSpeech = PartOfSpeech.Particle, Reading = "ト",
                        StartOffset = w2.StartOffset >= 0 ? w2.StartOffset + 2 : -1,
                        EndOffset = w2.EndOffset
                    });
                    i += 2;
                    continue;
                }

                // Sudachi misparsing verb stem + とくよう as verb/noun + 徳用 (na-adj)
                // Should be verb/noun + とく (ておく contraction, auxiliary) + よう (formal noun)
                // e.g., 片づけとくように → 片づけ + とく + よう + に
                // w1 accepts Noun because Sudachi often classifies verb continuative forms as nouns
                if (w1.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Noun &&
                    w2 is { Text: "とくよう", NormalizedForm: "徳用" })
                {
                    newList.Add(w1);
                    newList.Add(new WordInfo
                    {
                        Text = "とく", DictionaryForm = "とく", NormalizedForm = "とく",
                        PartOfSpeech = PartOfSpeech.Auxiliary, Reading = "トク",
                        StartOffset = w2.StartOffset,
                        EndOffset = w2.StartOffset >= 0 ? w2.StartOffset + 2 : -1
                    });
                    newList.Add(new WordInfo
                    {
                        Text = "よう", DictionaryForm = "よう", NormalizedForm = "よう",
                        PartOfSpeech = PartOfSpeech.Noun, Reading = "ヨウ",
                        StartOffset = w2.StartOffset >= 0 ? w2.StartOffset + 2 : -1,
                        EndOffset = w2.EndOffset
                    });
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

                if (w1 is { Text: "しよう", PartOfSpeech: PartOfSpeech.Noun } &&
                    w2 is { Text: "として" })
                {
                    newList.Add(new WordInfo(w1)
                    {
                        Text = "しようとして", EndOffset = w2.EndOffset,
                        Reading = "シヨウトシテ",
                        DictionaryForm = "しようとする", PartOfSpeech = PartOfSpeech.Verb
                    });
                    i += 2;
                    continue;
                }

                // Sudachi parses 恋って as te-form of 恋う (archaic), but in modern Japanese
                // it's almost always 恋(noun) + って(quotation particle)
                if (w1 is { Text: "恋っ", PartOfSpeech: PartOfSpeech.Verb, DictionaryForm: "恋う" }
                    && w2 is { Text: "て", PartOfSpeech: PartOfSpeech.Particle })
                {
                    newList.Add(new WordInfo(w1)
                    {
                        Text = "恋", DictionaryForm = "恋", NormalizedForm = "恋",
                        PartOfSpeech = PartOfSpeech.Noun, Reading = "コイ",
                        EndOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1
                    });
                    newList.Add(new WordInfo(w2)
                    {
                        Text = "って", DictionaryForm = "って", NormalizedForm = "って",
                        PartOfSpeech = PartOfSpeech.Particle,
                        StartOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1
                    });
                    i += 2;
                    continue;
                }

                // Sudachi splits 逆に as 逆(名詞) + に(助動詞/だ). In modern Japanese
                // 逆に is almost always a single adverb ("conversely, on the contrary").
                if (w1 is { Text: "逆", PartOfSpeech: PartOfSpeech.Noun } &&
                    w2 is { Text: "に", DictionaryForm: "だ" })
                {
                    newList.Add(new WordInfo(w1)
                    {
                        Text = "逆に", EndOffset = w2.EndOffset,
                        DictionaryForm = "逆に", PartOfSpeech = PartOfSpeech.Adverb,
                        Reading = "ギャクニ"
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

                // Sudachi misidentifies し as a conjunction (接続詞) in しようとして,
                // splitting into し (conjunction) + ようとして (expression).
                // Recombine into しようとして (te-form of しようとする).
                if (w1 is { Text: "し", PartOfSpeech: PartOfSpeech.Conjunction } &&
                    w2 is { Text: "ようとして" })
                {
                    newList.Add(new WordInfo(w1)
                    {
                        Text = "しようとして", EndOffset = w2.EndOffset,
                        Reading = w1.Reading + w2.Reading,
                        DictionaryForm = "しようとして", PartOfSpeech = PartOfSpeech.Verb
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

                    foreach (var sc in sc2Candidates)
                    {
                        if (sc.Second == "い" && kaIBlocked) continue;
                        if (sc.Second == "で" && tokoroDeBlocked) continue;
                        if (sc.Second == "じゃ" && soreJaBlocked) continue;
                        if (sc.Second == "に" && doNiBlocked) continue;
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

            // This word is (sometimes?) parsed as auxiliary for some reason
            if (w1.Text == "でしょう")
            {
                var newWord = new WordInfo(w1);
                newWord.PartOfSpeech = PartOfSpeech.Expression;
                newWord.PartOfSpeechSection1 = PartOfSpeechSection.None;

                newList.Add(newWord);
                i++;
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

            // Always process よう as the noun
            if (w1.Text is "よう")
                w1.PartOfSpeech = PartOfSpeech.Noun;

            if (w1.Text is "十五")
                w1.PartOfSpeech = PartOfSpeech.Numeral;

            if (w1.Text is "オレ")
                w1.PartOfSpeech = PartOfSpeech.Pronoun;

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
        if (noun.DictionaryForm.EndsWith(verbStem))
            noun.DictionaryForm = noun.DictionaryForm[..^w];
        if (noun.NormalizedForm.EndsWith(verbStem))
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

    private static bool IsKatakanaTextChar(char c) => c is (>= 'ァ' and <= 'ヺ') or 'ー';

    // True when text deconjugates in exactly one step to a clause-final conjugation
    // (imperative/volitional) of a real JMDict word. Stem/infinitive chains are rejected so
    // genuine te-forms (信じきって) never match while quotative re-cuts (信じろ+って) do.
    private bool MergesToFinalForm(string text)
    {
        if (HasCompoundLookup == null) return false;
        foreach (var f in Deconjugator.Instance.Deconjugate(text))
        {
            if (f.Process.Count != 1 || string.IsNullOrEmpty(f.Text)) continue;
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
            if (f.Process.Count == 1 && f.Process[0].Contains("volitional", StringComparison.Ordinal))
                return true;

        return false;
    }

    // Quotative って stealing the な of an interrogative なに(何): Sudachi reads ってなに as the colloquial
    // tag ってな(=という) + に, or splits it って|な|に. The colloquial ってな only ever takes a noun head
    // (ってな具合/ってな歌), never the bare particle に — so な+に right after って is always 何 (vs the
    // colloquial ってな+noun form). Recombine both shapes to って + なに.
    private List<WordInfo> RepairTteNani(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        List<WordInfo>? newList = null;

        WordInfo Nani(WordInfo basis, int startOffset, int endOffset) => new(basis)
        {
            Text = "なに", DictionaryForm = "なに", NormalizedForm = "何", Reading = "ナニ",
            PartOfSpeech = PartOfSpeech.Noun, PartOfSpeechSection1 = PartOfSpeechSection.None,
            PreMatchedWordId = 1577100,
            StartOffset = startOffset, EndOffset = endOffset
        };

        for (int i = 0; i < wordInfos.Count; i++)
        {
            // Fused: ってな + に  ->  って + なに
            if (wordInfos[i].Text == "ってな" && i + 1 < wordInfos.Count && wordInfos[i + 1].Text == "に")
            {
                newList ??= [..wordInfos[..i]];
                var tteNa = wordInfos[i];
                var ni = wordInfos[i + 1];
                int mid = tteNa.StartOffset >= 0 ? tteNa.StartOffset + 2 : -1;
                newList.Add(new WordInfo(tteNa)
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って", Reading = "ッテ",
                    PartOfSpeech = PartOfSpeech.Particle, EndOffset = mid
                });
                newList.Add(Nani(ni, mid, ni.EndOffset));
                i++;
                continue;
            }

            // Split: って + な + に  ->  って + なに
            if (wordInfos[i].Text == "って" && i + 2 < wordInfos.Count
                && wordInfos[i + 1].Text == "な" && wordInfos[i + 2].Text == "に")
            {
                newList ??= [..wordInfos[..i]];
                var na = wordInfos[i + 1];
                var ni = wordInfos[i + 2];
                newList.Add(wordInfos[i]);
                newList.Add(Nani(na, na.StartOffset, ni.EndOffset));
                i += 2;
                continue;
            }

            newList?.Add(wordInfos[i]);
        }

        return newList ?? wordInfos;
    }

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
                // The case particle が never belongs inside a reattached noun: stop so が + 行って is not glued
                // into the kana-row noun が行 (気が行って) and the verb keeps its mora.
                if (result[^k].PartOfSpeech == PartOfSpeech.Auxiliary
                    || result[^k] is { PartOfSpeech: PartOfSpeech.Particle, Text: "が" }) break;
                var t = result[^k].Text;
                if (t.Length == 0) break;
                bool kanaOrKanji = true;
                foreach (var c in t)
                    if (c is not ((>= 'ぁ' and <= 'ゖ') or (>= '゠' and <= 'ヿ') or (>= '一' and <= '鿿'))) { kanaOrKanji = false; break; }
                if (!kanaOrKanji) break;
                acc = t + acc;
                if (HasNonNameCompoundLookup(acc)) best = k;
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



            // いたって[Adverb] homograph: after a て-form (te-iru している→していた) and before いう it is いた
            // (past of いる) + quotative って, not the adverb いたって ("extremely"). Split so the て-form keeps
            // its いた and って clusters into っていう. Gated on a preceding て-form, which the genuine adverb
            // (いたって元気) never has, and on a following いう.
            if (result.Count > 0
                && wordInfos[i] is { Text: "いたって", PartOfSpeech: PartOfSpeech.Adverb }
                && i + 1 < wordInfos.Count && wordInfos[i + 1] is { Text: "いう", DictionaryForm: "いう" }
                && (result[^1].Text.EndsWith("て", StringComparison.Ordinal)
                    || result[^1].Text.EndsWith("で", StringComparison.Ordinal)))
            {
                var fused = wordInfos[i];
                var iu = wordInfos[i + 1];
                int mid = fused.StartOffset >= 0 ? fused.StartOffset + 2 : -1;
                result.Add(new WordInfo(fused)
                {
                    Text = "いた", DictionaryForm = "いる", NormalizedForm = "いる", Reading = "イタ",
                    PartOfSpeech = PartOfSpeech.Verb, EndOffset = mid
                });
                result.Add(new WordInfo(iu)
                {
                    Text = "っていう", DictionaryForm = "っていう", NormalizedForm = "っていう", Reading = "ッテイウ",
                    PartOfSpeech = PartOfSpeech.Conjunction, StartOffset = mid, EndOffset = iu.EndOffset
                });
                i++;
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
                while (kl < cur.Length && IsKatakanaTextChar(cur[kl])) kl++;

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

            if (i + 1 >= wordInfos.Count
                || wordInfos[i].Text.Length < 2
                || wordInfos[i].Text[^1] != 'っ'
                || wordInfos[i + 1].Text != "て"
                || wordInfos[i].PartOfSpeech is not (PartOfSpeech.Verb or PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                    or PartOfSpeech.Adverb))
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
            if (stripped.Length == 1 && stripped[0] != 'ー' && IsKatakanaTextChar(stripped[0])
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

            if (thief.PartOfSpeech is PartOfSpeech.Verb && CommonTeFormVerbs.Contains(thief.DictionaryForm))
            {
                result.Add(wordInfos[i]);
                continue;
            }

            // Noun-mora theft, pair shape: 繋|がりっ[Adverb]|て → 繋がり + って. The generic verb-reattach
            // below only accepts う-row verb endings, so a renyoukei/nominalised noun (繋がり) needs this.
            // Gated to a non-Verb thief (がりっ is an Adverb; genuine verb te-forms いっ/なっ are tagged Verb)
            // and to a hiragana stripped that reforms a real non-name JMDict word via lookback.
            if (thief.PartOfSpeech is not PartOfSpeech.Verb
                && stripped.Length >= 1
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
                        if (f.Process.Count == 1) { hasRealDeconjStep = true; break; }
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
                        var forms = deconj.Deconjugate(stripped);
                        shouldRepair = forms.Count > 0;
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

    /// <summary>
    /// Classical attributive き fused into the following noun by the lattice: 白|き尾 → 白き|尾.
    /// Fires when the previous token is a bare kanji noun whose stem+い is a JMDict i-adjective,
    /// the current OOV-ish noun starts with き, and the remainder resolves on its own. The
    /// re-attached Xき form deconjugates through the existing "classical attributive" rule.
    /// </summary>
    private List<WordInfo> RepairClassicalKiAdjective(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2 || HasNonNameCompoundLookup == null || HasCompoundLookup == null)
            return wordInfos;

        List<WordInfo>? result = null;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var current = wordInfos[i];
            var prev = i > 0 ? (result != null ? result[^1] : wordInfos[i - 1]) : null;

            if (prev == null
                || current.PartOfSpeech != PartOfSpeech.Noun
                || current.Text.Length < 2 || current.Text[0] != 'き'
                || prev.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.NaAdjective)
                || prev.Text.Length is < 1 or > 2
                || !prev.Text.All(c => c is >= '一' and <= '鿿')
                || HasCompoundLookup(current.Text)
                || !HasNonNameCompoundLookup(prev.Text + "い")
                || !HasNonNameCompoundLookup(current.Text[1..]))
            {
                result?.Add(current);
                continue;
            }

            result ??= [..wordInfos[..i]];
            result[^1] = new WordInfo(prev)
            {
                Text = prev.Text + 'き',
                DictionaryForm = prev.Text + "い",
                NormalizedForm = prev.Text + "い",
                PartOfSpeech = PartOfSpeech.IAdjective,
                EndOffset = prev.EndOffset >= 0 ? prev.EndOffset + 1 : -1
            };
            result.Add(new WordInfo(current)
            {
                Text = current.Text[1..],
                DictionaryForm = current.Text[1..],
                NormalizedForm = current.Text[1..],
                StartOffset = current.StartOffset >= 0 ? current.StartOffset + 1 : -1
            });
        }

        return result ?? wordInfos;
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
                    if (form.Tags.Count == 0 || form.Tags.Count > 5) continue;
                    var lastTag = form.Tags[^1];
                    if (!lastTag.StartsWith("v") && lastTag is not "adj-i" and not "adj-na")
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
