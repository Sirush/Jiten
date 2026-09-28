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
        new() { "ね", "よ", "ぞ", "わ", "な", "さ", "か", "の", "かな", "かしら", "よね", "わよ", "わね", "のよ", "のね",
                "もんか" };

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
                // Archaic pronouns (汝 なんじ) are common in modern literary/fantasy prose, so they get a softer penalty.
                bool isPronoun = word.CachedPOS.Contains(PartOfSpeech.Pronoun);
                wordScore -= isArchaicSentence ? 50 : (isPronoun ? 100 : 350);
            }
        }

        // Any modern primary POS exempts (無し adj-ku also has n); "suf" doesn't, since a v2a-s word can also be a suffix.
        var posToCheck = candidate.EffectivePos;
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

        if (isSentenceInitial && word.PartsOfSpeech.Any(p => p is "adv" or "adv-to"))
            wordScore += 10;

        // Separates the final particle な from homographs like the na-adjective ending.
        if (isSentenceFinal
            && word.PartsOfSpeech.Any(p => p is "prt")
            && SentenceFinalParticleSurfaces.Contains(candidate.FormTextHiragana))
            wordScore += 25;

        // Unclass entries are uncategorised JMnedict names, a last resort outside name context.
        if (!isNameContext && word.PartsOfSpeech.All(p => p is "unclass"))
            wordScore -= 40;

        // Pure counters almost always follow a number, so homophones win elsewhere (色 いろ over counter ショク).
        if (word.PartsOfSpeech.All(p => p is "ctr"))
            wordScore -= 10;

        int chainCount = candidate.DeconjForm?.Process.Length ?? 0;
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

        var wnf = wordPri.FirstOrDefault(p => p.StartsWith("nf", StringComparison.Ordinal));
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

        // Copulas (である, だ) lack the ichi1/news1 tags that would keep frequent verbs from crowding them out.
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

        var nf = priorities.FirstOrDefault(p => p.StartsWith("nf", StringComparison.Ordinal));
        if (nf is { Length: > 2 } && int.TryParse(nf[2..], out var nfRank))
            formPriorityScore += Math.Max(0, 3 - (int)Math.Round(nfRank / 10f));

        if (formPriorityScore == 0)
        {
            if (priorities.Contains("spec1")) formPriorityScore += 8;
            if (priorities.Contains("spec2")) formPriorityScore += 3;
        }

        if (priorities.Contains("jiten")) formPriorityScore += 25;

        if (word.PartsOfSpeech.Contains("uk"))
        {
            // jiten is excluded: it's an internal priority, not public frequency evidence.
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

        // JMDict orders readings by prevalence, so a secondary one (草花 そうか) must not beat そうか "I see"; uk is exempt.
        if (!isPureKanaWord && form.FormType == JmDictFormType.KanaForm && context.IsKanaSurface
            && !word.PartsOfSpeech.Contains("uk")
            && !IsFirstKanaReading(word, candidate.FormTextHiragana))
            formFlagScore -= 15;

        // Exact-surface [exp, col] entries (こった = ことだ) beat homographs like こった "elaborate" (1238990) in speech.
        if (formMatchesSurface && word.PartsOfSpeech.Contains("col") && word.PartsOfSpeech.Contains("exp"))
            formFlagScore += 15;

        // Single-kana tokens are almost always grammatical, so a jiten kanji word (打 だ) must not win through its reading.
        if (!isPureKanaWord && form.FormType == JmDictFormType.KanaForm
            && context.IsKanaSurface && context.Surface.Length == 1
            && word.Priorities?.Contains("jiten") == true)
            formFlagScore -= 100;

        return formFlagScore;
    }

    /// <summary>Phonetic compare, so a katakana spelling of the primary reading (懐炉 カイロ) counts as primary.</summary>
    private static bool IsFirstKanaReading(JmDictWord word, string formTextHiragana)
    {
        foreach (var f in word.Forms)
        {
            if (f.FormType != JmDictFormType.KanaForm) continue;
            return KanaScoringHelpers.ToNormalizedHiragana(f.Text, convertLongVowelMark: false) == formTextHiragana;
        }

        return false;
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
            // A katakana-exact match is usually intended gairaigo (タンゴ over 単語's たんご).
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
            // Hiragana → katakana folds hit different words (まま vs ママ), as do mixed → hiragana (ショートして → しょうとして).
            // Katakana surface → hiragana form keeps the full bonus (gairaigo written in kana).
            bool pureScriptDifference = KanaScoringHelpers.IsPureKanaScriptDifference(surface, formText);
            if (JapaneseTextHelper.IsAllHiragana(surface)
                    ? formText.Any(JapaneseTextHelper.IsKatakana)
                    : !pureScriptDifference && JapaneseTextHelper.IsAllHiragana(formText)
                      && surface.Any(JapaneseTextHelper.IsKatakana) && surface.Any(JapaneseTextHelper.IsHiragana))
                score += 40;
            else
                score += pureScriptDifference ? 280 : 120;
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
        if (candidate.DeconjForm?.Process is { Length: > 0 } deconjProcess)
            lemmaScale = Math.Max(0.0, 1.0 - (deconjProcess.Length - 1) * 0.35);

        if (!string.IsNullOrEmpty(dictionaryForm) && dictionaryForm != surface)
        {
            if (dictionaryForm == formText)
            {
                // Sudachi and the deconjugator agreeing earns a higher floor so deep chains (来てない→来る) aren't crushed.
                bool deconjConfirmsDictForm = candidate.DeconjForm is { Process.Length: > 0 }
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

                if (word.Forms.Any(f => f.Text == normalizedForm))
                {
                    int normBonus = 50;
                    // A standalone kana token is not a bound suffix: ねえ must not become 姉 (n-suf) via its NormalizedForm.
                    if (context.IsKanaSurface && KanaScoringHelpers.ContainsKanji(normalizedForm)
                        && word.PartsOfSpeech.Contains("n-suf")
                        && !word.PartsOfSpeech.Any(p => p is "n" or "n-adv" or "n-t"))
                        normBonus = 10;
                    score += (int)(normBonus * lemmaScale);
                }
            }
        }

        if (candidate.DeconjForm?.Text != null && existingSurfaceScore + score == 0)
        {
            var deconjHira = KanaScoringHelpers.ToNormalizedHiragana(
                                                                     candidate.DeconjForm.Text,
                                                                     convertLongVowelMark: false);

            if (deconjHira == candidate.FormTextHiragana)
            {
                // Against a conflicting DictionaryForm, an unprioritized deconj match is spurious (背負っていた → 背負ってる exp).
                bool dictFormConflicts = !string.IsNullOrEmpty(context.DictionaryForm)
                    && context.DictionaryForm != context.Surface
                    && !KanaScoringHelpers.WordHasFormEquivalentTo(word, context.DictionaryForm);

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
        bool surfaceMatchesFormDirectly = KanaScoringHelpers.SurfaceEquivalentTo(context.Surface, candidate.Form.Text);

        if (!string.IsNullOrEmpty(context.DictionaryForm)
            && context.DictionaryForm != context.Surface
            && surfaceMatchesFormDirectly
            && (candidate.DeconjForm == null || candidate.DeconjForm.Process.Length == 0))
        {
            var posToCheck = candidate.EffectivePos;
            bool isInflectable = KanaScoringHelpers.IsInflectableVerbOrAdj(posToCheck);

            if (!isInflectable)
            {
                // Expressions are handled by ApplyExpressionConflictPenalty.
                bool isExpression = posToCheck.Any(p => p is "exp" or "on-mim");
                if (isExpression) return false;

                // Sudachi gives numerals (一つ) DictionaryForm=つ, the counter, not a conjugation base.
                bool isNumeral = posToCheck.Any(p => p is "num");
                if (isNumeral) return false;

                // Frequent adverbs (悪しからず ichi1) are fixed forms; their DictionaryForm comes from a Sudachi sub-token.
                bool isAdverb = posToCheck.Any(p => p is "adv" or "adv-to");
                if (isAdverb && KanaScoringHelpers.HasFrequencyMarker(candidate.Word.Priorities))
                    return false;

                // Particle で gets DictionaryForm=だ but isn't a conjugation; kanji particle forms (許し for ばかし) stay penalised.
                bool isParticle = posToCheck.Any(p => p is "prt");
                if (isParticle && context.IsKanaSurface) return false;

                // adj-pn/adj-t (亡き, 堂々たる) are exempt only when DictionaryForm is their own form, not another word's (させる→する).
                bool isAdnominal = posToCheck.Any(p => p is "adj-pn" or "adj-t");
                if (isAdnominal && KanaScoringHelpers.WordHasFormEquivalentTo(candidate.Word, context.DictionaryForm))
                    return false;

                // Sudachi reads suffix たまえ (食べたまえ) as 給う's imperative; its 非自立可能 flag marks the auxiliary use.
                bool isBoundSuffix = posToCheck.Any(p => p is "suf")
                                     && !posToCheck.Any(p => p is "n" or "n-adv" or "n-t");
                if (isBoundSuffix && context.IsSudachiPossibleDependant)
                    return false;

                // Classical き-forms (悪しき) get the modern い-adjective as DictionaryForm, but the き entry is the right word.
                if ((isAdnominal || posToCheck.Contains("adj-f"))
                    && candidate.Form.Text.EndsWith('き'))
                {
                    var attributiveStem = candidate.Form.Text[..^1];
                    if (context.DictionaryForm == attributiveStem
                        || context.DictionaryForm == attributiveStem + "い"
                        || context.NormalizedForm == attributiveStem + "い")
                        return false;
                }

                // A DictionaryForm outside this word's forms names another word: noun 答え must not beat verb 答える.
                bool nounHasDictForm = KanaScoringHelpers.WordHasFormEquivalentTo(candidate.Word, context.DictionaryForm);
                if (!nounHasDictForm)
                {
                    // An interjection (やった) is never a verb form, so -300 fully cancels its surface bonus.
                    bool isInterjection = posToCheck.Any(p => p is "int");
                    if (isInterjection)
                    {
                        surfaceMatchScore -= 300;
                        return true;
                    }

                    // Ichidan stems often stand alone as nouns (目覚め) or prefixes (生き); godan masu-stems (立ち) are verbal.
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

                    // adj-pn/adj-t/aux-v (無き, 如く) get archaic-base DictionaryForms; plain nouns (支店 as してん) get the full -300.
                    bool isNonNounWithLegitimateForm = posToCheck.Any(p => p is "adj-pn" or "adj-t" or "aux-v");
                    surfaceMatchScore -= isNonNounWithLegitimateForm ? 200 : 300;
                    return true;
                }
                return false;
            }

            // adj-ix base forms aren't other words' conjugations, even when Sudachi reads いい as いう.
            if (posToCheck.Any(p => p is "adj-ix"))
                return false;

            // Frequent fixed expressions (いけない ichi1) keep their exact match though Sudachi reads いける+ない.
            bool isInflectableExpression = posToCheck.Any(p => p is "exp" or "on-mim");
            if (isInflectableExpression && KanaScoringHelpers.HasFrequencyMarker(candidate.Word.Priorities))
                return false;

            // Words owning the DictionaryForm (食べ from 食べる) get -200; unrelated ones (一転 for いってん ← いう) get -300.
            bool inflectableHasDictForm = KanaScoringHelpers.WordHasFormEquivalentTo(candidate.Word, context.DictionaryForm);
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
        bool expressionSurfaceMatchesForm = KanaScoringHelpers.SurfaceEquivalentTo(context.Surface, candidate.Form.Text);

        if (string.IsNullOrEmpty(context.DictionaryForm)
            || context.DictionaryForm == context.Surface
            || !expressionSurfaceMatchesForm)
            return false;

        var posToCheck = candidate.EffectivePos;
        bool isExpression = posToCheck.Any(p => p is "exp" or "on-mim");
        if (!isExpression)
            return false;

        bool hasFreqMarker = KanaScoringHelpers.HasFrequencyMarker(candidate.Word.Priorities);
        if (hasFreqMarker)
            return false;

        bool dictFormMatchesWord = KanaScoringHelpers.WordHasFormEquivalentTo(candidate.Word, context.DictionaryForm);
        if (!dictFormMatchesWord)
        {
            // A DictionaryForm prefixing the surface signals a dialectal or auxiliary base (Kansai や → やろ), so softer.
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
    // Superlinear by common-prefix length: 5*n*(n+1)/2 for kanji forms, 3*n*(n+1)/2 for kana, capped at n=5.
    private static readonly int[] KanjiScale = [0, 5, 15, 30, 50, 75];
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

        if (form.Text.Length > 2
            && form.Text[^1] == 'る'
            && candidate.EffectivePos.Any(p => p is "v1" or "v1-s")
            && surface.StartsWith(form.Text[..^1], StringComparison.Ordinal))
        {
            if (!KanaScoringHelpers.DictFormPointsToDifferentWord(context, word))
            {
                int stemLen = form.Text.Length - 1;
                scriptScore += Math.Min(15, stemLen * 5);
            }
        }

        // Prefix overlap is coincidental when DictionaryForm names another word (noun 気づかれ vs passive of 気づく).
        if (scriptScore > 15 && KanaScoringHelpers.DictFormPointsToDifferentWord(context, word))
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
                    if (formMatchesDictForm && candidate.DeconjForm?.Process is not { Length: > 0 })
                        readingMatchScore -= 70;
                }
            }

            if (readingMatchScore > 0 && archaicPosTypes is { Count: > 0 })
            {
                foreach (var p in candidate.EffectivePos)
                    if (archaicPosTypes.Contains(p)) { readingMatchScore /= 2; break; }
            }
        }

        if (identityPenaltyApplied && readingMatchScore > 0)
            readingMatchScore = 0;

        // Multi-reading kanji (得る える/うる) aren't crushed by Sudachi's pick, which also skips the 0.3 surface slash.
        // A bare single kanji qualifies without frequency data: Sudachi must pick some reading (智→サトシ).
        if (readingMatchScore < 0
            && candidate.Form.FormType == JmDictFormType.KanjiForm
            && context.Surface == candidate.Form.Text
            && (KanaScoringHelpers.HasFrequencyMarker(candidate.Word.Priorities)
                || context.Surface.Length == 1))
        {
            readingMatchScore = 0;
        }

        // A name agreeing with Sudachi's name lemma on a bare kanji is circular (仁も義も礼も智も is the noun, not サトシ).
        if (readingMatchScore > 0 && !context.IsKanaSurface && context.Surface.Length == 1
            && !context.IsNameContext && candidate.Word.CachedPOS.Contains(PartOfSpeech.Name))
        {
            readingMatchScore = 0;
        }

        // A Sudachi 人名 guess on a bare kanji is a name reading; it must not vouch for a common noun (忍 → fern しのぶ).
        if (readingMatchScore > 0 && !context.IsKanaSurface && context.Surface.Length == 1
            && context.IsSudachiNameGuess && !candidate.Word.CachedPOS.Contains(PartOfSpeech.Name))
        {
            readingMatchScore = 0;
        }

        // Sudachi normalizes bare kanji to an okurigana lemma (改 → 改め); that reading must not beat 改 かい.
        if (readingMatchScore != 0 && SudachiNormalizedBareKanjiToOkurigana(context))
            readingMatchScore = 0;

        return readingMatchScore;
    }

    private static bool SudachiNormalizedBareKanjiToOkurigana(FormScoringContext context)
    {
        if (context.IsKanaSurface) return false;
        var surface = context.Surface;
        var normalized = context.NormalizedForm;
        if (string.IsNullOrEmpty(surface) || string.IsNullOrEmpty(normalized)) return false;
        if (normalized.Length <= surface.Length) return false;
        if (!normalized.StartsWith(surface, StringComparison.Ordinal)) return false;

        foreach (var c in surface)
            if (!JapaneseTextHelper.IsKanji(c)) return false;

        for (int i = surface.Length; i < normalized.Length; i++)
            if (!JapaneseTextHelper.IsHiragana(normalized[i])) return false;

        return true;
    }
}

