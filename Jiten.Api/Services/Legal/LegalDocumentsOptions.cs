namespace Jiten.Api.Services.Legal;

/// <summary>
/// Current legal document versions. The recorded acceptance version and the version the banner displays
/// must both come from here; <c>Jiten.Web/app/utils/legalDocuments.ts</c> must be bumped in step.
/// </summary>
public sealed class LegalDocumentsOptions
{
    public const string SectionName = "Legal:Documents";

    public string CguVersion { get; set; } = "2026-08-08";
    public string CgvVersion { get; set; } = "2026-08-08";

    /// <summary>Per-user notice period before an unaccepted CGU update binds by continued use (CGU art. 13.4).</summary>
    public int NoticePeriodDays { get; set; } = 30;
}
