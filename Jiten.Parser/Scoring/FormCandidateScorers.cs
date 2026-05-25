using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using WanaKanaShaapu;

namespace Jiten.Parser.Scoring;

internal static class FormCandidateScorer
{
    public static FormScoreTrace Score(
        FormCandidate candidate,
        FormScoringContext context,
        IReadOnlySet<string> archaicPosTypes)
    {
        int wordScore = WordPriorityScorer.Score(candidate, context.IsNameContext, context.IsArchaicSentence, archaicPosTypes, context.IsSentenceInitial, context.IsSentenceFinal);
        int entryPriorityScore = EntryPriorityScorer.Score(candidate);
        int formPriorityScore = FormPriorityScorer.Score(candidate, context.IsKanaSurface);
        int formFlagScore = FormFlagScorer.Score(candidate, context);

        int surfaceMatchScore = SurfaceScorer.Score(candidate, context);
        surfaceMatchScore += LemmaScorer.Score(candidate, context, surfaceMatchScore);

        bool conjugatedIdentityPenaltyApplied =
            PenaltyScorer.ApplyConjugatedIdentityPenalty(candidate, context, ref surfaceMatchScore);
        bool expressionConflictPenaltyApplied =
            PenaltyScorer.ApplyExpressionConflictPenalty(candidate, context, ref surfaceMatchScore);

        int scriptScore = ScriptScorer.Score(candidate, context);
        int readingMatchScore = ReadingScorer.Score(candidate, context,
            conjugatedIdentityPenaltyApplied || expressionConflictPenaltyApplied,
            archaicPosTypes);

        if (readingMatchScore < 0 && surfaceMatchScore >= 300)
            surfaceMatchScore = (int)(surfaceMatchScore * 0.3);

        int posAffinityScore = PosAffinityScorer.Score(candidate, context);

        int rubyPriorsScore = RubyPriorsScorer.Score(candidate, context);

        return new FormScoreTrace(
                                  wordScore,
                                  entryPriorityScore,
                                  formPriorityScore,
                                  formFlagScore,
                                  surfaceMatchScore,
                                  scriptScore,
                                  readingMatchScore,
                                  posAffinityScore,
                                  conjugatedIdentityPenaltyApplied || expressionConflictPenaltyApplied,
                                  rubyPriorsScore);
    }
}

internal static class WordPriorityScorer
{
    private static readonly HashSet<string> SentenceFinalParticleSurfaces =
        new() { "ね", "よ", "ぞ", "わ", "な", "さ", "か", "の", "かな", "かしら", "よね", "わよ", "わね", "のよ", "のね" };

    public static int Score(FormCandidate candidate, bool isNameContext, bool isArchaicSentence, IReadOnlySet<string> archaicPosTypes, bool isSentenceInitial = false, bool isSentenceFinal = false)
    {
        var word = candidate.Word;
        int wordScore = 0;

        if (word.Priorities?.Contains("jiten") == true)
            wordScore += 100;

        if (!isNameContext && word.CachedPOS.Contains(PartOfSpeech.Name))
            wordScore -= 50;

        if (word.IsFullyArchaic)
        {
            bool hasFrequencyMarker = KanaScoringHelpers.HasFrequencyMarker(word.Priorities);
            if (!hasFrequencyMarker)
            {
                // Archaic pronouns (汝 なんじ, 我 われ, etc.) appear regularly in literary/fantasy
                // prose without the sentence being fully classical. Softer penalty for pronouns.
                bool isPronoun = word.CachedPOS.Contains(PartOfSpeech.Pronoun);
                wordScore -= isArchaicSentence ? 50 : (isPronoun ? 100 : 350);
            }
        }

        // Only penalise when the word has NO non-archaic primary POS.
        // E.g. 無し has adj-ku (archaic) BUT also n — modern usage still valid, skip penalty.
        // "suf"/"pref" are sub-categorisations, not primary word classes — a word can be
        // simultaneously archaic (v2a-s) and a suffix, so they must not exempt it from the penalty.
        var readingPos = candidate.CachedReadingPos;
        IEnumerable<string> posToCheck = readingPos.Count > 0 ? readingPos : word.PartsOfSpeech;
        if (posToCheck.Any(archaicPosTypes.Contains)
            && !posToCheck.Any(p => p is "n" or "n-adv" or "n-t" or "n-pref" or "n-suf"
                                     or "v1" or "v1-s"
                                     or "v5a" or "v5b" or "v5g" or "v5k" or "v5k-s" or "v5m"
                                     or "v5n" or "v5r" or "v5r-i" or "v5s" or "v5t"
                                     or "v5u" or "v5u-s" or "v5uru"
                                     or "vs" or "vs-c" or "vs-i" or "vs-s" or "vk" or "vz"
                                     or "adj-i" or "adj-ix" or "adj-na" or "adj-no" or "adj-pn"
                                     or "adv" or "adv-to" or "cop" or "aux" or "aux-v" or "aux-adj"
                                     or "prt" or "int" or "exp" or "pref" or "ctr"
                                     or "on-mim" or "pn" or "conj"))
            wordScore -= isArchaicSentence ? 15 : 75;

        if (word.PartsOfSpeech.Any(p => p is "on-mim"))
            wordScore += 10;

        // Adverbs commonly start clauses; mild boost when sentence-initial.
        if (isSentenceInitial && word.PartsOfSpeech.Any(p => p is "adv" or "adv-to"))
            wordScore += 10;

        // Sentence-final particles (ね/よ/ぞ/わ/な/さ/か/の …) get a bonus only at true
        // end-of-sentence, matching Ichiran's :final flag in gen-score (dict.lisp:1120).
        // Resolves homograph conflicts like 〜な (na-adj ending) vs. final 〜な (particle).
        if (isSentenceFinal
            && word.PartsOfSpeech.Any(p => p is "prt")
            && SentenceFinalParticleSurfaces.Contains(candidate.FormTextHiragana))
            wordScore += 25;

        // Unclass entries (JMnedict names with no category) are last-resort matches.
        // Penalise them when not in a name context so proper words score higher.
        if (!isNameContext && word.PartsOfSpeech.All(p => p is "unclass"))
            wordScore -= 40;

        // Pure counters (words whose only real POS is "ctr") almost always follow a number.
        // Penalise them so noun/adjective homophones win in non-numeric contexts
        // (e.g. 色/ショク counter vs 色/いろ noun).
        if (word.PartsOfSpeech.All(p => p is "ctr"))
            wordScore -= 10;

        // Shorter deconjugation chains are preferred for morphological plausibility.
        int chainCount = candidate.DeconjForm?.Process.Count ?? 0;
        if (chainCount <= 2)
            wordScore += 8;
        else
            wordScore -= 8 * (chainCount - 2);

        return wordScore;
    }
}

