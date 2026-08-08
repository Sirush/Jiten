namespace Jiten.Core.Data;

public enum LegalDocument
{
    Cgu = 0,
    Cgv = 1
}

public enum LegalAcceptanceSource
{
    Registration = 0,
    Banner = 1,
    Checkout = 2
}

/// <summary>
/// Per-user, per-version evidence of legal document notice and acceptance. Rows are never updated after
/// acceptance/dismissal, never deleted, never reused across versions: the row is the evidence.
/// </summary>
public class UserLegalDocumentState
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public LegalDocument Document { get; set; }

    /// <summary>Document version identifier, e.g. "2026-08-08", matching the published document header.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Set once on first confirmed render; the user's 30-day notice clock starts here.</summary>
    public DateTime? NoticeShownAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    /// <summary>Permanent banner dismissal after the notice period elapsed without acceptance.</summary>
    public DateTime? DismissedAt { get; set; }

    public LegalAcceptanceSource Source { get; set; }
}