internal static class ReadingPosHelper
{
    public static HashSet<string> GetPosForReading(JmDictWord word, byte readingIndex)
    {
        if (word.Definitions.Count == 0)
            return [];

        return word.Definitions
            .Where(d => AppliesToReading(word, d, readingIndex))
            .SelectMany(d => d.PartsOfSpeech)
            .ToHashSet();
    }

    // stagk and stagr share one flat index list, so a restriction only excludes forms of its own type (kanji vs kana).
    private static bool AppliesToReading(JmDictWord word, JmDictDefinition definition, byte readingIndex)
    {
        var restrictions = definition.RestrictedToReadingIndices;
        if (restrictions == null || restrictions.Count == 0)
            return true;

        var currentForm = word.Forms.Find(f => f.ReadingIndex == readingIndex);
        if (currentForm == null)
            return restrictions.Contains((short)readingIndex);

        bool sawSameAxis = false;
        foreach (var index in restrictions)
        {
            var form = word.Forms.Find(f => f.ReadingIndex == index);
            if (form == null || form.FormType != currentForm.FormType) continue;
            if (index == readingIndex) return true;
            sawSameAxis = true;
        }

        return !sawSameAxis;
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

    public static bool WordHasFormEquivalentTo(JmDictWord word, string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var f in word.Forms)
            if (f.Text == text || IsPureKanaScriptDifference(f.Text, text))
                return true;
        return false;
    }