internal static class EntryPriorityScorer
{
    public static int Score(FormCandidate candidate)
    {
        var wordPri = candidate.Word.Priorities ?? [];
        int entryPriorityScore = 0;

        if (wordPri.Contains("ichi1")) entryPriorityScore += 20;
        if (wordPri.Contains("ichi2")) entryPriorityScore += 10;
        if (wordPri.Contains("news1")) entryPriorityScore += 15;
        if (wordPri.Contains("news2")) entryPriorityScore += 10;
        if (wordPri.Contains("gai1")) entryPriorityScore += 15;
        if (wordPri.Contains("gai2")) entryPriorityScore += 10;

        var wnf = wordPri.FirstOrDefault(p => p.StartsWith("nf"));
        if (wnf is { Length: > 2 } && int.TryParse(wnf[2..], out var wnfRank))
        {
            entryPriorityScore += Math.Max(0, 5 - (int)Math.Round(wnfRank / 10f));
            if (wnfRank <= 5)
                entryPriorityScore += 6 - wnfRank;
        }

        if (entryPriorityScore == 0)
        {
            if (wordPri.Contains("spec1")) entryPriorityScore += 15;
            if (wordPri.Contains("spec2")) entryPriorityScore += 5;
        }
        else
        {
            if (wordPri.Contains("spec1")) entryPriorityScore += 4;
            if (wordPri.Contains("spec2")) entryPriorityScore += 2;
        }

        // Grammatical copula words (である, だ, etc.) compete against high-frequency content words.
        // Boost them so they aren't crowded out by ichi1/news1 verbs in grammatical positions.
        if (candidate.Word.PartsOfSpeech.Contains("cop"))
            entryPriorityScore += 20;

        return entryPriorityScore;
    }
}

internal static class FormPriorityScorer
{
    public static int Score(FormCandidate candidate, bool isKanaSurface)
    {
        var word = candidate.Word;
        var priorities = candidate.Form.Priorities ?? [];
        var wordPri = word.Priorities ?? [];
        int formPriorityScore = 0;

        if (priorities.Contains("ichi1")) formPriorityScore += 10;
        if (priorities.Contains("ichi2")) formPriorityScore += 5;
        if (priorities.Contains("news1")) formPriorityScore += 8;
        if (priorities.Contains("news2")) formPriorityScore += 5;
        if (priorities.Contains("gai1")) formPriorityScore += 8;
        if (priorities.Contains("gai2")) formPriorityScore += 5;

        var nf = priorities.FirstOrDefault(p => p.StartsWith("nf"));
        if (nf is { Length: > 2 } && int.TryParse(nf[2..], out var nfRank))
            formPriorityScore += Math.Max(0, 3 - (int)Math.Round(nfRank / 10f));

        if (formPriorityScore == 0)
        {
            if (priorities.Contains("spec1")) formPriorityScore += 8;
            if (priorities.Contains("spec2")) formPriorityScore += 3;
        }

        if (priorities.Contains("jiten")) formPriorityScore += 25;

        // "uk" (usually-kana) bias, scaled by whether the word has stronger frequency evidence.
        if (word.PartsOfSpeech.Contains("uk"))
        {
            // "uk" bias — jiten is excluded because it's an internal priority, not public frequency evidence
            bool hasFreqMarker = KanaScoringHelpers.HasFrequencyMarker(wordPri, includeJiten: false);
            int ukBonus = hasFreqMarker ? 10 : 3;
            if (candidate.Form.FormType == JmDictFormType.KanaForm || isKanaSurface)
                formPriorityScore += ukBonus;
            else
                formPriorityScore -= ukBonus;
        }

        return formPriorityScore;
    }
}

