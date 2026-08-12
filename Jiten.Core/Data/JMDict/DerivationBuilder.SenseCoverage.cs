namespace Jiten.Core.Data.JMDict;

public static partial class DerivationBuilder
{
    /// <summary>English-gloss coherence check that can only
    /// demote, never approve, and binds in every category unless a committed override says otherwise. It weighs
    /// an entry's whole sense list rather than the matched reading's, which can only over-demote.</summary>
    private static class SenseCoverage
    {
        /// <param name="Signal">Why the rule landed where it did, for the classification pass to weigh.</param>
        /// <param name="Verdict">The automatic outcome before any override.</param>
        /// <param name="NeedsReview">Polysemy-wide or fossilised, both of which the gloss check reads badly.</param>
        /// <param name="IsTransitivitySplit">A vi/vt pair mislabelled as a potential.</param>
        internal sealed record Result(DerivationVerdict Verdict, bool NeedsReview, bool IsTransitivitySplit,
                                      string Signal);

        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "the", "and", "for", "with", "that", "this", "from", "into", "onto", "one", "ones", "someone",
            "something", "somebody", "etc", "esp", "usu", "used", "use", "make", "made", "become", "becoming",
            "have", "has", "having", "his", "her", "their", "its", "not", "but", "who", "which", "when",
            "such", "also", "very", "more", "most", "each", "other", "another", "person", "thing", "state",
            "being", "about", "over", "out", "off", "all", "any", "may", "can", "will", "would"
        };

        /// <summary>Suffixes a gloss noun/verb can carry over its base word; stripped longest-first so
        /// "brightness"/"bright" and "arrival"/"arrive" collapse to the same stem.</summary>
        private static readonly string[] Suffixes =
            ["iness", "ness", "ality", "ility", "ity", "ance", "ence", "ment", "tion", "sion", "ing", "ers",
             "er", "ed", "es", "s", "ly", "al"];

        /// <summary>Glosses a category is expected to produce, so a derived sense that reads exactly as the
        /// grammar point predicts counts as covered even when it shares no stem with the base.</summary>
        private static readonly Dictionary<DerivationCategory, string[]> Expectations = new()
        {
            [DerivationCategory.Potential] = ["can ", "able to", "-able", "possib"],
            [DerivationCategory.LexicalPassive] = ["to be ", "being "],
            [DerivationCategory.Garu] = ["to feel", "to act", "to show signs", "person who"],
            [DerivationCategory.Gari] = ["person who", "sensitive to", "to feel"],
            [DerivationCategory.HonorificPrefix] = ["honorific", "polite", "hon.", "pol."],
            [DerivationCategory.SaNominal] = ["-ness", "degree of", "extent"],
            [DerivationCategory.NaSaNominal] = ["-ness", "degree of", "extent"],
            [DerivationCategory.MiNominal] = ["-ness", "quality of", "sense of"],
            [DerivationCategory.KuAdverb] = ["-ly", "in a "],
            [DerivationCategory.NiAdverb] = ["-ly", "in a "],
            [DerivationCategory.TeAdverb] = ["-ly", "in a "],
            [DerivationCategory.Sugiru] = ["too ", "excess", "over"],
            [DerivationCategory.Ppoi] = ["-ish", "-like", "tend", "apt to"],
            [DerivationCategory.Gachi] = ["apt to", "tend", "prone", "often"],
            [DerivationCategory.Gimi] = ["touch of", "slight", "-ish", "feeling"],
            [DerivationCategory.MeModerate] = ["somewhat", "rather", "-ish", "slight"],
            [DerivationCategory.Sou] = ["look", "seem", "appear"],
            [DerivationCategory.GeAdjective] = ["look", "seem", "appear", "-ly"],
            [DerivationCategory.MaIntensifier] = ["pure", "complete", "dead ", "deep", "bright"],
            [DerivationCategory.CausativeDoublet] = ["to make", "to let", "to have "],
            [DerivationCategory.ZuruJiru] = [],
            [DerivationCategory.ClassicalAdjective] = ["archaic", "literary"]
        };

