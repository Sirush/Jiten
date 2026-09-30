namespace Jiten.Api.Dtos.Requests;

/// <summary>The <c>sso</c> and <c>sig</c> query parameters the forum put on its redirect to /community/sso, forwarded unchanged.</summary>
public class DiscourseSsoRequest
{
    public required string Sso { get; set; }
    public required string Sig { get; set; }
}