internal static class FormFlagScorer
{
    public static int Score(FormCandidate candidate, FormScoringContext context)
    {
        var word = candidate.Word;
        var form = candidate.Form;
        int formFlagScore = 0;

        bool formMatchesSurface = context.Surface == form.Text
                                  || (candidate.DeconjForm != null && candidate.DeconjForm.Text == form.Text);
        if (form.IsSearchOnly)
            formFlagScore += formMatchesSurface ? 0 : -300;
        if (form.IsObsolete)
            formFlagScore += formMatchesSurface ? 0 : -150;
        if (!form.IsActiveInLatestSource)
            formFlagScore -= 30;

        bool isPureKanaWord = word.Forms.All(f => f.FormType != JmDictFormType.KanjiForm);
        if (isPureKanaWord && form.FormType == JmDictFormType.KanaForm && context.IsKanaSurface)
            formFlagScore += 20;

        // Colloquial expressions (e.g. こった = ことだ contraction, めでたいこった) are often
        // tagged [exp, col]. When the surface exactly matches such an entry's form, prefer it
        // over adjectival/nominal homographs (e.g. 1238990 こった "elaborate" adj-f) that happen
        // to share the kana form — the colloquial reading is usually intended in running speech.
        if (formMatchesSurface && word.PartsOfSpeech.Contains("col") && word.PartsOfSpeech.Contains("exp"))
            formFlagScore += 15;

        // High-frequency kanji words (jiten priority) should not beat grammatical words
        // via a single-char kana match. Single-char kana tokens are virtually always
        // grammatical (copula/aux/particle); jiten content words like 打(だ) compete
        // unfairly through their kana reading and must be suppressed.
        if (!isPureKanaWord && form.FormType == JmDictFormType.KanaForm
            && context.IsKanaSurface && context.Surface.Length == 1
            && word.Priorities?.Contains("jiten") == true)
            formFlagScore -= 100;

        return formFlagScore;
    }
}

internal static class SurfaceScorer
{
    public static int Score(FormCandidate candidate, FormScoringContext context)
    {
        var surface = context.Surface;
        var formText = candidate.Form.Text;
        int score = 0;

        if (surface == formText)
        {
            score += 300;
            // Katakana-exact match usually indicates a gairaigo entry intentionally written in
            // katakana; give a small edge over otherwise-equal hiragana forms of kanji words
            // (e.g. タンゴ dance vs. 単語 hiragana form たんご).
            bool isPureKatakana = surface.Length > 0;
            foreach (var c in surface)
            {
                if (c is < '\u30A0' or > '\u30FF') { isPureKatakana = false; break; }
            }
            if (isPureKatakana)
                score += 10;
        }
        else if (context.SurfaceHiragana == candidate.FormTextHiragana)
        {
            score += KanaScoringHelpers.IsPureKanaScriptDifference(surface, formText) ? 280 : 120;
        }
        else
        {
            var formLoose = KanaScoringHelpers.ToNormalizedHiragana(formText, convertLongVowelMark: true);
            if (context.SurfaceHiraganaLoose == formLoose)
                score += 60;
            else if (surface.Contains('ー'))
            {
                var surfaceStripped = surface.Replace("ー", "");
                var formStripped = formText.Replace("ー", "");
                if (surfaceStripped.Length > 0 && (surfaceStripped == formStripped ||
                    KanaScoringHelpers.IsPureKanaScriptDifference(surfaceStripped, formStripped)))
                    score += 40;
            }
        }

        return score;
    }
}

