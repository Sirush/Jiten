using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class ConfirmEmailChangeRequest
{
    [Required]
    public required string UserId { get; set; }
    [Required, EmailAddress]
    public required string NewEmail { get; set; }
    [Required]
    public required string Code { get; set; }
}
