using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class ChangeEmailRequest
{
    [Required, EmailAddress]
    public required string NewEmail { get; set; }
    public string? CurrentPassword { get; set; }
}
