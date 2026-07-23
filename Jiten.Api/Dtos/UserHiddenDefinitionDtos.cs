namespace Jiten.Api.Dtos;

public class UserHiddenDefinitionsDto
{
    public int WordId { get; set; }
    public List<int> HiddenIndices { get; set; } = new();
}

public class UpdateUserHiddenDefinitionsRequest
{
    public List<int> HiddenIndices { get; set; } = new();
}
