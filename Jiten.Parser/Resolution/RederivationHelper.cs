using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Resolution;

internal static class RederivationHelper
{
    internal sealed class RederiveState
    {
        public required WordInfo WordInfo;
        public required string Text;
        public required string TextInHiragana;
        public List<int>? DirectIds;
        public List<int>? HiraganaIds;
        public required List<int> CandidateIds;
        public List<(DeconjugationForm form, List<int> ids)>? DeconjMatches;
        public bool IsVerbPath;
        public PartOfSpeech MatchPos;
    }

    /// <param name="asVerbStem">Deconjugates a noun-tagged token so its verb reading can compete (探し as 探す).</param>
    public static RederiveState? CollectRederivationIds(
        WordInfo wordInfo,
        Dictionary<string, List<int>> lookups,
        Deconjugator deconjugator,
        bool asVerbStem = false)
    {
        var text = wordInfo.Text;
        var textInHiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);

        if (wordInfo.PreMatchedCandidateWordIds is { Count: > 0 } constrainedIds)
        {
            return new RederiveState
            {
                WordInfo = wordInfo, Text = text, TextInHiragana = textInHiragana,
                CandidateIds = new List<int>(constrainedIds), IsVerbPath = false
            };
        }

        lookups.TryGetValue(text, out List<int>? directIds);
        lookups.TryGetValue(textInHiragana, out var hiraganaIds);

        var candidateIds = LookupCandidateCollector.CollectIds(lookups, text);

        if (asVerbStem || wordInfo.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary
            or PartOfSpeech.NaAdjective or PartOfSpeech.Expression)
        {
            var normalizedText = KanaNormalizer.Normalize(KanaConverter.ToHiragana(text));
            var deconjugated = deconjugator.Deconjugate(normalizedText);

            var deconjMatches = new List<(DeconjugationForm form, List<int> ids)>(deconjugated.Count);
            var seen = new HashSet<int>(candidateIds);

            for (int d = 0; d < deconjugated.Count; d++)
            {
                var form = deconjugated[d];
                if (lookups.TryGetValue(form.Text, out List<int>? lookup))
                {
                    deconjMatches.Add((form, lookup));
                    foreach (var id in lookup)
                        if (seen.Add(id))
                            candidateIds.Add(id);
                }
            }

            deconjMatches.Sort((a, b) => b.form.Text.Length.CompareTo(a.form.Text.Length));

            if (candidateIds.Count == 0)
                return null;

            return new RederiveState
            {
                WordInfo = wordInfo, Text = text, TextInHiragana = textInHiragana,
                DirectIds = directIds, HiraganaIds = hiraganaIds,
                CandidateIds = candidateIds, DeconjMatches = deconjMatches, IsVerbPath = true,
                MatchPos = asVerbStem ? PartOfSpeech.Verb : wordInfo.PartOfSpeech
            };
        }

        if (candidateIds.Count == 0)
            return null;

        return new RederiveState
        {
            WordInfo = wordInfo, Text = text, TextInHiragana = textInHiragana,
            DirectIds = directIds, HiraganaIds = hiraganaIds,
            CandidateIds = candidateIds, IsVerbPath = false
        };
    }

    public static List<FormCandidate> BuildCandidatesFromWords(
        RederiveState state,
        Dictionary<int, JmDictWord> wordCache,
        bool skipPosFilter = false,
        bool allowCounterCandidates = false)
    {
        var allCandidates = new List<FormCandidate>();

        if (state.IsVerbPath)
        {
            var matchedWordIds = new HashSet<int>();
            foreach (var m in DeconjugationMatcher.FilterMatches(state.DeconjMatches!, wordCache, state.MatchPos))
            {
                var forms = FormCandidateFactory.EnumerateCandidateForms(m.Word, m.Form.Text,
                    allowLooseLvmMatch: true, deconjForm: m.Form, surface: state.Text);
                allCandidates.AddRange(forms);
                matchedWordIds.Add(m.Word.WordId);
            }

            int directCount = state.DirectIds?.Count ?? 0;
            int surfaceIdCount = directCount + (state.HiraganaIds?.Count ?? 0);
            var seenSurfaceIds = new HashSet<int>(surfaceIdCount);
            for (int k = 0; k < surfaceIdCount; k++)
            {
                int id = k < directCount ? state.DirectIds![k] : state.HiraganaIds![k - directCount];
                if (!seenSurfaceIds.Add(id)) continue;
                if (matchedWordIds.Contains(id)) continue;
                if (!wordCache.TryGetValue(id, out var word)) continue;
                var posList = word.CachedPOS;
                if (!posList.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown))) continue;
                bool isPosIncompat = matchedWordIds.Count > 0 &&
                    !PosMapper.IsJmDictCompatibleWithSudachi(word.CachedPOS, state.WordInfo.PartOfSpeech);
                var forms = FormCandidateFactory.EnumerateCandidateForms(word, state.TextInHiragana, allowLooseLvmMatch: true, surface: state.Text);
                if (isPosIncompat)
                    foreach (var f in forms) f.IsPosIncompatibleDirectSurface = true;
                allCandidates.AddRange(forms);
            }

            return allCandidates;
        }

        bool namesOnly = !skipPosFilter && state.WordInfo.IsPersonNameContext && HasPureNameCandidate(state, wordCache);

        foreach (var id in state.CandidateIds)
        {
            if (!wordCache.TryGetValue(id, out var word)) continue;
            if (namesOnly && !IsPureNameEntry(word)) continue;
            if (!skipPosFilter)
            {
                bool isNameWord = word.CachedPOS.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown);
                // ctr fails the context-free POS check vs Suffix; allowCounterCandidates admits it after numerals (第二|話 → わ).
                if (!PosMapper.IsJmDictCompatibleWithSudachi(word.CachedPOS, state.WordInfo.PartOfSpeech)
                    && !(state.WordInfo.IsPersonNameContext && isNameWord)
                    && !(allowCounterCandidates && word.CachedPOS.Contains(PartOfSpeech.Counter)))
                    continue;
            }

            var forms = FormCandidateFactory.EnumerateCandidateForms(word, state.TextInHiragana, allowLooseLvmMatch: true, surface: state.Text);
            allCandidates.AddRange(forms);
        }

        return allCandidates;
    }

    private static bool IsPureNameEntry(JmDictWord word) =>
        word.CachedPOS.Contains(PartOfSpeech.Name) && word.CachedPOS.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown);

    /// <summary>The first pass resolves a person-name context among name entries only (忍さん); rescoring must not reopen it to common nouns.</summary>
    private static bool HasPureNameCandidate(RederiveState state, Dictionary<int, JmDictWord> wordCache)
    {
        foreach (var id in state.CandidateIds)
            if (wordCache.TryGetValue(id, out var word) && IsPureNameEntry(word))
                return true;
        return false;
    }
}
