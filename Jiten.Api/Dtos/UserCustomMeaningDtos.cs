namespace Jiten.Api.Dtos;

public class UserCustomMeaningDto
{
    public int WordId { get; set; }
    public string Text { get; set; } = "";
}

public class UpsertUserCustomMeaningRequest
{
    public required string Text { get; set; }
}
