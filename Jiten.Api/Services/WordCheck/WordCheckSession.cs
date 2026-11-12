using System.Text.Json.Serialization;

namespace Jiten.Api.Services.WordCheck;

public enum WordCheckItemKind : byte
{
    Word = 0,
    Pseudo = 1,
    Verify = 3
}

public enum WordCheckPhase : byte
{
    Probe = 0,
    Verify = 1,
    Failed = 3
}

public sealed class WordCheckItem
{
    public int Id { get; set; }
    public WordCheckItemKind Kind { get; set; }
    public int Round { get; set; }
    public int Rank { get; set; }
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Text { get; set; } = "";
    public WordCheckAnswer? Answer { get; set; }
}

public sealed class WordCheckSession : ICheckSession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public WordCheckLevel? Level { get; set; }
    public WordCheckPhase Phase { get; set; }
    public List<WordCheckItem> Items { get; set; } = [];
    public int RoundsAsked { get; set; }
    public int Cutoff { get; set; }

    [JsonIgnore]
    public IEnumerable<WordCheckItem> Pending => Items.Where(i => i.Answer == null);

    [JsonIgnore]
    public int Answered => Items.Count(i => i.Answer != null && i.Kind != WordCheckItemKind.Verify);

    public List<WordCheckObservation> Observations()
        => Items.Where(i => i.Kind != WordCheckItemKind.Pseudo && i.Answer != null)
                .Select(i => new WordCheckObservation(i.Rank, i.Answer == WordCheckAnswer.Know))
                .ToList();

    public (int Shown, int Claimed) Traps()
    {
        var traps = Items.Where(i => i.Kind == WordCheckItemKind.Pseudo && i.Answer != null).ToList();
        return (traps.Count, traps.Count(i => i.Answer == WordCheckAnswer.Know));
    }
}