        public static Result Analyse(WordEntry baseWord, WordEntry derivedWord, DerivationCategory category)
        {
            var baseVocab = Vocabulary(baseWord);
            var derivedVocab = Vocabulary(derivedWord);
            var expectations = Expectations.GetValueOrDefault(category, []);

            var derivedCovered = CoveredSenses(derivedWord, baseVocab, expectations, out var derivedTotal);
            var baseCovered = CoveredSenses(baseWord, derivedVocab, [], out var baseTotal);

            var derivedFull = derivedTotal > 0 && derivedCovered == derivedTotal;
            var baseFull = baseTotal > 0 && baseCovered == baseTotal;

            var verdict = derivedFull
                ? baseFull ? DerivationVerdict.Bidirectional : DerivationVerdict.OneWayOnly
                : DerivationVerdict.Exclude;

            var polysemyWide = derivedTotal > 0 && derivedCovered * 2 < derivedTotal;
            var fossilised = derivedWord.FrequencyRank > 0 && baseWord.FrequencyRank > 0 &&
                             derivedWord.FrequencyRank * 3 < baseWord.FrequencyRank && !derivedFull;
            var split = IsTransitivitySplit(baseWord, derivedWord, category);

            var signals = new List<string>
            {
                $"derived {derivedCovered}/{derivedTotal} senses covered by base",
                $"base {baseCovered}/{baseTotal} senses covered by derived"
            };
            if (polysemyWide) signals.Add("derived polysemy wider than the grammar point");
            if (fossilised)
                signals.Add($"derived is far more frequent (rank {derivedWord.FrequencyRank} vs base {baseWord.FrequencyRank})");
            if (split) signals.Add("target is vi against a vt base with no ability gloss");

            return new Result(verdict, polysemyWide || fossilised, split, string.Join("; ", signals));
        }

        private static bool IsTransitivitySplit(WordEntry baseWord, WordEntry derivedWord, DerivationCategory category)
        {
            if (category != DerivationCategory.Potential) return false;
            if (!HasTag(derivedWord, "vi") || !HasTag(baseWord, "vt")) return false;

            return !derivedWord.Senses.Any(s => s.Glosses.Any(
                                                    g => g.Contains("can ", StringComparison.OrdinalIgnoreCase) ||
                                                         g.Contains("able", StringComparison.OrdinalIgnoreCase)));
        }

        private static bool HasTag(WordEntry word, string tag) => word.Senses.Any(s => s.Pos.Contains(tag));

        private static int CoveredSenses(WordEntry word, HashSet<string> otherVocabulary, string[] expectations,
                                          out int total)
        {
            total = 0;
            var covered = 0;

            foreach (var sense in word.Senses)
            {
                if (sense.Glosses.Length == 0) continue;
                total++;

                if (sense.Glosses.Any(g => expectations.Any(e => g.Contains(e, StringComparison.OrdinalIgnoreCase))))
                {
                    covered++;
                    continue;
                }

                if (sense.Glosses.SelectMany(ContentWords).Any(otherVocabulary.Contains))
                    covered++;
            }

            return covered;
        }

        private static HashSet<string> Vocabulary(WordEntry word)
        {
            var vocabulary = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sense in word.Senses)
            foreach (var gloss in sense.Glosses)
            foreach (var token in ContentWords(gloss))
                vocabulary.Add(token);

            return vocabulary;
        }

        private static IEnumerable<string> ContentWords(string gloss)
        {
            foreach (var raw in gloss.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.ToLowerInvariant();
                if (token.Length < 3 || StopWords.Contains(token)) continue;

                yield return Stem(token);
            }
        }

        private static readonly char[] SplitChars =
            [' ', ',', ';', '.', '(', ')', '\'', '"', '-', '/', ':', '!', '?', '’', '“', '”'];

        private static string Stem(string token)
        {
            foreach (var suffix in Suffixes)
            {
                if (token.Length - suffix.Length < 3 || !token.EndsWith(suffix, StringComparison.Ordinal)) continue;
                return token[..^suffix.Length];
            }

            return token;
        }
    }
}
