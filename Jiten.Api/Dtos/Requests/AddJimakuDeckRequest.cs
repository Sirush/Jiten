namespace Jiten.Api.Dtos.Requests;

public class AddJimakuDeckRequest
{
    public required int JimakuId { get; set; }
    public required List<AddJimakuDeckFileDto> Files { get; set; }
}

public class AddJimakuDeckFileDto
{
    public required string Url { get; set; }
    public required string Name { get; set; }

    /// Optional subdeck title; falls back to "Episode {n}" when absent.
    public string? Title { get; set; }
}
