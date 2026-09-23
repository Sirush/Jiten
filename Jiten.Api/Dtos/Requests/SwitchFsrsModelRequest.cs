namespace Jiten.Api.Dtos.Requests;

public class SwitchFsrsModelRequest
{
    /// <summary>FSRS major version to switch to: 6 or 7.</summary>
    public int Version { get; set; }
}
