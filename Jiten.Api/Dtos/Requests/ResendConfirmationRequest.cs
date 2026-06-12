using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class ResendConfirmationRequest
{
    [Required, EmailAddress]
    public required string Email { get; set; }
    public required string RecaptchaResponse { get; set; }
}
