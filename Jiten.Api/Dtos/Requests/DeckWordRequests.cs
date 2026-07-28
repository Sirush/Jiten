using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class AddDeckWordRequest
{
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
    [Range(1, int.MaxValue)]
    public int Occurrences { get; set; } = 1;
    [MaxLength(150)]
    public string? Sentence { get; set; }
    [MaxLength(150)]
    public string? Source { get; set; }
}

public class BatchAddDeckWordsRequest
{
    public List<AddDeckWordRequest> Words { get; set; } = new();
}

public class UpdateDeckWordRequest
{
    [Range(1, int.MaxValue)]
    public int Occurrences { get; set; }
}

public class DeckWordKey
{
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
}

public class BatchRemoveDeckWordsRequest
{
    public List<DeckWordKey> Words { get; set; } = new();
}