internal static class LemmaScorer
{
    public static int Score(FormCandidate candidate, FormScoringContext context, int existingSurfaceScore)
    {
        int score = 0;
        var word = candidate.Word;
        var formText = candidate.Form.Text;

        var surface = context.Surface;
        var dictionaryForm = context.DictionaryForm;
        var normalizedForm = context.NormalizedForm;

        double lemmaScale = 1.0;
        if (candidate.DeconjForm?.Process is { Count: > 0 } deconjProcess)
            lemmaScale = Math.Max(0.0, 1.0 - (deconjProcess.Count - 1) * 0.35);

        // Lemma match — only when dictionaryForm differs from surface.
        if (!string.IsNullOrEmpty(dictionaryForm) && dictionaryForm != surface)
        {
            if (dictionaryForm == formText)
            {
                // When deconjugation traces back to the DictionaryForm, Sudachi and the
                // deconjugator independently agree on the base form — use a higher floor
                // so deep chains (e.g. 来てない→来る via teru contraction) aren't crushed.
                bool deconjConfirmsDictForm = candidate.DeconjForm is { Process.Count: > 0 }
                    && candidate.DeconjForm.Text == context.DictionaryFormHiragana;
                double floor = deconjConfirmsDictForm ? 0.8 : 0.3;
                double effectiveScale = Math.Max(floor, lemmaScale);
                int bonus = (int)(100 * effectiveScale);

                score += bonus;
            }
            else
            {
                if (context.DictionaryFormHiragana == candidate.FormTextHiragana)
                    score += (int)(40 * lemmaScale);
            }
        }

        // NormalizedForm bonus — only when it differs from both surface and dictionaryForm.
        if (!string.IsNullOrEmpty(normalizedForm) && normalizedForm != surface && normalizedForm != dictionaryForm)
        {
            if (normalizedForm == formText)
            {
                score += (int)(50 * lemmaScale);
            }
            else
            {
                if (context.NormalizedFormHiragana == candidate.FormTextHiragana)
                    score += (int)(20 * lemmaScale);

                // Sudachi normalized form matches a form of this word (e.g. リス → 栗鼠).
                if (word.Forms.Any(f => f.Text == normalizedForm))
                {
                    int normBonus = 50;
                    // Suffix-only words (n-suf without n) getting a kanji NormalizedForm boost
                    // on a kana surface are almost always wrong — e.g. ねえ (interjection) being
                    // matched to 姉 (n-suf "older sister"). Suffixes are bound morphemes; standalone
                    // kana tokens are virtually always the function-word reading.
                    if (context.IsKanaSurface && KanaScoringHelpers.ContainsKanji(normalizedForm)
                        && word.PartsOfSpeech.Contains("n-suf")
                        && !word.PartsOfSpeech.Any(p => p is "n" or "n-adv" or "n-t"))
                        normBonus = 10;
                    score += (int)(normBonus * lemmaScale);
                }
            }
        }

        // Deconjugation-based lemma fallback when standard lemma evidence is absent.
        if (candidate.DeconjForm?.Text != null && existingSurfaceScore + score == 0)
        {
            var deconjHira = KanaScoringHelpers.ToNormalizedHiragana(
                                                                     candidate.DeconjForm.Text,
                                                                     convertLongVowelMark: false);

            if (deconjHira == candidate.FormTextHiragana)
            {
                // When Sudachi's DictionaryForm points to a different word and this candidate
                // has no frequency evidence, the deconjugation match is likely spurious.
                // E.g. 背負っていた deconj→背負ってる (exp "conceited") instead of 背負う ("carry").
                bool dictFormConflicts = !string.IsNullOrEmpty(context.DictionaryForm)
                    && context.DictionaryForm != context.Surface
                    && !word.Forms.Any(f => f.Text == context.DictionaryForm
                        || KanaScoringHelpers.IsPureKanaScriptDifference(f.Text, context.DictionaryForm));

                if (!dictFormConflicts || KanaScoringHelpers.HasFrequencyMarker(word.Priorities))
                    score += (int)(100 * lemmaScale);
            }
        }

        return score;
    }
}

