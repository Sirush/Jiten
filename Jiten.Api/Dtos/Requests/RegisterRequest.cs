using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class RegisterRequest
{
    [Required, MaxLength(30)]
    public required string Username { get; set; }

    [Required, EmailAddress, MaxLength(100)]
    public required string Email { get; set; }

    [Required, MinLength(8), MaxLength(100)]
    public required string Password { get; set; }

    public required string RecaptchaResponse { get; set; }
    public bool TosAccepted { get; set; }
    public bool ReceiveNewsletter { get; set; }
}