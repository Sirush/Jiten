namespace Jiten.Core.Data.JMDict;

public class JmDictWord
{
    public int WordId { get; set; }
    public List<string> Readings { get; set; } = new();
    public List<string> ReadingsFurigana { get; set; } = new();
    public List<JmDictReadingType> ReadingTypes { get; set; } = new();
    public List<string>? ObsoleteReadings { get; set; } = new();
    public List<string> PartsOfSpeech { get; set; } = new();
    public List<JmDictDefinition> Definitions { get; set; } = new();
    public List<JmDictLookup> Lookups { get; set; } = new();
    public List<int>? PitchAccents { get; set; } = new();
    public List<string>? Priorities { get; set; } = new();

    public int GetPriorityScore(bool isKana)
    {
        if (Priorities == null || !Priorities.Any())
            return 0;

        int score = 0;
        if (Priorities.Contains("ichi1"))
            score += 20;

        if (Priorities.Contains("ichi2"))
            score += 10;

        if (Priorities.Contains("news1"))
            score += 15;

        if (Priorities.Contains("news2"))
            score += 10;

        if (Priorities.Contains("gai1") || Priorities.Contains("gai2"))
            score += 5;

        var nf = Priorities.FirstOrDefault(p => p.StartsWith("nf"));
        if (nf != null)
        {
            var nfRank = int.Parse(nf[2..]);
            score += Math.Max(0, 5 - (int)Math.Round(nfRank / 10f));
        }

        if (score == 0)
        {
            if (Priorities.Contains("spec1"))
                score += 15;

            if (Priorities.Contains("spec2"))
                score += 5;
        }

        if (PartsOfSpeech.Contains("uk"))
        {
            if (isKana)
                score += 10;
            else
                score -= 10;
        }

        return score;
    }
}