internal static class PenaltyScorer
{
    public static bool ApplyConjugatedIdentityPenalty(
        FormCandidate candidate,
        FormScoringContext context,
        ref int surfaceMatchScore)
    {
        bool surfaceMatchesFormDirectly = context.Surface == candidate.Form.Text
            || KanaScoringHelpers.IsPureKanaScriptDifference(context.Surface, candidate.Form.Text);

        if (!string.IsNullOrEmpty(context.DictionaryForm)
            && context.DictionaryForm != context.Surface
            && surfaceMatchesFormDirectly
            && (candidate.DeconjForm == null || candidate.DeconjForm.Process.Count == 0))
        {
            // Non-inflectable words (adj-pn, etc.) cannot be conjugated forms,
            // so the penalty should not apply (e.g. 亡き is adj-pn, not a conjugation of 亡い)
            var readingPos = candidate.CachedReadingPos;
            IEnumerable<string> posToCheck = readingPos.Count > 0 ? readingPos : candidate.Word.PartsOfSpeech;
            bool isInflectable = posToCheck.Any(p =>
                p is "adj-i" or "adj-ix"
                || (p.StartsWith('v') && p is not "vulg" and not "vet" and not "vidg"));

            if (!isInflectable)
            {
                // Expressions have their own ExpressionConflictPenalty mechanism; skip this penalty for them.
                bool isExpression = posToCheck.Any(p => p is "exp" or "on-mim");
                if (isExpression) return false;

                // Numerals (一つ, 二つ, 三つ …) have DictionaryForm=つ (the counter suffix)
                // from Sudachi, not a conjugation base. Skip the penalty.
                bool isNumeral = posToCheck.Any(p => p is "num");
                if (isNumeral) return false;

                // Standalone adverbs with frequency evidence (e.g. 悪しからず ichi1) are not
                // conjugated forms — they're fixed expressions found via direct lookup.
                // The DictionaryForm comes from a Sudachi sub-token and is irrelevant.
                bool isAdverb = posToCheck.Any(p => p is "adv" or "adv-to");
                if (isAdverb && KanaScoringHelpers.HasFrequencyMarker(candidate.Word.Priorities))
                    return false;

                // Particles are function words whose surface form IS their canonical form.
                // Sudachi may give DictionaryForm=だ for で (etymological), but で the particle
                // is not a conjugation — don't penalise it.
                // Restrict to kana surfaces: kanji forms of particles (e.g. 許し for ばかし)
                // are archaic and virtually never intended; let them fall through to the penalty.
                bool isParticle = posToCheck.Any(p => p is "prt");
                if (isParticle && context.IsKanaSurface) return false;

                // adj-pn/adj-t are standalone prenominal adjectives (e.g. 亡き, 無き, 堂々たる).
                // Their surface IS their dictionary form; Sudachi may give a different DictionaryForm
                // (the archaic base), but only skip the penalty when that DictForm IS one of this
                // word's own forms. If DictForm points to a completely different word
                // (e.g. させる→する), fall through to the nounHasDictForm check below.
                bool isAdnominal = posToCheck.Any(p => p is "adj-pn" or "adj-t");
                if (isAdnominal && candidate.Word.Forms.Any(f => f.Text == context.DictionaryForm
                        || KanaScoringHelpers.IsPureKanaScriptDifference(f.Text, context.DictionaryForm)))
                    return false;

                // For non-inflectable words (e.g., plain nouns), still apply the penalty when Sudachi's
                // DictionaryForm doesn't appear in this word's forms — it points to a different (inflectable) word.
                // E.g., surface=答え, DictForm=答える: noun 答え shouldn't beat verb 答える via surface match.
                bool nounHasDictForm = candidate.Word.Forms.Any(f => f.Text == context.DictionaryForm
                    || KanaScoringHelpers.IsPureKanaScriptDifference(f.Text, context.DictionaryForm));
                if (!nounHasDictForm)
                {
                    // Interjections (e.g. やった "hooray!") can never be conjugated verb forms.
                    // When DictForm points to a verb, the -200 base penalty leaves them too close to the
                    // verb candidate. Use -300 to fully cancel the surface-match bonus.
                    bool isInterjection = posToCheck.Any(p => p is "int");
                    if (isInterjection)
                    {
                        surfaceMatchScore -= 300;
                        return true;
                    }

                    // Ichidan verb stems (DictForm = Surface + る) commonly stand alone as nouns
                    // (目覚め, 答え, 始め) or prefixes (生き). Softer penalty when reading matches.
                    // Godan masu-stems (立ち from 立つ, 持ち from 持つ) are almost always verbal.
                    if (!string.IsNullOrEmpty(context.SudachiReading) && !context.IsKanaSurface
                        && context.DictionaryForm == context.Surface + "る")
                    {
                        var sudachiHira = KanaScoringHelpers.ToNormalizedHiragana(context.SudachiReading, convertLongVowelMark: false);
                        bool hasExactReadingMatch = candidate.Word.Forms
                            .Where(f => f.FormType == JmDictFormType.KanaForm)
                            .Any(f => KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false) == sudachiHira);
                        if (hasExactReadingMatch)
                        {
                            bool isPrefixLike = candidate.Word.PartsOfSpeech.Contains("pref");
                            bool isNounLike = candidate.Word.PartsOfSpeech.Any(p => p is "n" or "n-adv" or "n-t");
                            if (isPrefixLike)
                            {
                                surfaceMatchScore -= 100;
                                return false;
                            }
                            if (isNounLike)
                            {
                                surfaceMatchScore -= context.SudachiPOS == PartOfSpeech.Verb ? 200 : 110;
                                return false;
                            }
                        }
                    }

                    // adj-pn/adj-t (無き, 堂々たる) and aux-v (如く) have archaic bases as
                    // DictionaryForm; keep the softer penalty so they can still beat competitors.
                    // Plain nouns (e.g. 支店 matching してん) get the full -300 to fully cancel
                    // the coincidental surface-match bonus.
                    bool isNonNounWithLegitimateForm = posToCheck.Any(p => p is "adj-pn" or "adj-t" or "aux-v");
                    surfaceMatchScore -= isNonNounWithLegitimateForm ? 200 : 300;
                    return true;
                }
                return false;
            }

            // adj-ix (e.g. いい/よい) has irregular conjugations; its base forms are not conjugated
            // forms of other words, even if Sudachi misidentifies them (e.g. いい as verb いう).
            if (posToCheck.Any(p => p is "adj-ix"))
                return false;

            // High-priority fixed expressions (e.g. いけない "must not", exp+adj-i, ichi1) whose
            // surface exactly matches their form should not be penalised just because Sudachi
            // analysed them as a conjugation of a different verb (e.g. いけない → いける+ない).
            bool isInflectableExpression = posToCheck.Any(p => p is "exp" or "on-mim");
            if (isInflectableExpression && KanaScoringHelpers.HasFrequencyMarker(candidate.Word.Priorities))
                return false;

            // Inflectable words whose forms include DictionaryForm (e.g. 食べ from 食べる)
            // get a softer penalty; truly unrelated words (e.g. 一転 matching いってん when
            // DictionaryForm=いう) get the full -300 to cancel the coincidental surface match.
            bool inflectableHasDictForm = candidate.Word.Forms.Any(f => f.Text == context.DictionaryForm
                || KanaScoringHelpers.IsPureKanaScriptDifference(f.Text, context.DictionaryForm));
            surfaceMatchScore -= inflectableHasDictForm ? 200 : 300;
            return true;
        }

        return false;
    }

    public static bool ApplyExpressionConflictPenalty(
        FormCandidate candidate,
        FormScoringContext context,
        ref int surfaceMatchScore)
    {
        bool expressionSurfaceMatchesForm = context.Surface == candidate.Form.Text
            || KanaScoringHelpers.IsPureKanaScriptDifference(context.Surface, candidate.Form.Text);

        if (string.IsNullOrEmpty(context.DictionaryForm)
            || context.DictionaryForm == context.Surface
            || !expressionSurfaceMatchesForm)
            return false;

        var readingPos = candidate.CachedReadingPos;
        IEnumerable<string> posToCheck = readingPos.Count > 0 ? readingPos : candidate.Word.PartsOfSpeech;
        bool isExpression = posToCheck.Any(p => p is "exp" or "on-mim");
        if (!isExpression)
            return false;

        bool hasFreqMarker = KanaScoringHelpers.HasFrequencyMarker(candidate.Word.Priorities);
        if (hasFreqMarker)
            return false;

        bool dictFormMatchesWord = candidate.Word.Forms.Any(f => f.Text == context.DictionaryForm
            || KanaScoringHelpers.IsPureKanaScriptDifference(f.Text, context.DictionaryForm));
        if (!dictFormMatchesWord)
        {
            // If DictionaryForm is a strict prefix of the surface, the expression likely derives
            // from a dialectal or auxiliary base (e.g. Kansai copula や → やろ from やろう).
            // Apply a softer penalty to avoid suppressing the correct expression entry.
            bool dictFormIsPrefixOfSurface = context.DictionaryFormHiragana is { Length: > 0 }
                && context.SurfaceHiragana.Length > context.DictionaryFormHiragana.Length
                && context.SurfaceHiragana.StartsWith(context.DictionaryFormHiragana, StringComparison.Ordinal);
            surfaceMatchScore -= dictFormIsPrefixOfSurface ? 100 : 250;
            return true;
        }

        return false;
    }
}

