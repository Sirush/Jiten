namespace Jiten.Core.Data.Authentication;

public class RefreshToken
{
    public required string Token { get; set; }
    public required string JwtId { get; set; }
    public DateTime CreationDate { get; set; } = DateTime.UtcNow;
    public DateTime ExpiryDate { get; set; }
    public bool IsUsed { get; set; }
    public DateTime? UsedAt { get; set; }
    public bool IsRevoked { get; set; }
    public required string UserId { get; set; }
    public User? User { get; set; }
}