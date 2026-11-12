using System.Text.Json.Serialization;

namespace Jiten.Api.Services.WordCheck;

public enum WordCheckAnswer : byte
{
    Know = 0,
    NotSure = 1,
    No = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WordCheckLevel
{
    Starting,
    Beginner,
    Intermediate,
    Advanced
}

public readonly record struct WordCheckObservation(int Rank, bool Known);

public static class WordCheckEngine
{
    public const int WordsPerRound = 8;
    public const int Rounds = 4;
    public const int SoftFailTrapHits = 3;
    public const int RealWordsBeforeTrap = 3;
    public const int VerificationWords = 10;
    public const int MaxCutoffRank = 10000;
    public const int MaxProbeRank = 11500;

    /// <summary>The first rounds carry a second pseudo-word, so overclaiming shows up before the estimate settles.</summary>
    private const int DoubleTrapRounds = 2;

    private const int LowestWindowCentre = 250;

    public static int ProbeQuestions => Enumerable.Range(0, Rounds).Sum(round => WordsPerRound + TrapsInRound(round));

    public static int TrapsInRound(int round) => round < DoubleTrapRounds ? 2 : 1;

    public static int StartRank(WordCheckLevel? level) => level switch
    {
        WordCheckLevel.Beginner => 500,
        WordCheckLevel.Intermediate => 2000,
        WordCheckLevel.Advanced => 5000,
        _ => 1000
    };

    /// <summary>Wide on purpose: the first round only has to place the reader roughly, whatever level they picked.</summary>
    public static (int Min, int Max) FirstWindow(WordCheckLevel? level)
    {
        var start = StartRank(level);
        return (start / 3, Math.Min(MaxProbeRank, (int)(start * 2.2)));
    }

    /// <summary>Later rounds ask around the running estimate, where each answer moves the result most.</summary>
    public static (int Min, int Max) NextWindow(int cutoff)
    {
        if (cutoff >= MaxCutoffRank) return (MaxCutoffRank / 2, MaxProbeRank);

        var centre = Math.Max(cutoff, LowestWindowCentre);
        return ((int)(centre * 0.5), Math.Min(MaxProbeRank, (int)(centre * 1.7)));
    }

    public static (int Min, int Max) VerificationWindow(int cutoff) => ((int)(cutoff * 0.6), cutoff);

    /// <summary>Real-word counts after which a trap is shown: never before <see cref="RealWordsBeforeTrap"/> real words and never two traps in a row.</summary>
    public static List<int> TrapSlots(int realWords, int traps, Random random)
    {
        var first = Math.Min(RealWordsBeforeTrap, realWords);
        return Enumerable.Range(first, realWords - first + 1)
                         .OrderBy(_ => random.Next())
                         .Take(traps)
                         .Order()
                         .ToList();
    }

    /// <summary>Share of trap words claimed, used as the false-alarm rate in the guessing correction.</summary>
    public static double FalseAlarmRate(int trapsShown, int trapsClaimed)
        => trapsShown == 0 ? 0 : Math.Clamp((double)trapsClaimed / trapsShown, 0, 0.95);
}