internal static class ScriptScorer
{
    // Superlinear prefix scoring: 5*n*(n+1)/2 capped at n=5 → 5, 15, 30, 50, 75
    private static readonly int[] KanjiScale = [0, 5, 15, 30, 50, 75];
    // Weaker scaling for kana-only forms: 3*n*(n+1)/2 capped at n=5 → 3, 9, 18, 30, 45
    private static readonly int[] KanaScale = [0, 3, 9, 18, 30, 45];

    public static int Score(FormCandidate candidate, FormScoringContext context)
    {
        var word = candidate.Word;
        var form = candidate.Form;
        var surface = context.Surface;

        int prefixLen = KanaScoringHelpers.GetCommonPrefixLen(surface, form.Text);
        bool hasKanji = KanaScoringHelpers.ContainsKanji(form.Text);
        var scale = hasKanji ? KanjiScale : KanaScale;
        int scriptScore = scale[Math.Min(prefixLen, scale.Length - 1)];

        // Ichidan stem bonus — suppress when Sudachi identifies a different verb.
        var ichidanReadingPos = candidate.CachedReadingPos;
        IEnumerable<string> ichidanPosToCheck = ichidanReadingPos.Count > 0 ? ichidanReadingPos : word.PartsOfSpeech;
        if (form.Text.Length > 2
            && form.Text[^1] == 'る'
            && ichidanPosToCheck.Any(p => p is "v1" or "v1-s")
            && surface.StartsWith(form.Text[..^1], StringComparison.Ordinal))
        {
            bool dictFormConflicts = context.DictionaryForm is not null
                                     && context.DictionaryForm != surface
                                     && !word.Forms.Any(f => f.Text == context.DictionaryForm
                                         || KanaScoringHelpers.IsPureKanaScriptDifference(f.Text, context.DictionaryForm));

            if (!dictFormConflicts)
            {
                int stemLen = form.Text.Length - 1;
                scriptScore += Math.Min(15, stemLen * 5);
            }
        }

        // When Sudachi identifies a DictionaryForm that doesn't belong to this word,
        // the prefix overlap with the conjugated surface is coincidental
        // (e.g. noun 気づかれ matching the passive form of verb 気づく). Cap the script score.
        if (scriptScore > 15
            && context.DictionaryForm is not null
            && context.DictionaryForm != context.Surface
            && !word.Forms.Any(f => f.Text == context.DictionaryForm
                || KanaScoringHelpers.IsPureKanaScriptDifference(f.Text, context.DictionaryForm)))
        {
            scriptScore = Math.Min(scriptScore, 15);
        }

        return scriptScore;
    }
}

