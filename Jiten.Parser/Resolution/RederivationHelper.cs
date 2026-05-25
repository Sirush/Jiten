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
    }

    public static RederiveState? CollectRederivationIds(
        WordInfo wordInfo,
        Dictionary<string, List<int>> lookups,
        Deconjugator deconjugator)
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

        if (wordInfo.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.Auxiliary
            or PartOfSpeech.NaAdjective or PartOfSpeech.Expression)
        {
            var normalizedText = KanaNormalizer.Normalize(KanaConverter.ToHiragana(text));
            var deconjugated = deconjugator.Deconjugate(normalizedText);

            var deconjMatches = new List<(DeconjugationForm form, List<int> ids)>();
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
                CandidateIds = candidateIds, DeconjMatches = deconjMatches, IsVerbPath = true
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
        bool skipPosFilter = false)
    {
        var allCandidates = new List<FormCandidate>();

        if (state.IsVerbPath)
        {
            var matchedWordIds = new HashSet<int>();
            foreach (var m in DeconjugationMatcher.FilterMatches(state.DeconjMatches!, wordCache, state.WordInfo.PartOfSpeech))
            {
                var forms = FormCandidateFactory.EnumerateCandidateForms(m.Word, m.Form.Text,
                    allowLooseLvmMatch: true, deconjForm: m.Form, surface: state.Text);
                allCandidates.AddRange(forms);
                matchedWordIds.Add(m.Word.WordId);
            }

            foreach (var id in (state.DirectIds ?? []).Concat(state.HiraganaIds ?? []).Distinct())
            {
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

        foreach (var id in state.CandidateIds)
        {
            if (!wordCache.TryGetValue(id, out var word)) continue;
            if (!skipPosFilter)
            {
                bool isNameWord = word.CachedPOS.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown);
                if (!PosMapper.IsJmDictCompatibleWithSudachi(word.CachedPOS, state.WordInfo.PartOfSpeech)
                    && !(state.WordInfo.IsPersonNameContext && isNameWord))
                    continue;
            }

            var forms = FormCandidateFactory.EnumerateCandidateForms(word, state.TextInHiragana, allowLooseLvmMatch: true, surface: state.Text);
            allCandidates.AddRange(forms);
        }

        return allCandidates;
    }
}