    public static bool DictFormPointsToDifferentWord(FormScoringContext context, JmDictWord word) =>
        context.DictionaryForm is not null
        && context.DictionaryForm != context.Surface
        && !WordHasFormEquivalentTo(word, context.DictionaryForm);

    public static bool SurfaceEquivalentTo(string surface, string formText) =>
        surface == formText || IsPureKanaScriptDifference(surface, formText);

    public static bool IsInflectableVerbOrAdj(IEnumerable<string> pos)
    {
        foreach (var p in pos)
        {
            if (p is "adj-i" or "adj-ix") return true;
            if (p.Length > 0 && p[0] == 'v' && p is not ("vulg" or "vet" or "vidg")) return true;
        }
        return false;
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

    public static bool ContainsKatakana(string text)
    {
        foreach (char c in text)
        {
            if (c is >= 'ァ' and <= 'ヺ')
                return true;
        }

        return false;
    }

    public static bool IsPureKatakanaToken(string text)
    {
        bool hasKatakana = false;
        foreach (char c in text)
        {
            if (c is >= 'ァ' and <= 'ヺ') { hasKatakana = true; continue; }
            if (c is 'ー' or 'ヽ' or 'ヾ' or '・') continue;
            return false;
        }

        return hasKatakana;
    }

    public static bool HasFrequencyMarker(IReadOnlyList<string>? priorities, bool includeJiten = true)
    {
        if (priorities == null) return false;
        foreach (var p in priorities)
        {
            if (p is "ichi1" or "ichi2" or "news1" or "news2") return true;
            if (includeJiten && p == "jiten") return true;
            if (p.StartsWith("nf", StringComparison.Ordinal)) return true;
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

        // A noun+suffix hybrid (ゲ [n-suf, n]) must not beat a pure suffix (げ) via noun-particle synergy.
        if (context.SudachiPOS == PartOfSpeech.Suffix
            && PosMask.Has(candidate.Word.CachedPOSMask, PosMask.NounLike))
            score -= 50;

        // A DictionaryForm's godan row rules out other rows (こく v5k vs こる v5r).
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

        // Suru nouns conjugate only via する: 遺棄 いき must not match いきました (行く).
        if (context.SudachiPOS == PartOfSpeech.Verb
            && candidate.Form.FormType == JmDictFormType.KanaForm
            && candidate.Word.PartsOfSpeech.Contains("vs")
            && !candidate.Word.PartsOfSpeech.Any(static p =>
                p is "v1" or "v1-s" or "vs-c" or "vs-i" or "vs-s"
                or "v5a" or "v5b" or "v5g" or "v5k" or "v5k-s"
                or "v5m" or "v5n" or "v5r" or "v5r-i" or "v5s" or "v5t"
                or "v5u" or "v5u-s" or "v5uru" or "vk" or "vz" or "aux-v")
            // Exempts DictionaryForm = form + する, or キスさせてくれ resolves to 記す.
            && !(context.DictionaryFormHiragana is { Length: > 2 } dfh
                 && dfh.EndsWith("する", StringComparison.Ordinal)
                 && candidate.FormTextHiragana + "する" == dfh))
        {
            score -= 60;
        }

        // Sudachi 非自立可能 marks auxiliary use (te-form + くれる → 呉れる, not 暮れる).
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
            _ => null // る is ambiguous between v5r and v1
        };
    }
}