internal static class ReadingScorer
{
    public static int Score(FormCandidate candidate, FormScoringContext context, bool identityPenaltyApplied,
        IReadOnlySet<string>? archaicPosTypes = null)
    {
        int readingMatchScore = 0;
        if (!string.IsNullOrEmpty(context.SudachiReading) && !context.IsKanaSurface)
        {
            var word = candidate.Word;
            var sudachiHira = KanaScoringHelpers.ToNormalizedHiragana(context.SudachiReading, convertLongVowelMark: false);

            int kanaFormCount = 0;
            bool hasMatchingReading = false;
            foreach (var f in word.Forms)
            {
                if (f.FormType != JmDictFormType.KanaForm) continue;
                kanaFormCount++;
                var h = KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
                if (h == sudachiHira) { hasMatchingReading = true; break; }
            }

            if (hasMatchingReading)
            {
                readingMatchScore += 70;
            }
            else if (sudachiHira.Length > 1)
            {
                bool found = false;
                foreach (var f in word.Forms)
                {
                    if (f.FormType != JmDictFormType.KanaForm) continue;
                    var h = KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
                    if (h.Length > sudachiHira.Length && h.StartsWith(sudachiHira, StringComparison.Ordinal))
                    { found = true; break; }
                }

                if (found)
                {
                    readingMatchScore += 70;
                }
                else if (sudachiHira.Length > 2)
                {
                    foreach (var f in word.Forms)
                    {
                        if (f.FormType != JmDictFormType.KanaForm) continue;
                        var h = KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
                        if (h.Length < 3 || !h.EndsWith('る')) continue;
                        if (sudachiHira.StartsWith(h[..^1], StringComparison.Ordinal))
                        { found = true; break; }
                    }
                    if (found)
                        readingMatchScore += 70;
                }

                if (!found)
                {
                    var sudachiStem = sudachiHira[..^1];
                    bool hasStemMatch = false;
                    foreach (var f in word.Forms)
                    {
                        if (f.FormType != JmDictFormType.KanaForm) continue;
                        var h = KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
                        if (h.Length > 1 && h[..^1] == sudachiStem) { hasStemMatch = true; break; }
                    }

                    if (!hasStemMatch && sudachiStem.Length > 1 && sudachiStem[^1] == 'い')
                    {
                        var rootStem = sudachiStem[..^1];
                        foreach (var f in word.Forms)
                        {
                            if (f.FormType != JmDictFormType.KanaForm) continue;
                            var h = KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
                            if (h.Length > 1 && h[..^1] == rootStem) { hasStemMatch = true; break; }
                        }
                    }

                    if (!hasStemMatch && sudachiStem.Length > 1 && sudachiStem[^1] == 'っ')
                    {
                        var rootStem = sudachiStem[..^1];
                        foreach (var f in word.Forms)
                        {
                            if (f.FormType != JmDictFormType.KanaForm) continue;
                            var h = KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
                            if (h.Length > 1 && h[..^1] == rootStem) { hasStemMatch = true; break; }
                        }
                    }

                    if (hasStemMatch)
                        readingMatchScore += 25;
                }
            }

            if (readingMatchScore == 0 && sudachiHira.Length > 2)
            {
                bool hasSuruStemMatch = false;
                foreach (var f in word.Forms)
                {
                    if (f.FormType != JmDictFormType.KanaForm) continue;
                    var h = KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
                    if (h.Length < 2 || h.Length >= sudachiHira.Length) continue;
                    if (!sudachiHira.StartsWith(h, StringComparison.Ordinal)) continue;
                    char nextChar = sudachiHira[h.Length];
                    if (nextChar is 'し' or 'す' or 'さ' or 'せ') { hasSuruStemMatch = true; break; }
                }
                if (hasSuruStemMatch)
                    readingMatchScore += 70;
            }

            if (readingMatchScore == 0 && sudachiHira.Length >= 2 && kanaFormCount > 0)
            {
                char firstChar = sudachiHira[0];
                bool anyPrefixMatch = false;
                foreach (var f in word.Forms)
                {
                    if (f.FormType != JmDictFormType.KanaForm) continue;
                    var h = KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
                    if (h.Length > 0 && h[0] == firstChar) { anyPrefixMatch = true; break; }
                }

                if (anyPrefixMatch)
                {
                    readingMatchScore += 25;
                }
                else
                {
                    bool formMatchesDictForm = false;
                    if (!string.IsNullOrEmpty(context.DictionaryForm))
                    {
                        foreach (var f in word.Forms)
                            if (f.Text == context.DictionaryForm) { formMatchesDictForm = true; break; }
                    }
                    if (formMatchesDictForm && candidate.DeconjForm?.Process is not { Count: > 0 })
                        readingMatchScore -= 70;
                }
            }

            if (readingMatchScore > 0 && archaicPosTypes is { Count: > 0 })
            {
                var readingPos = candidate.CachedReadingPos;
                if (readingPos.Count > 0)
                {
                    foreach (var p in readingPos)
                        if (archaicPosTypes.Contains(p)) { readingMatchScore /= 2; break; }
                }
                else
                {
                    foreach (var p in word.PartsOfSpeech)
                        if (archaicPosTypes.Contains(p)) { readingMatchScore /= 2; break; }
                }
            }
        }

        if (identityPenaltyApplied && readingMatchScore > 0)
            readingMatchScore = 0;

        // Kanji with multiple valid readings (e.g. 得る える/うる, 色 いろ/しょく)
        // shouldn't be crushed when Sudachi picks a different reading — the kanji
        // surface is inherently ambiguous. This also prevents the SurfaceMatch 0.3
        // slash in FormCandidateScorer.Score from triggering.
        if (readingMatchScore < 0
            && candidate.Form.FormType == JmDictFormType.KanjiForm
            && context.Surface == candidate.Form.Text
            && KanaScoringHelpers.HasFrequencyMarker(candidate.Word.Priorities))
        {
            readingMatchScore = 0;
        }

        return readingMatchScore;
    }
}

internal static class ReadingPosHelper
{
    public static HashSet<string> GetPosForReading(JmDictWord word, byte readingIndex)
    {
        if (word.Definitions.Count == 0)
            return [];

        return word.Definitions
            .Where(d => d.RestrictedToReadingIndices == null
                     || d.RestrictedToReadingIndices.Contains((short)readingIndex))
            .SelectMany(d => d.PartsOfSpeech)
            .ToHashSet();
    }
}

internal static class KanaScoringHelpers
{
    public static string ToNormalizedHiragana(string text, bool convertLongVowelMark)
    {
        return KanaNormalizer.Normalize(KanaConverter.ToHiragana(text, convertLongVowelMark));
    }

