using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Resolution;

internal static class DeconjugationMatcher
{
    public readonly record struct DeconjMatch(JmDictWord Word, DeconjugationForm Form);

    /// Filters deconjugation candidates against POS compatibility and deconjugation tag validation.
    public static List<DeconjMatch> FilterMatches(
        IEnumerable<(DeconjugationForm form, List<int> ids)> candidates,
        Dictionary<int, JmDictWord> wordCache,
        PartOfSpeech sudachiPos,
        bool strictPosCheck = true)
    {
        var matches = new List<DeconjMatch>();
        foreach (var (form, ids) in candidates)
        {
            var lastTag = PosMapper.GetLastValidatableDeconjTag(form.Tags);
            foreach (var id in ids)
            {
                if (!wordCache.TryGetValue(id, out var word)) continue;

                if (strictPosCheck)
                {
                    if (!PosMapper.IsJmDictCompatibleWithSudachi(word.CachedPOS, sudachiPos))
                        continue;
                }
                else
                {
                    var posList = word.CachedPOS;
                    if (!posList.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown)))
                        continue;
                }

                if (lastTag != null && !PosMapper.IsDeconjTagCompatibleWithJmDict(lastTag, word.PartsOfSpeech))
                    continue;

                matches.Add(new(word, form));
            }
        }
        return matches;
    }
}
