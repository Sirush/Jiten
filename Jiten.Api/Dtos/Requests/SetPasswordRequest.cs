using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class SetPasswordRequest
{
    [Required, MinLength(10)]
    public required string NewPassword { get; set; }
}
