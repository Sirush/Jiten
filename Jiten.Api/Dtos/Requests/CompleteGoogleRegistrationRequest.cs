using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class CompleteGoogleRegistrationRequest
{
    [Required, MaxLength(30)]
    public required string Username { get; set; }

    public required string TempToken { get; set; }
    public bool TosAccepted { get; set; }
    public bool ReceiveNewsletter { get; set; }
}