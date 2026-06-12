namespace Jiten.Api.Dtos;

public class AccountInfoResponse
{
    public required string UserId { get; set; }
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public bool EmailConfirmed { get; set; }
    public bool HasPassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool ReceivesNewsletter { get; set; }
    public required string RateLimitTier { get; set; }
    public required IList<string> Roles { get; set; }
}
