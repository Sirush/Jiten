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

            // Split かって misparsed as the adverb かつて (historical kana surface) → か + って.
            // かつて right after a verb is implausible; verb+か+って is the quotative question frame
            // (飲むかってこと "the question of whether to drink").
            if (i > 0 &&
                word is { Text: "かって", PartOfSpeech: PartOfSpeech.Adverb, Reading: "カツテ" } &&
                wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb)
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

    private static readonly string[] OovGrammarMarkers = ["って", "った", "のは", "のが", "のに", "ので", "んだ", "んで", "わけ", "ない"];

    private static readonly (string text, string reading, PartOfSpeech pos, PartOfSpeechSection sec)[] GrammarTokenTable =
    [
        ("って", "ッテ", PartOfSpeech.Particle, PartOfSpeechSection.AdverbialParticle),
        ("った", "ッタ", PartOfSpeech.Auxiliary, PartOfSpeechSection.None),
        ("わけ", "ワケ", PartOfSpeech.Noun, PartOfSpeechSection.CommonNoun),
        ("ない", "ナイ", PartOfSpeech.IAdjective, PartOfSpeechSection.PossibleDependant),
        ("から", "カラ", PartOfSpeech.Particle, PartOfSpeechSection.ConjunctionParticle),
        ("けど", "ケド", PartOfSpeech.Particle, PartOfSpeechSection.ConjunctionParticle),
        ("の", "ノ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("は", "ハ", PartOfSpeech.Particle, PartOfSpeechSection.BindingParticle),
        ("が", "ガ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("も", "モ", PartOfSpeech.Particle, PartOfSpeechSection.BindingParticle),
        ("で", "デ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("に", "ニ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("を", "ヲ", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("と", "ト", PartOfSpeech.Particle, PartOfSpeechSection.CaseMarkingParticle),
        ("か", "カ", PartOfSpeech.Particle, PartOfSpeechSection.AdverbialParticle),
        ("だ", "ダ", PartOfSpeech.Auxiliary, PartOfSpeechSection.None),
        ("な", "ナ", PartOfSpeech.Particle, PartOfSpeechSection.SentenceEndingParticle),
        ("ん", "ン", PartOfSpeech.Particle, PartOfSpeechSection.Juntaijoushi),
        ("た", "タ", PartOfSpeech.Auxiliary, PartOfSpeechSection.None),
    ];

    private static bool IsAllHiraganaSpan(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (c is < '぀' or > 'ゟ') return false;
        return text.Length > 0;
    }

    private static bool IsLikelyOovGarbage(WordInfo w)
    {
        if (w.Text.Length < 4) return false;
        if (w.PartOfSpeech is not (PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Interjection or PartOfSpeech.Filler))
            return false;
        if (!IsAllHiraganaSpan(w.Text.AsSpan())) return false;
        if (w.NormalizedForm != w.Text) return false;

        foreach (var marker in OovGrammarMarkers)
            if (w.Text.Contains(marker, StringComparison.Ordinal))
                return true;

        return false;
    }

    private static List<WordInfo> TokenizeGrammarRemainder(string text, int startOffset)
    {
        var tokens = new List<WordInfo>();
        int i = 0;
        while (i < text.Length)
        {
            bool matched = false;
            foreach (var (gram, reading, pos, sec) in GrammarTokenTable)
            {
                if (text.AsSpan(i).StartsWith(gram))
                {
                    tokens.Add(new WordInfo
                    {
                        Text = gram, DictionaryForm = gram, NormalizedForm = gram, Reading = reading,
                        PartOfSpeech = pos, PartOfSpeechSection1 = sec,
                        StartOffset = startOffset >= 0 ? startOffset + i : -1,
                        EndOffset = startOffset >= 0 ? startOffset + i + gram.Length : -1
                    });
                    i += gram.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched) break;
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
                    t.Text != "わけ");
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
