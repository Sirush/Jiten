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

            result.Add(current);
        }

        return changed ? result : wordInfos;
    }

    private List<WordInfo> RepairNTokenisation(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        // Phase 1: Split compound tokens that Sudachi incorrectly grouped
        var split = new List<WordInfo>(wordInfos.Count + 4);
        foreach (var word in wordInfos)
        {
            // Split tokens starting with ん (e.g., んだ → ん + だ)
            if (word.Text.Length > 1 && word.Text[0] == 'ん')
            {
                var remainder = word.Text[1..];
                if (NCompoundSuffixes.Contains(remainder) || NCompoundSuffixes.Any(s => remainder.StartsWith(s)))
                {
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
            if (word.Text.Length > 1 && word.Text[0] == 'だ' &&
                split.Count > 0 && (split[^1].Text == "ん" || split[^1].Text.EndsWith("ん")))
            {
                var remainder = word.Text[1..];
                if (DaCompoundSuffixes.Contains(remainder))
                {
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

            // Split そうだ → そう + だ (appearance/hearsay pattern should be split for combining logic)
            if (word is { Text: "そうだ", PartOfSpeech: PartOfSpeech.Adverb })
            {
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

            split.Add(word);
        }

        // Phase 2: Recombine verb stems with ん using deconjugator validation
        var result = new List<WordInfo>(split.Count);
        var deconj = Deconjugator.Instance;

        for (int i = 0; i < split.Count; i++)
        {
            var current = split[i];

            // Case: Token already ends with ん (e.g., 飲ん) and next is だ/で - combine as past/te-form
            // Skip na-adjectives (e.g., たくさん + で should NOT combine - で is the copula, not verb conjugation)
            // Skip suffixes (e.g., さん + だ should NOT combine - さん is honorific, だ is copula)
            if (current.Text.EndsWith("ん") && current.Text.Length > 1 && current.Text != "ん" &&
                !IsNaAdjectiveToken(current) &&
                current.PartOfSpeech != PartOfSpeech.Suffix &&
                !NormalizeToHiragana(current.DictionaryForm).EndsWith("ん") &&
                i + 1 < split.Count && split[i + 1].Text is "だ" or "で")
            {
                var candidateText = current.Text + split[i + 1].Text;
                if (IsNdaVerbForm(deconj.Deconjugate(NormalizeToHiragana(candidateText))))
                {
                    var candidateReading = KanaConverter.ToHiragana(current.Reading + split[i + 1].Reading);
                    result.Add(new WordInfo(current)
                    {
                        Text = candidateText, PartOfSpeech = PartOfSpeech.Verb,
                        NormalizedForm = candidateText, Reading = candidateReading,
                        EndOffset = split[i + 1].EndOffset
                    });
                    i++;
                    continue;
                }
            }

            // Case: Standalone ん - try to combine with preceding verb stem
            if (current.Text == "ん" && result.Count > 0)
            {
                bool combined = false;

                // Try んだ/んで pattern (past/te-form) - only for verb conjugation, not explanatory ん
                // Skip when ん is explanatory particle (DictionaryForm = "の" or "ん") or negative auxiliary (DictionaryForm = "ぬ")
                if (i + 1 < split.Count && split[i + 1].Text is "だ" or "で" &&
                    current.DictionaryForm is not "ぬ" and not "の" and not "ん")
                {
                    var suffix = "ん" + split[i + 1].Text;
                    var suffixReading = "ん" + split[i + 1].Reading;
                    if (TryCombineWithLookback(result, suffix, suffixReading, deconj, IsNdaVerbForm, out var combinedWord))
                    {
                        combinedWord!.EndOffset = split[i + 1].EndOffset;
                        result.Add(combinedWord);
                        combined = true;
                        i++;
                    }
                }

                // Fallback for ん classified as explanatory (from interjection split):
                // Sudachi sometimes misparsed verb stems as nouns (e.g., 喜んだだろうね → 喜(noun) + んだ)
                // Validate via dictionary lookup that noun + ぶ/む/ぬ/ぐ is a real verb
                if (!combined && i + 1 < split.Count && split[i + 1].Text is "だ" or "で" &&
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
                            var candidateText = prev.Text + "ん" + split[i + 1].Text;
                            var candidateReading = KanaConverter.ToHiragana(prev.Reading + "ん" + split[i + 1].Reading);
                            result.RemoveAt(result.Count - 1);
                            result.Add(new WordInfo(prev)
                            {
                                Text = candidateText, PartOfSpeech = PartOfSpeech.Verb,
                                NormalizedForm = candidateText, Reading = candidateReading,
                                EndOffset = split[i + 1].EndOffset,
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

                    result.Add(negativeWord);
                    combined = true;
                }

                if (!combined)
                    result.Add(current);
                continue;
            }

            result.Add(current);
        }

        return result;
    }

    private List<WordInfo> ProcessSpecialCases(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count == 0)
            return wordInfos;

        List<WordInfo> newList = new List<WordInfo>(wordInfos.Count);


        for (int i = 0; i < wordInfos.Count;)
        {
            WordInfo w1 = wordInfos[i];

            // Sudachi misclassifies katakana particle sequences as nouns (e.g. ノヨ → name 乃代).
            // Split into individual particles: ノ + ヨ, ノネ, ノサ, etc.
            if (w1.PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.NaAdjective && w1.Text == "ノヨ")
            {
                newList.Add(new WordInfo { Text = "ノ", DictionaryForm = "の", NormalizedForm = "の", PartOfSpeech = PartOfSpeech.Particle, Reading = "ノ", StartOffset = w1.StartOffset, EndOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1 });
                newList.Add(new WordInfo { Text = "ヨ", DictionaryForm = "よ", NormalizedForm = "よ", PartOfSpeech = PartOfSpeech.Particle, Reading = "ヨ", StartOffset = w1.StartOffset >= 0 ? w1.StartOffset + 1 : -1, EndOffset = w1.EndOffset });
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
            if (w1 is { Text: "着", PartOfSpeech: PartOfSpeech.Suffix, Reading: "ギ" }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1].PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary)
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
                    && IsAllHiraganaSpan(w1.Text.AsSpan())
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
                if (w1.PartOfSpeech == PartOfSpeech.Adverb &&
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
                    foreach (var sc in sc2Candidates)
                    {
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

    private static readonly HashSet<string> CaseParticles =
        ["に", "を", "が", "へ", "で", "と", "は", "も", "か", "から", "より", "まで", "の"];

    private static readonly HashSet<string> CommonTeFormVerbs =
        ["なる", "する", "やる", "いる", "ある", "くる", "できる", "おる", "みる", "しまう"];

    private List<WordInfo> RepairQuotativeTte(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        var deconj = Deconjugator.Instance;
        var result = new List<WordInfo>(wordInfos.Count + 2);
        bool changed = false;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            if (i + 1 >= wordInfos.Count
                || wordInfos[i].Text.Length < 2
                || wordInfos[i].Text[^1] != 'っ'
                || wordInfos[i + 1].Text != "て"
                || wordInfos[i].PartOfSpeech is not (PartOfSpeech.Verb or PartOfSpeech.Noun or PartOfSpeech.CommonNoun))
            {
                result.Add(wordInfos[i]);
                continue;
            }

            var thief = wordInfos[i];

            if (thief.PartOfSpeech is PartOfSpeech.Verb && CommonTeFormVerbs.Contains(thief.DictionaryForm))
            {
                result.Add(wordInfos[i]);
                continue;
            }

            var stripped = thief.Text[..^1];
            bool shouldRepair = false;
            bool mergeIntoPrev = false;

            if (stripped.Length == 1 && stripped[0] is >= 'ぁ' and <= 'ゖ')
            {
                if (result.Count > 0)
                {
                    var prev = result[^1];
                    if (prev.PartOfSpeech == PartOfSpeech.Particle && CaseParticles.Contains(prev.Text))
                    {
                        result.Add(wordInfos[i]);
                        continue;
                    }

                    var merged = prev.Text + stripped;
                    var forms = deconj.Deconjugate(merged);
                    bool hasShortChain = false;
                    foreach (var f in forms)
                    {
                        if (f.Process.Count <= 1) { hasShortChain = true; break; }
                    }
                    if (hasShortChain)
                    {
                        shouldRepair = true;
                        mergeIntoPrev = true;
                    }
                    else if (forms.Count == 0)
                    {
                        shouldRepair = true;
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
                    var forms = deconj.Deconjugate(stripped);
                    shouldRepair = forms.Count > 0;
                }
            }

            if (shouldRepair)
            {
                if (mergeIntoPrev && result.Count > 0)
                {
                    var prev = result[^1];
                    result[^1] = new WordInfo(prev)
                    {
                        Text = prev.Text + stripped,
                        EndOffset = thief.EndOffset >= 0 ? thief.EndOffset - 3 : -1
                    };
                }
                else
                {
                    result.Add(new WordInfo(thief) { Text = stripped, PartOfSpeech = PartOfSpeech.Verb });
                }

                result.Add(new WordInfo(wordInfos[i + 1])
                {
                    Text = "って",
                    DictionaryForm = "って",
                    NormalizedForm = "って",
                    PartOfSpeech = PartOfSpeech.Particle,
                    StartOffset = thief.EndOffset >= 0 ? thief.EndOffset - 3 : -1,
                    EndOffset = wordInfos[i + 1].EndOffset
                });
                i++;
                changed = true;
                continue;
            }

            result.Add(wordInfos[i]);
        }

        return changed ? result : wordInfos;
    }

    private List<WordInfo> RecombineHiraganaTokens(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2 || HasCompoundLookup == null)
            return wordInfos;

        var hasNonNameLookup = HasNonNameCompoundLookup ?? HasCompoundLookup;
        var hasPrioritizedLookup = HasPrioritizedNonNameCompoundLookup ?? hasNonNameLookup;

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
                    if (!IsAllHiraganaSpan(w.Text) ||
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

                string combinedText = spanLen switch
                {
                    2 => wordInfos[i].Text + wordInfos[i + 1].Text,
                    3 => wordInfos[i].Text + wordInfos[i + 1].Text + wordInfos[i + 2].Text,
                    _ => wordInfos[i].Text + wordInfos[i + 1].Text + wordInfos[i + 2].Text + wordInfos[i + 3].Text,
                };

                if (hasNonNameLookup(combinedText))
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