    public static bool IsPureKanaScriptDifference(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            int diff = a[i] - b[i];
            if (diff is not 0x60 and not -0x60) return false;
        }
        return true;
    }

    public static int GetCommonPrefixLen(string s1, string s2)
    {
        int len = Math.Min(s1.Length, s2.Length);
        int match = 0;
        for (int i = 0; i < len; i++)
        {
            if (s1[i] == s2[i])
                match++;
            else
                break;
        }

        return match;
    }

    public static bool IsKanaSurfaceWithNoMatchingReading(
        FormScoringContext context, JmDictWord word, string formText)
    {
        if (!context.IsKanaSurface || !ContainsKanji(formText)) return false;
        if (!word.Forms.Any(f => !ContainsKanji(f.Text))) return false;

        var surfaceFirstChar = context.SurfaceHiragana.Length > 0 ? context.SurfaceHiragana[0] : '\0';
        foreach (var f in word.Forms)
        {
            if (ContainsKanji(f.Text)) continue;
            var fHiragana = ToNormalizedHiragana(f.Text, convertLongVowelMark: false);
            if (fHiragana.Length > 0 && fHiragana[0] == surfaceFirstChar)
                return false;
        }
        return true;
    }

    public static bool ContainsKanji(string text)
    {
        foreach (char c in text)
        {
            if (JapaneseTextHelper.IsKanji(c))
                return true;
        }

        return false;
    }

    public static bool HasFrequencyMarker(IReadOnlyList<string>? priorities, bool includeJiten = true)
    {
        if (priorities == null) return false;
        foreach (var p in priorities)
        {
            if (p is "ichi1" or "ichi2" or "news1" or "news2") return true;
            if (includeJiten && p == "jiten") return true;
            if (p.StartsWith("nf")) return true;
        }
        return false;
    }
}

internal static class PosAffinityScorer
{
    public static int Score(FormCandidate candidate, FormScoringContext context)
    {
        if (context.SudachiPOS is PartOfSpeech.Unknown)
            return 0;

        bool isHighConfidencePOS = context.SudachiPOS is PartOfSpeech.Verb or PartOfSpeech.IAdjective
            or PartOfSpeech.Suffix or PartOfSpeech.Interjection;

        if (!isHighConfidencePOS)
            return 0;

        bool compatible = PosMapper.IsJmDictCompatibleWithSudachi(
            candidate.Word.CachedPOS, context.SudachiPOS);

        if (!compatible)
            return context.SudachiPOS == PartOfSpeech.Interjection ? -250 : -20;

        int score = 25;

        // When Sudachi says Suffix but the candidate also has Noun POS, it's a noun+suffix
        // hybrid (e.g. ゲ [n-suf, n] "videogame") competing with a pure suffix (e.g. げ [suf]).
        // Penalize the hybrid so noun-particle-synergy can't flip the winner.
        if (context.SudachiPOS == PartOfSpeech.Suffix
            && PosMask.Has(candidate.Word.CachedPOSMask, PosMask.NounLike))
            score -= 50;

        // Verb-class matching: when Sudachi identifies a specific godan row via DictionaryForm,
        // penalize candidates from a different row (e.g. こく→v5k vs こる→v5r).
        // Only for unambiguous endings (る is ambiguous between v5r and v1).
        if (context.SudachiPOS == PartOfSpeech.Verb
            && context.DictionaryForm is { Length: > 0 }
            && context.DictionaryForm != context.Surface)
        {
            var expectedTag = InferGodanTag(context.DictionaryForm);
            if (expectedTag != null)
            {
                bool hasMatchingTag = candidate.Word.PartsOfSpeech.Contains(expectedTag)
                    || candidate.Word.PartsOfSpeech.Contains(expectedTag + "-s");
                if (!hasMatchingTag)
                    score -= 45;
            }
        }

        // Plain suru-verb nouns (vs without a direct verb class) conjugate via する,
        // not directly. Penalize kana-form matches against verb conjugations — e.g.
        // 遺棄 "いき" [n, vs] should not match いきました; that's 行く.
        if (context.SudachiPOS == PartOfSpeech.Verb
            && candidate.Form.FormType == JmDictFormType.KanaForm
            && candidate.Word.PartsOfSpeech.Contains("vs")
            && !candidate.Word.PartsOfSpeech.Any(static p =>
                p is "v1" or "v1-s" or "vs-c" or "vs-i" or "vs-s"
                or "v5a" or "v5b" or "v5g" or "v5k" or "v5k-s"
                or "v5m" or "v5n" or "v5r" or "v5r-i" or "v5s" or "v5t"
                or "v5u" or "v5u-s" or "v5uru" or "vk" or "vz" or "aux-v"))
        {
            score -= 60;
        }

        // Sudachi 非自立可能 means the verb is used as an auxiliary (e.g. くれる after te-form).
        // Boost candidates with aux-v, penalize those without — e.g. 呉れる (aux-v) over 暮れる.
        if (context.IsSudachiPossibleDependant
            && context.SudachiPOS == PartOfSpeech.Verb)
        {
            if (candidate.Word.PartsOfSpeech.Contains("aux-v"))
                score += 30;
            else
                score -= 30;
        }

        return score;
    }

    private static string? InferGodanTag(string dictionaryForm)
    {
        if (dictionaryForm.Length == 0) return null;
        return dictionaryForm[^1] switch
        {
            'く' => "v5k",
            'ぐ' => "v5g",
            'す' => "v5s",
            'つ' => "v5t",
            'ぬ' => "v5n",
            'ぶ' => "v5b",
            'む' => "v5m",
            'う' => "v5u",
            _ => null // る is ambiguous (v5r vs v1), others not godan
        };
    }
}