namespace Jiten.Api.Dtos;

public class UserProfileResponse
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public bool IsMediaListPublic { get; set; }
}
