using Jiten.Core;
using Jiten.Core.Data;
using WanaKanaShaapu;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    /// <summary>
    /// Splits compound verb tokens that Sudachi outputs as single tokens when they contain auxiliary verbs.
    /// For example: し終わっ (dict: し終わる) → し + 終わっ
    /// This is necessary because compound verbs like し終わる don't exist in JMDict, but their components do.
    /// </summary>
    private List<WordInfo> SplitCompoundAuxiliaryVerbs(List<WordInfo> wordInfos)
    {
        var result = new List<WordInfo>(wordInfos.Count + 4);
        bool changed = false;

        foreach (var word in wordInfos)
        {
            // Only process verb tokens with dictionary forms
            if (word.PartOfSpeech != PartOfSpeech.Verb ||
                string.IsNullOrEmpty(word.DictionaryForm) ||
                word.DictionaryForm.Length < 3)
            {
                result.Add(word);
                continue;
            }

            // Check if dictionary form ends with any auxiliary verb
            string? matchedAux = null;
            foreach (var aux in CompoundVerbSplitSuffixes)
            {
                if (word.DictionaryForm.EndsWith(aux) && word.DictionaryForm.Length > aux.Length)
                {
                    matchedAux = aux;
                    break;
                }
            }

            if (matchedAux == null)
            {
                result.Add(word);
                continue;
            }

            // If the full compound exists in JMDict, keep it intact so the form scoring
            // pipeline can use the Sudachi reading for disambiguation (e.g. 滲み出す
            // read as にじみだす vs しみだす — both share the kanji form but are different entries)
            if (HasCompoundLookup != null && HasCompoundLookup(word.DictionaryForm))
            {
                result.Add(word);
                continue;
            }

            // Calculate the main verb prefix length from dictionary form
            int mainVerbDictLen = word.DictionaryForm.Length - matchedAux.Length;
            string mainVerbDict = word.DictionaryForm[..mainVerbDictLen];

            // The surface form should have the same prefix length for the main verb
            // e.g., し終わっ → し (1 char) + 終わっ (3 chars)
            if (word.Text.Length <= mainVerbDictLen)
            {
                result.Add(word);
                continue;
            }

            string mainVerbSurface = word.Text[..mainVerbDictLen];
            string auxVerbSurface = word.Text[mainVerbDictLen..];

            // Verify the auxiliary surface starts with the auxiliary stem
            if (!AuxiliaryVerbStems.TryGetValue(matchedAux, out var auxStem) ||
                !auxVerbSurface.StartsWith(auxStem))
            {
                result.Add(word);
                continue;
            }

            // Create the main verb token
            var mainVerb = new WordInfo
                           {
                               Text = mainVerbSurface, DictionaryForm = mainVerbDict, NormalizedForm = mainVerbDict,
                               PartOfSpeech = PartOfSpeech.Verb, Reading = KanaConverter.ToHiragana(mainVerbSurface),
                               StartOffset = word.StartOffset,
                               EndOffset = word.StartOffset >= 0 ? word.StartOffset + mainVerbDictLen : -1
                           };

            // Create the auxiliary verb token
            var auxVerb = new WordInfo
                          {
                              Text = auxVerbSurface, DictionaryForm = matchedAux, NormalizedForm = matchedAux,
                              PartOfSpeech = PartOfSpeech.Verb, PartOfSpeechSection1 = PartOfSpeechSection.PossibleDependant,
                              Reading = KanaConverter.ToHiragana(auxVerbSurface),
                              StartOffset = word.StartOffset >= 0 ? word.StartOffset + mainVerbDictLen : -1,
                              EndOffset = word.EndOffset
                          };

            result.Add(mainVerb);
            result.Add(auxVerb);
            changed = true;
        }

        return changed ? result : wordInfos;
    }

    private static readonly Dictionary<char, char> RenyokeiToGodanBase = new()
    {
        ['き'] = 'く', ['ぎ'] = 'ぐ', ['し'] = 'す', ['ち'] = 'つ', ['に'] = 'ぬ',
        ['び'] = 'ぶ', ['み'] = 'む', ['り'] = 'る', ['い'] = 'う'
    };

    /// <summary>
    /// Decomposes productive compound verbs that are not in JMDict (驚き戸惑う, 縫い止める,
    /// 挑みかかる, 寝乱れる) into renyokei-stem verb + second verb, so both surface as vocabulary
    /// instead of the whole token being dropped as unresolvable at lookup time.
    /// Runs only when the full dictionary form has no JMDict entry; both parts must resolve.
    /// </summary>
    private List<WordInfo> SplitUnresolvableCompoundVerbs(List<WordInfo> wordInfos)
    {
        if (HasCompoundLookup == null || HasNonNameCompoundLookup == null)
            return wordInfos;

        List<WordInfo>? result = null;

        for (int idx = 0; idx < wordInfos.Count; idx++)
        {
            var word = wordInfos[idx];
            string dictForm = word.DictionaryForm;

            if (word.PartOfSpeech != PartOfSpeech.Verb ||
                string.IsNullOrEmpty(dictForm) || dictForm.Length < 4 ||
                word.Text.Length < 2 ||
                !word.Text.Any(c => c is >= '一' and <= '鿿'))
            {
                result?.Add(word);
                continue;
            }

            // Resolvable verbs are left for the normal lookup/deconjugation path.
            // The surface check covers renyokei compounds that exist as nouns (買い支え).
            if (HasCompoundLookup(dictForm) ||
                (word.Text != dictForm && HasCompoundLookup(word.Text)) ||
                (!string.IsNullOrEmpty(word.NormalizedForm) && word.NormalizedForm != dictForm &&
                 HasCompoundLookup(word.NormalizedForm)))
            {
                result?.Add(word);
                continue;
            }

            (string prefixBase, int splitAt)? split = null;

            // Prefer the longest stem (latest split point) so 縫い+止める beats 縫+い止める
            for (int p = Math.Min(dictForm.Length - 2, word.Text.Length); p >= 1 && split == null; p--)
            {
                var prefix = dictForm[..p];
                if (!word.Text.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var suffixDict = dictForm[p..];
                if (!HasNonNameCompoundLookup(suffixDict))
                    continue;

                // The stem must itself be a verb: ichidan (寝→寝る) or godan renyokei (驚き→驚く)
                var ichidan = prefix + 'る';
                if (HasNonNameCompoundLookup(ichidan))
                {
                    split = (ichidan, p);
                    break;
                }

                if (RenyokeiToGodanBase.TryGetValue(prefix[^1], out var baseEnd))
                {
                    var godan = prefix[..^1] + baseEnd;
                    if (HasNonNameCompoundLookup(godan))
                        split = (godan, p);
                }
            }

            if (split == null)
            {
                result?.Add(word);
                continue;
            }

            result ??= [..wordInfos[..idx]];

            var (stemBase, at) = split.Value;
            var stemSurface = word.Text[..at];
            var tailSurface = word.Text[at..];

            result.Add(new WordInfo
            {
                Text = stemSurface, DictionaryForm = stemBase, NormalizedForm = stemBase,
                PartOfSpeech = PartOfSpeech.Verb, Reading = KanaConverter.ToHiragana(stemSurface),
                StartOffset = word.StartOffset,
                EndOffset = word.StartOffset >= 0 ? word.StartOffset + at : -1
            });
            result.Add(new WordInfo
            {
                Text = tailSurface, DictionaryForm = word.DictionaryForm[at..], NormalizedForm = word.DictionaryForm[at..],
                PartOfSpeech = PartOfSpeech.Verb, Reading = KanaConverter.ToHiragana(tailSurface),
                StartOffset = word.StartOffset >= 0 ? word.StartOffset + at : -1,
                EndOffset = word.EndOffset
            });
        }

        return result ?? wordInfos;
    }

    // Productive adjective prefixes with their own JMDict entries (薄赤い → 薄+赤い).
    private static readonly string[] AdjectivePrefixes = ["真っ", "薄", "真", "ほの", "ど", "超"];

    /// <summary>
    /// Splits unresolvable prefixed i-adjectives into prefix + base adjective (薄赤い → 薄+赤い).
    /// Sudachi emits these as a single clean IAdjective token, but IAdjective is in the
    /// resegmentation skip list, so without this the whole token is dropped at lookup time.
    /// Resolvable compounds (薄暗い, 真っ白い) keep their own entries.
    /// </summary>
    private List<WordInfo> SplitUnresolvablePrefixedAdjectives(List<WordInfo> wordInfos)
    {
        if (HasCompoundLookup == null || HasNonNameCompoundLookup == null)
            return wordInfos;

        List<WordInfo>? result = null;

        for (int idx = 0; idx < wordInfos.Count; idx++)
        {
            var word = wordInfos[idx];
            string dictForm = word.DictionaryForm;

            string? prefix = null;
            if (word.PartOfSpeech == PartOfSpeech.IAdjective &&
                !string.IsNullOrEmpty(dictForm) && dictForm.Length >= 3 &&
                !HasCompoundLookup(dictForm) &&
                (dictForm == word.Text || !HasCompoundLookup(word.Text)))
            {
                foreach (var p in AdjectivePrefixes)
                {
                    if (dictForm.StartsWith(p, StringComparison.Ordinal)
                        && word.Text.StartsWith(p, StringComparison.Ordinal)
                        && dictForm.Length - p.Length >= 2
                        && HasNonNameCompoundLookup(dictForm[p.Length..]))
                    {
                        prefix = p;
                        break;
                    }
                }
            }

            if (prefix == null)
            {
                result?.Add(word);
                continue;
            }

            result ??= [..wordInfos[..idx]];

            result.Add(new WordInfo
            {
                Text = prefix, DictionaryForm = prefix, NormalizedForm = prefix,
                PartOfSpeech = PartOfSpeech.Prefix, Reading = KanaConverter.ToHiragana(prefix),
                StartOffset = word.StartOffset,
                EndOffset = word.StartOffset >= 0 ? word.StartOffset + prefix.Length : -1
            });
            result.Add(new WordInfo
            {
                Text = word.Text[prefix.Length..],
                DictionaryForm = word.DictionaryForm[prefix.Length..],
                NormalizedForm = word.DictionaryForm[prefix.Length..],
                PartOfSpeech = PartOfSpeech.IAdjective,
                Reading = KanaConverter.ToHiragana(word.Text[prefix.Length..]),
                StartOffset = word.StartOffset >= 0 ? word.StartOffset + prefix.Length : -1,
                EndOffset = word.EndOffset
            });
        }

        return result ?? wordInfos;
    }

    /// <summary>
    /// Decomposes noun+する merges whose noun is not a suru-noun (no vs tag) and whose merged
    /// surface is unresolvable: 大怪我して gets merged by the combine stages, but 大怪我する has
    /// no JMDict entry and 大怪我 [n] has no vs tag, so the deconjugation path can't rescue it
    /// and the whole token is dropped at lookup time. Splits back into noun + する-conjugation.
    /// Genuine suru-nouns (密着した — 密着 [n,vs]) keep the merge: deconjugation resolves them
    /// with the full chain. Runs after the combine stages that create these merges.
    /// </summary>
    private List<WordInfo> SplitUnresolvableSuruCompounds(List<WordInfo> wordInfos)
    {
        if (HasCompoundLookup == null || HasNonNameCompoundLookup == null || HasSuruVerbCompoundLookup == null)
            return wordInfos;

        List<WordInfo>? result = null;
        var deconj = Deconjugator.Instance;

        for (int idx = 0; idx < wordInfos.Count; idx++)
        {
            var word = wordInfos[idx];
            string text = word.Text;

            if (word.PartOfSpeech is not (PartOfSpeech.Verb or PartOfSpeech.Noun) ||
                text.Length < 3 ||
                !text.Any(c => c is >= '一' and <= '鿿'))
            {
                result?.Add(word);
                continue;
            }

            // Tokens whose surface has its own entry are left alone (買い支え-style).
            if (HasCompoundLookup(text))
            {
                result?.Add(word);
                continue;
            }

            int splitAt = -1;
            for (int p = text.Length - 1; p >= 2 && splitAt < 0; p--)
            {
                if (text[p] is not ('し' or 'さ' or 'す' or 'せ')) continue;

                var prefix = text[..p];
                if (!HasNonNameCompoundLookup(prefix)) continue;
                // Suru-nouns resolve as one token with the conjugation chain — keep them merged.
                if (HasSuruVerbCompoundLookup(prefix)) break;
                // A dictForm that resolves on its own and is not just the noun stem means the
                // deconjugation path can handle this token (思い出して → 思い出す): keep merged.
                if (word.DictionaryForm != prefix && word.DictionaryForm != text
                    && HasCompoundLookup(word.DictionaryForm)) break;

                var tail = text[p..];
                foreach (var f in deconj.Deconjugate(tail))
                {
                    if (f.Text == "する") { splitAt = p; break; }
                }
            }

            if (splitAt < 0)
            {
                result?.Add(word);
                continue;
            }

            result ??= [..wordInfos[..idx]];

            var nounSurface = text[..splitAt];
            var suruSurface = text[splitAt..];

            result.Add(new WordInfo
            {
                Text = nounSurface, DictionaryForm = nounSurface, NormalizedForm = nounSurface,
                PartOfSpeech = PartOfSpeech.Noun, Reading = KanaConverter.ToHiragana(nounSurface),
                StartOffset = word.StartOffset,
                EndOffset = word.StartOffset >= 0 ? word.StartOffset + splitAt : -1
            });
            result.Add(new WordInfo
            {
                Text = suruSurface, DictionaryForm = "する", NormalizedForm = "する",
                PartOfSpeech = PartOfSpeech.Verb, Reading = suruSurface,
                StartOffset = word.StartOffset >= 0 ? word.StartOffset + splitAt : -1,
                EndOffset = word.EndOffset
            });
        }

        return result ?? wordInfos;
    }

    /// <summary>
    /// Splits たん(suffix) + だ/です(auxiliary) into [prev+た] + ん + だ/です when the preceding token
    /// forms a valid verb past tense. Sudachi sometimes tokenizes たんだ as たん(suffix) + だ(auxiliary),
    /// e.g., イッ(noun) + たん(suffix) + だ(aux) instead of イッた + んだ.
    /// After this split, ProcessSpecialCases merges ん + だ → んだ (explanatory のだ).
    /// </summary>
    private List<WordInfo> SplitTanSuffix(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        var deconj = Deconjugator.Instance;
        var result = new List<WordInfo>(wordInfos.Count + 2);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (word is not { Text: "たん", PartOfSpeech: PartOfSpeech.Suffix }
                || i + 1 >= wordInfos.Count
                || wordInfos[i + 1] is not { PartOfSpeech: PartOfSpeech.Auxiliary, DictionaryForm: "だ" or "です" }
                || result.Count == 0)
            {
                result.Add(word);
                continue;
            }

            var prev = result[^1];
            bool shouldSplit = false;

            if (prev.Text[^1] is 'て' or 'で')
            {
                shouldSplit = true;
            }
            else
            {
                var candidateText = NormalizeToHiragana(prev.Text + "た");
                var forms = deconj.Deconjugate(candidateText);
                if (forms.Any(f => f.Tags.Any(t => t.StartsWith("v")) && f.Process.Any(p => p == "past")))
                    shouldSplit = true;
            }

            if (!shouldSplit)
            {
                result.Add(word);
                continue;
            }

            result[^1] = new WordInfo(prev)
            {
                Text = prev.Text + "た",
                EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
            };
            result.Add(new WordInfo
            {
                Text = "ん", DictionaryForm = "の", NormalizedForm = "ん", Reading = "ん",
                PartOfSpeech = PartOfSpeech.Particle, PartOfSpeechSection1 = PartOfSpeechSection.Juntaijoushi,
                StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1,
                EndOffset = word.EndOffset
            });
        }

        return result;
    }

    /// <summary>
    /// Splits the conjunctive particle たって/だって into た/だ (past auxiliary) + って (quotative particle)
    /// when it follows a verb in 連用形 (infinitive/stem form).
    /// Sudachi treats たって as a single 接続助詞 but it should be た + って for proper deconjugation.
    /// Examples: 出たって → 出 + た + って, 行ったって → 行っ + た + って
    /// </summary>
    private List<WordInfo> SplitTatteParticle(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        var result = new List<WordInfo>(wordInfos.Count + 2);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            // Split だな misparsed as 棚 (shelf) → だ (copula) + な (particle)
            if (word is { Text: "だな", PartOfSpeech: PartOfSpeech.Noun, NormalizedForm: "棚" })
            {
                result.Add(new WordInfo { Text = "だ", DictionaryForm = "だ", NormalizedForm = "だ", PartOfSpeech = PartOfSpeech.Auxiliary, Reading = "だ",
                    StartOffset = word.StartOffset, EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1 });
                result.Add(new WordInfo { Text = "な", DictionaryForm = "な", NormalizedForm = "な", PartOfSpeech = PartOfSpeech.Particle, PartOfSpeechSection1 = PartOfSpeechSection.SentenceEndingParticle, Reading = "な",
                    StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1, EndOffset = word.EndOffset });
                continue;
            }

            // Split かって misparsed as the adverb かつて (historical kana surface) → か + って. かつて right
            // after a clause-final predicate is implausible; predicate+か+って is the quotative question
            // frame (飲むかってこと, じゃないかって "(wondering) whether it isn't"). Gated on the predecessor
            // being a verb / i-adjective / auxiliary / predicative expression (じゃない) so a genuine かつて
            // (after a noun/topic, or clause-initial) is left alone.
            if (i > 0 &&
                word is { Text: "かって", PartOfSpeech: PartOfSpeech.Adverb, Reading: "カツテ" } &&
                wordInfos[i - 1].PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective
                    or PartOfSpeech.Auxiliary or PartOfSpeech.Expression)
            {
                result.Add(new WordInfo
                {
                    Text = "か", DictionaryForm = "か", NormalizedForm = "か",
                    PartOfSpeech = PartOfSpeech.Particle,
                    PartOfSpeechSection1 = PartOfSpeechSection.SentenceEndingParticle,
                    Reading = "カ",
                    StartOffset = word.StartOffset,
                    EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
                });
                result.Add(new WordInfo
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って",
                    PartOfSpeech = PartOfSpeech.Particle,
                    PartOfSpeechSection1 = PartOfSpeechSection.ConjunctionParticle,
                    Reading = "ッテ",
                    StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1,
                    EndOffset = word.EndOffset
                });
                continue;
            }

            // Split いたって misparsed as the adverb 至って ("extremely") → い (居る) + た (past) + って
            // (quotative). 至って never follows a て-form connective particle; right after て/で the surface
            // いたって is the ている-past + quotative frame (望んで+いた+って, 見ていたって). Gated on the
            // predecessor being a て/で 接続助詞 so a genuine 至って (clause-initial, after は/noun) is left alone.
            if (i > 0 &&
                word is { Text: "いたって", PartOfSpeech: PartOfSpeech.Adverb, Reading: "イタッテ" } &&
                wordInfos[i - 1] is { PartOfSpeech: PartOfSpeech.Particle, Text: "て" or "で" } prevTe &&
                prevTe.HasPartOfSpeechSection(PartOfSpeechSection.ConjunctionParticle))
            {
                result.Add(new WordInfo
                {
                    Text = "い", DictionaryForm = "いる", NormalizedForm = "居る",
                    PartOfSpeech = PartOfSpeech.Verb, Reading = "イ",
                    StartOffset = word.StartOffset,
                    EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
                });
                result.Add(new WordInfo
                {
                    Text = "た", DictionaryForm = "た", NormalizedForm = "た",
                    PartOfSpeech = PartOfSpeech.Auxiliary, Reading = "タ",
                    StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1,
                    EndOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1
                });
                result.Add(new WordInfo
                {
                    Text = "って", DictionaryForm = "って", NormalizedForm = "って",
                    PartOfSpeech = PartOfSpeech.Particle,
                    PartOfSpeechSection1 = PartOfSpeechSection.ConjunctionParticle,
                    Reading = "ッテ",
                    StartOffset = word.StartOffset >= 0 ? word.StartOffset + 2 : -1,
                    EndOffset = word.EndOffset
                });
                continue;
            }

            // Check if this is たって/だって as a conjunctive particle following a verb
            if (i > 0 &&
                word.PartOfSpeech == PartOfSpeech.Particle &&
                word.HasPartOfSpeechSection(PartOfSpeechSection.ConjunctionParticle) &&
                word.Text is "たって" or "だって")
            {
                var prev = wordInfos[i - 1];

                // Only split if preceded by verb/adjective in a stem form (連用形 or similar)
                if (prev.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary)
                {
                    // Determine which past marker to use
                    string pastMarker = word.Text == "たって" ? "た" : "だ";

                    // Add the past auxiliary verb (た/だ)
                    result.Add(new WordInfo
                    {
                        Text = pastMarker,
                        DictionaryForm = pastMarker,
                        NormalizedForm = pastMarker,
                        PartOfSpeech = PartOfSpeech.Auxiliary,
                        Reading = pastMarker,
                        StartOffset = word.StartOffset,
                        EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
                    });

                    // Add the quotative particle (って)
                    result.Add(new WordInfo
                    {
                        Text = "って",
                        DictionaryForm = "って",
                        NormalizedForm = "って",
                        PartOfSpeech = PartOfSpeech.Particle,
                        PartOfSpeechSection1 = PartOfSpeechSection.ConjunctionParticle,
                        Reading = "って",
                        StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1,
                        EndOffset = word.EndOffset
                    });

                    continue;
                }
            }

            result.Add(word);
        }

        return result;
    }

    /// <summary>
    /// Splits たわけ (misanalysed as 戯け noun or たわける verb) into た (past auxiliary) + わけ (noun)
    /// when preceded by a verb stem, auxiliary, or っ (geminate mark).
    /// Sudachi frequently fuses た+わけ into たわけ after verb stems,
    /// e.g., してたわけ → してた+わけ, あるったわけ → あった+わけ.
    /// Legitimate uses of たわけ (戯け "fool") follow nouns, prefixes, or adnominals and are left intact.
    /// </summary>
    private static List<WordInfo> SplitTawakeNoun(List<WordInfo> wordInfos)
    {
        var result = new List<WordInfo>(wordInfos.Count + 2);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (word.Text == "たわけ" && word.DictionaryForm is "たわけ" or "たわける" && i > 0)
            {
                var prev = wordInfos[i - 1];
                bool afterVerbContext = prev.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.Auxiliary or PartOfSpeech.IAdjective or PartOfSpeech.Particle
                    || (prev.PartOfSpeech == PartOfSpeech.SupplementarySymbol && prev.Text == "っ")
                    || prev.PartOfSpeech == PartOfSpeech.Adverb;

                if (afterVerbContext)
                {
                    result.Add(new WordInfo
                    {
                        Text = "た", DictionaryForm = "た", NormalizedForm = "た",
                        PartOfSpeech = PartOfSpeech.Auxiliary, Reading = "た",
                        StartOffset = word.StartOffset,
                        EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
                    });
                    result.Add(new WordInfo
                    {
                        Text = "わけ", DictionaryForm = "わけ", NormalizedForm = "わけ",
                        PartOfSpeech = PartOfSpeech.Noun, Reading = "わけ",
                        StartOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1,
                        EndOffset = word.EndOffset
                    });
                    continue;
                }
            }

            result.Add(word);
        }

        return result;
    }

    /// <summary>
    /// Sudachi lexicalises どうして (adverb 如何して) even when it is the してた (している) contraction:
    /// どうしてた = どう + して + (い)た. The "why/how" adverb cannot take a directly-following past た,
    /// so どうして immediately before た is re-cut to どう (adverb) + し (する) + て (てる), which the
    /// inflection combiner reforms into してた. Gated on the directly-following た so genuine どうして
    /// (どうして来たの, どうしてですか) is untouched.
    /// </summary>
    private static List<WordInfo> SplitDoushiteContraction(List<WordInfo> wordInfos)
    {
        var result = new List<WordInfo>(wordInfos.Count + 2);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (word is { Text: "どうして", PartOfSpeech: PartOfSpeech.Adverb }
                && i + 1 < wordInfos.Count
                && wordInfos[i + 1] is { Text: "た", PartOfSpeech: PartOfSpeech.Auxiliary }
                && word.StartOffset >= 0)
            {
                int s = word.StartOffset;
                result.Add(new WordInfo
                {
                    Text = "どう", DictionaryForm = "どう", NormalizedForm = "どう",
                    PartOfSpeech = PartOfSpeech.Adverb, Reading = "ドウ",
                    StartOffset = s, EndOffset = s + 2
                });
                result.Add(new WordInfo
                {
                    Text = "し", DictionaryForm = "する", NormalizedForm = "為る",
                    PartOfSpeech = PartOfSpeech.Verb, Reading = "シ",
                    StartOffset = s + 2, EndOffset = s + 3
                });
                result.Add(new WordInfo
                {
                    Text = "て", DictionaryForm = "てる", NormalizedForm = "てる",
                    PartOfSpeech = PartOfSpeech.Auxiliary, Reading = "テ",
                    StartOffset = s + 3, EndOffset = s + 4
                });
                continue;
            }

            result.Add(word);
        }

        return result;
    }

    /// <summary>
    /// V-連用形 + も + する emphatic negative (かすりもしない, 見もしません): after a verb stem Sudachi
    /// often lexicalises the も + し sequence as the adverb もし (若し "if"). The conditional adverb
    /// cannot follow a 連用形, so re-cut もし → も (binding particle) + し (する), letting the downstream
    /// combiner reform しません/しませんでした. Gated on a preceding inflected verb and a following
    /// する-negation/polite continuation so a genuine もし is left intact.
    /// </summary>
    private static List<WordInfo> SplitEmphaticMoSuru(List<WordInfo> wordInfos)
    {
        var result = new List<WordInfo>(wordInfos.Count + 1);

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (word is { Text: "もし", PartOfSpeech: PartOfSpeech.Adverb }
                && i > 0
                && wordInfos[i - 1] is { PartOfSpeech: PartOfSpeech.Verb } prev
                && prev.Text != prev.DictionaryForm
                && i + 1 < wordInfos.Count
                && IsSuruNegationOrPoliteContinuation(wordInfos[i + 1].Text)
                && word.StartOffset >= 0)
            {
                int s = word.StartOffset;
                result.Add(new WordInfo
                {
                    Text = "も", DictionaryForm = "も", NormalizedForm = "も",
                    PartOfSpeech = PartOfSpeech.Particle,
                    PartOfSpeechSection1 = PartOfSpeechSection.BindingParticle,
                    Reading = "モ", StartOffset = s, EndOffset = s + 1
                });
                // Bare し (する 連用形). Pin the word id: free scoring mismatches the surface homograph
                // 四 (し "four"), and the parser keeps しません/ませんでした as separate suffix tokens anyway.
                result.Add(new WordInfo
                {
                    Text = "し", DictionaryForm = "する", NormalizedForm = "為る",
                    PartOfSpeech = PartOfSpeech.Verb, Reading = "シ",
                    PreMatchedWordId = 1157170,
                    StartOffset = s + 1, EndOffset = s + 2
                });
                continue;
            }

            result.Add(word);
        }

        return result;
    }

    private static bool IsSuruNegationOrPoliteContinuation(string text) =>
        text.StartsWith("ませ", StringComparison.Ordinal)
        || text.StartsWith("まし", StringComparison.Ordinal)
        || text.StartsWith("ます", StringComparison.Ordinal)
        || text.StartsWith("ない", StringComparison.Ordinal)
        || text.StartsWith("なかっ", StringComparison.Ordinal)
        || text.StartsWith("なく", StringComparison.Ordinal)
        || text.StartsWith("ねえ", StringComparison.Ordinal)
        || text.StartsWith("ねぇ", StringComparison.Ordinal);

    private static readonly string[] OovGrammarMarkers = ["って", "った", "のは", "のが", "のに", "ので", "んだ", "んで", "わけ", "ない"];

    private static readonly (string text, string reading, PartOfSpeech pos, PartOfSpeechSection sec)[] GrammarTokenTable =
    [
        ("って", "ッテ", PartOfSpeech.Particle, PartOfSpeechSection.AdverbialParticle),
        ("った", "ッタ", PartOfSpeech.Auxiliary, PartOfSpeechSection.None),
        ("わけ", "ワケ", PartOfSpeech.Noun, PartOfSpeechSection.CommonNoun),
        ("こと", "コト", PartOfSpeech.Noun, PartOfSpeechSection.CommonNoun),
        // こそあど demonstratives tokenised as Pronoun (not CommonNoun) so a leftover これ/それ/…
        // after a quotative って doesn't trip the hasLeftoverNoun guard that aborts the OOV split
        // (考える|って|これ from the るってこれ blob).
        ("これ", "コレ", PartOfSpeech.Pronoun, PartOfSpeechSection.Pronoun),
        ("それ", "ソレ", PartOfSpeech.Pronoun, PartOfSpeechSection.Pronoun),
        ("あれ", "アレ", PartOfSpeech.Pronoun, PartOfSpeechSection.Pronoun),
        ("どれ", "ドレ", PartOfSpeech.Pronoun, PartOfSpeechSection.Pronoun),
        ("ここ", "ココ", PartOfSpeech.Pronoun, PartOfSpeechSection.Pronoun),
        ("そこ", "ソコ", PartOfSpeech.Pronoun, PartOfSpeechSection.Pronoun),
        ("どこ", "ドコ", PartOfSpeech.Pronoun, PartOfSpeechSection.Pronoun),
        ("ない", "ナイ", PartOfSpeech.IAdjective, PartOfSpeechSection.PossibleDependant),
        ("から", "カラ", PartOfSpeech.Particle, PartOfSpeechSection.ConjunctionParticle),
        ("けど", "ケド", PartOfSpeech.Particle, PartOfSpeechSection.ConjunctionParticle),
        ("だけ", "ダケ", PartOfSpeech.Particle, PartOfSpeechSection.AdverbialParticle),
        ("でも", "デモ", PartOfSpeech.Particle, PartOfSpeechSection.AdverbialParticle),
        ("の", "ノ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("は", "ハ", PartOfSpeech.Particle, PartOfSpeechSection.BindingParticle),
        ("が", "ガ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("も", "モ", PartOfSpeech.Particle, PartOfSpeechSection.BindingParticle),
        ("で", "デ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("に", "ニ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("を", "ヲ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("と", "ト", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("か", "カ", PartOfSpeech.Particle, PartOfSpeechSection.AdverbialParticle),
        // だろう/だろ before だ — the longest-match loop keeps the tentative copula whole so the
        // trailing ろ/ろう is not stranded as a leftover noun that aborts the split (…ってことだろ).
        ("だろう", "ダロウ", PartOfSpeech.Auxiliary, PartOfSpeechSection.None),
        ("だろ", "ダロ", PartOfSpeech.Auxiliary, PartOfSpeechSection.None),
        ("だ", "ダ", PartOfSpeech.Auxiliary, PartOfSpeechSection.None),
        ("な", "ナ", PartOfSpeech.Particle, PartOfSpeechSection.SentenceEndingParticle),
        ("ね", "ネ", PartOfSpeech.Particle, PartOfSpeechSection.SentenceEndingParticle),
        ("よ", "ヨ", PartOfSpeech.Particle, PartOfSpeechSection.SentenceEndingParticle),
        ("さ", "サ", PartOfSpeech.Particle, PartOfSpeechSection.SentenceEndingParticle),
        ("わ", "ワ", PartOfSpeech.Particle, PartOfSpeechSection.SentenceEndingParticle),
        ("ん", "ン", PartOfSpeech.Particle, PartOfSpeechSection.Juntaijoushi),
        ("た", "タ", PartOfSpeech.Auxiliary, PartOfSpeechSection.None),
    ];

    private static bool DeconjugatesToVerb(string hiragana)
    {
        foreach (var f in Deconjugator.Instance.Deconjugate(hiragana))
            foreach (var t in f.Tags)
                if (t.StartsWith("v", StringComparison.Ordinal))
                    return true;
        return false;
    }

    private static bool IsLikelyOovGarbage(WordInfo w)
    {
        // Length 3 admits the minimal stem-mora-theft blob (る+って → なる|って), where Sudachi
        // strands the verb's final mora onto a bare quotative って. The split is still tightly
        // gated downstream (prev+prefix must deconjugate to a real verb/adjective).
        if (w.Text.Length < 3) return false;
        if (w.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Interjection or PartOfSpeech.Filler))
            return false;
        if (!JapaneseTextHelper.IsAllHiragana(w.Text)) return false;
        if (w.NormalizedForm != w.Text) return false;

        foreach (var marker in OovGrammarMarkers)
            if (w.Text.Contains(marker, StringComparison.Ordinal))
                return true;

        return false;
    }

    private List<WordInfo> TokenizeGrammarRemainder(string text, int startOffset)
    {
        var tokens = new List<WordInfo>();
        int i = 0;
        while (i < text.Length)
        {
            int markerLen = 0;
            (string reading, PartOfSpeech pos, PartOfSpeechSection sec) marker = default;
            foreach (var (gram, reading, pos, sec) in GrammarTokenTable)
                if (gram.Length > markerLen && text.AsSpan(i).StartsWith(gram))
                    (markerLen, marker) = (gram.Length, (reading, pos, sec));

            // Pull a trailing content VERB out of the cluster (って+いう, ってのは+わかる) instead of
            // dumping it as one OOV noun that aborts the whole split. Restricted to dictionary-form
            // verbs on purpose: matching arbitrary short tails mis-cuts grammatical clusters
            // (のはだな → の|肌|な, ってんだ → って|んだ). The candidate must end in a う-row kana — the
            // deconjugator alone over-generates (んだ/はだ both "deconjugate" to verb pasts).
            int verbLen = 0;
            if (HasNonNameCompoundLookup != null)
                for (int len = Math.Min(text.Length - i, 6); len > markerLen && len >= 2; len--)
                {
                    var cand = text.Substring(i, len);
                    if (cand[^1] is not ('う' or 'く' or 'ぐ' or 'す' or 'つ' or 'ぬ' or 'ぶ' or 'む' or 'る')) continue;
                    if (!HasNonNameCompoundLookup(cand)) continue;
                    if (!DeconjugatesToVerb(cand)) continue;
                    verbLen = len;
                    break;
                }

            if (verbLen > 0)
            {
                var w = text.Substring(i, verbLen);
                tokens.Add(new WordInfo
                {
                    Text = w, DictionaryForm = w, NormalizedForm = w,
                    Reading = WanaKanaShaapu.WanaKana.ToKatakana(NormalizeToHiragana(w)),
                    PartOfSpeech = PartOfSpeech.Verb,
                    StartOffset = startOffset >= 0 ? startOffset + i : -1,
                    EndOffset = startOffset >= 0 ? startOffset + i + verbLen : -1
                });
                i += verbLen;
                continue;
            }

            if (markerLen == 0) break;

            var gramText = text.Substring(i, markerLen);
            tokens.Add(new WordInfo
            {
                Text = gramText, DictionaryForm = gramText, NormalizedForm = gramText, Reading = marker.reading,
                PartOfSpeech = marker.pos, PartOfSpeechSection1 = marker.sec,
                StartOffset = startOffset >= 0 ? startOffset + i : -1,
                EndOffset = startOffset >= 0 ? startOffset + i + markerLen : -1
            });
            i += markerLen;
        }

        if (i < text.Length)
        {
            var leftover = text[i..];
            tokens.Add(new WordInfo
            {
                Text = leftover, DictionaryForm = leftover, NormalizedForm = leftover, Reading = leftover,
                PartOfSpeech = PartOfSpeech.Noun, PartOfSpeechSection1 = PartOfSpeechSection.CommonNoun,
                StartOffset = startOffset >= 0 ? startOffset + i : -1,
                EndOffset = startOffset >= 0 ? startOffset + text.Length : -1
            });
        }

        return tokens;
    }

    private List<WordInfo> SplitOovGarbageTokens(List<WordInfo> wordInfos)
    {
        if (wordInfos.Count < 2) return wordInfos;

        var deconj = Deconjugator.Instance;
        var result = new List<WordInfo>(wordInfos.Count + 8);
        bool changed = false;

        for (int i = 0; i < wordInfos.Count; i++)
        {
            var word = wordInfos[i];

            if (!IsLikelyOovGarbage(word) || result.Count == 0)
            {
                result.Add(word);
                continue;
            }

            if (HasCompoundLookup != null && HasCompoundLookup(word.Text))
            {
                result.Add(word);
                continue;
            }

            var prev = result[^1];
            bool repaired = false;

            // ている-contraction shredded by a following quotative って: [verb-stem][て/で particle][るって…blob].
            // Reform the stolen る as the て-form auxiliary てる/でる on the preceding verb stem (見られ|て|るって
            // → 見られ + てる(Aux) + って), so CombineInflections folds 見られてる instead of leaving a standalone
            // content verb 照る (1350860). Gated on prev being a bare て/で particle after a Verb/IAdjective.
            if (word.Text.StartsWith("るって", StringComparison.Ordinal)
                && prev is { PartOfSpeech: PartOfSpeech.Particle, Text: "て" or "で" }
                && result.Count >= 2
                && result[^2].PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary)
            {
                int boundary = word.StartOffset >= 0 ? word.StartOffset + 1 : -1;
                result[^1] = new WordInfo(prev)
                {
                    Text = prev.Text + "る",
                    DictionaryForm = prev.Text + "る",
                    NormalizedForm = prev.Text + "る",
                    Reading = prev.Reading + "ル",
                    PartOfSpeech = PartOfSpeech.Auxiliary,
                    EndOffset = boundary
                };
                result.AddRange(TokenizeGrammarRemainder(word.Text[1..], boundary));
                changed = true;
                continue;
            }

            // When prev is a bound Suffix (Sudachi split a verb's kanji stem, e.g. 頑|張), reattaching the
            // stolen mora to the suffix alone strands the leading kanji (張れ, 頑 orphaned). If the FULL run
            // prev2+prev+leadingMora is a real JMDict compound verb (頑張れ→頑張る 1217700), reform it across
            // both tokens and split off the trailing って/grammar. Gated on a real lookup to avoid over-merge.
            if (result.Count >= 2 && result[^1].PartOfSpeech == PartOfSpeech.Suffix && word.Text.Length >= 2)
            {
                var mora = word.Text[..1];
                var runHira = NormalizeToHiragana(result[^2].Text + result[^1].Text + mora);
                string? runDict = null;
                foreach (var f in deconj.Deconjugate(runHira))
                    if (f.Tags.Any(t => t.StartsWith("v", StringComparison.Ordinal))
                        && HasCompoundLookup?.Invoke(f.Text) == true) { runDict = f.Text; break; }
                if (runDict != null)
                {
                    var gt = TokenizeGrammarRemainder(word.Text[1..], word.StartOffset >= 0 ? word.StartOffset + 1 : -1);
                    bool leftoverNoun = gt.Any(t => t.PartOfSpeech == PartOfSpeech.Noun
                        && t.PartOfSpeechSection1 == PartOfSpeechSection.CommonNoun && t.Text is not ("わけ" or "こと"));
                    if (gt.Count > 0 && !leftoverNoun)
                    {
                        var head = result[^2];
                        var verbText = result[^2].Text + result[^1].Text + mora;
                        result.RemoveRange(result.Count - 2, 2);
                        result.Add(new WordInfo(head)
                        {
                            Text = verbText, DictionaryForm = runDict, NormalizedForm = runDict,
                            Reading = WanaKanaShaapu.WanaKana.ToKatakana(NormalizeToHiragana(verbText)),
                            PartOfSpeech = PartOfSpeech.Verb, PartOfSpeechSection1 = PartOfSpeechSection.Common,
                            EndOffset = word.StartOffset >= 0 ? word.StartOffset + 1 : -1
                        });
                        result.AddRange(gt);
                        changed = true;
                        continue;
                    }
                }
            }

            int maxPrefix = Math.Min(3, word.Text.Length - 2);
            for (int prefixLen = 1; prefixLen <= maxPrefix; prefixLen++)
            {
                var prefix = word.Text[..prefixLen];
                var candidate = prev.Text + prefix;
                var hiragana = NormalizeToHiragana(candidate);
                var forms = deconj.Deconjugate(hiragana);

                string? dictForm = null;
                PartOfSpeech repairedPos = PartOfSpeech.Verb;
                bool isValid = false;
                bool foundVerb = false;

                foreach (var f in forms)
                {
                    foreach (var t in f.Tags)
                    {
                        if (t.StartsWith("v", StringComparison.Ordinal))
                        {
                            isValid = true;
                            foundVerb = true;
                            dictForm ??= f.Text;
                            break;
                        }
                        if (t.StartsWith("adj", StringComparison.Ordinal))
                        {
                            isValid = true;
                            dictForm ??= f.Text;
                        }
                    }
                    if (foundVerb) break;
                }

                if (isValid && !foundVerb) repairedPos = PartOfSpeech.IAdjective;

                if (!isValid) continue;

                var remainder = word.Text[prefixLen..];
                var grammarTokens = TokenizeGrammarRemainder(remainder, word.StartOffset >= 0 ? word.StartOffset + prefixLen : -1);

                if (grammarTokens.Count == 0) continue;
                bool hasLeftoverNoun = grammarTokens.Any(t =>
                    t.PartOfSpeech == PartOfSpeech.Noun && t.PartOfSpeechSection1 == PartOfSpeechSection.CommonNoun &&
                    t.Text is not ("わけ" or "こと"));
                if (hasLeftoverNoun) continue;

                result[^1] = new WordInfo
                {
                    Text = candidate,
                    DictionaryForm = dictForm ?? hiragana,
                    NormalizedForm = dictForm ?? hiragana,
                    Reading = WanaKanaShaapu.WanaKana.ToKatakana(hiragana),
                    PartOfSpeech = repairedPos,
                    PartOfSpeechSection1 = PartOfSpeechSection.Common,
                    StartOffset = prev.StartOffset,
                    EndOffset = word.StartOffset >= 0 ? word.StartOffset + prefixLen : -1
                };

                result.AddRange(grammarTokens);
                repaired = true;
                changed = true;
                break;
            }

            if (!repaired)
                result.Add(word);
        }

        return changed ? result : wordInfos;
    }
}
