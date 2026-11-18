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
            if (lastTag != null && !StemTextFitsTag(form.Text, lastTag, form.Process.Length))
                continue;

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

    /// <summary>Stem tags validate against every verb, so a multi-step chain that lands on a dictionary-shaped stem (おくれている → おく) is a dead end; a one-step stem keeps Sudachi's fused verb + particle (あるで → ある).</summary>
    private static bool StemTextFitsTag(string text, string tag, int steps)
    {
        if (text.Length == 0)
            return true;

        char last = text[^1];
        if (tag.StartsWith("stem-te", StringComparison.Ordinal))
            return last is 'て' or 'で';
        if (tag.StartsWith("stem-ren", StringComparison.Ordinal) && steps > 1)
            return last is not ('う' or 'く' or 'ぐ' or 'す' or 'つ' or 'ぬ' or 'ぶ' or 'む' or 'る');
        return true;
    }
}
