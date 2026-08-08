using Jiten.Api.Services;
using Jiten.Api.Services.Legal;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/legal")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("fixed")]
public class LegalController(
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IOptions<LegalDocumentsOptions> options) : ControllerBase
{
    public record DocumentRequest(string? Document);

    private LegalDocumentsOptions Options => options.Value;

    private string CurrentVersion(LegalDocument document) =>
        document == LegalDocument.Cgu ? Options.CguVersion : Options.CgvVersion;

    private static bool TryParseDocument(string? value, out LegalDocument document)
    {
        document = LegalDocument.Cgu;
        if (string.Equals(value, "cgu", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(value, "cgv", StringComparison.OrdinalIgnoreCase))
        {
            document = LegalDocument.Cgv;
            return true;
        }

        return false;
    }

    [HttpGet("status")]
    public async Task<IResult> GetStatus()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var cgu = await GetRowAsync(userId, LegalDocument.Cgu);
        var cgv = await GetRowAsync(userId, LegalDocument.Cgv);

        var now = DateTime.UtcNow;
        var effectiveDate = cgu?.NoticeShownAt?.AddDays(Options.NoticePeriodDays);
        string? phase = null;
        if (cgu?.AcceptedAt is null && cgu?.DismissedAt is null)
            phase = effectiveDate is null || now < effectiveDate ? "notice" : "elapsed";

        return Results.Ok(new
        {
            cgu = new
            {
                version = Options.CguVersion,
                accepted = cgu?.AcceptedAt is not null,
                dismissed = cgu?.DismissedAt is not null,
                noticeShownAt = cgu?.NoticeShownAt,
                effectiveDate,
                phase
            },
            cgv = new
            {
                version = Options.CgvVersion,
                accepted = cgv?.AcceptedAt is not null
            }
        });
    }

    /// <summary>Starts the user's notice clock on first confirmed banner render. Idempotent: later calls never move it.</summary>
    [HttpPost("notice-shown")]
    public async Task<IResult> NoticeShown([FromBody] DocumentRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!TryParseDocument(request.Document, out var document))
            return Results.BadRequest(new { error = "Unknown document." });

        var row = await GetRowAsync(userId, document);
        if (row is null)
        {
            row = new UserLegalDocumentState
            {
                UserId = userId,
                Document = document,
                Version = CurrentVersion(document),
                NoticeShownAt = DateTime.UtcNow,
                Source = LegalAcceptanceSource.Banner
            };
            userContext.UserLegalDocumentStates.Add(row);
            await SaveIgnoringDuplicateAsync();
        }
        else if (row.NoticeShownAt is null)
        {
            row.NoticeShownAt = DateTime.UtcNow;
            await userContext.SaveChangesAsync();
        }

        return Results.Ok();
    }

    /// <summary>
    /// Records acceptance of the current version. A stale client posting a superseded version is rejected so
    /// acceptance can never be recorded for a document the user was not shown.
    /// </summary>
    [HttpPost("accept")]
    public async Task<IResult> Accept([FromBody] AcceptRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!TryParseDocument(request.Document, out var document))
            return Results.BadRequest(new { error = "Unknown document." });

        if (!string.Equals(request.Version, CurrentVersion(document), StringComparison.Ordinal))
            return Results.Conflict(new { error = "This document has been updated since it was displayed. Please reload." });

        var now = DateTime.UtcNow;
        var row = await GetRowAsync(userId, document);
        if (row is null)
        {
            row = new UserLegalDocumentState
            {
                UserId = userId,
                Document = document,
                Version = CurrentVersion(document),
                NoticeShownAt = now,
                AcceptedAt = now,
                Source = document == LegalDocument.Cgv ? LegalAcceptanceSource.Checkout : LegalAcceptanceSource.Banner
            };
            userContext.UserLegalDocumentStates.Add(row);
            await SaveIgnoringDuplicateAsync();
            row = await GetRowAsync(userId, document);
        }

        if (row is not null && row.AcceptedAt is null)
        {
            row.NoticeShownAt ??= now;
            row.AcceptedAt = now;
            await userContext.SaveChangesAsync();
        }

        return Results.Ok();
    }

    /// <summary>Permanent banner dismissal, only once the user's own notice period has elapsed without acceptance.</summary>
    [HttpPost("dismiss")]
    public async Task<IResult> Dismiss([FromBody] DocumentRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!TryParseDocument(request.Document, out var document))
            return Results.BadRequest(new { error = "Unknown document." });

        var row = await GetRowAsync(userId, document);
        if (row is null || row.NoticeShownAt is null)
            return Results.BadRequest(new { error = "Nothing to dismiss." });

        if (row.AcceptedAt is not null || row.DismissedAt is not null)
            return Results.Ok();

        if (DateTime.UtcNow < row.NoticeShownAt.Value.AddDays(Options.NoticePeriodDays))
            return Results.BadRequest(new { error = "The notice period has not elapsed yet." });

        row.DismissedAt = DateTime.UtcNow;
        await userContext.SaveChangesAsync();
        return Results.Ok();
    }

    public record AcceptRequest(string? Document, string? Version);

    private Task<UserLegalDocumentState?> GetRowAsync(string userId, LegalDocument document) =>
        userContext.UserLegalDocumentStates
                   .FirstOrDefaultAsync(s => s.UserId == userId && s.Document == document && s.Version == CurrentVersion(document));

    /// <summary>Two devices racing on first insert both succeed logically; the loser's duplicate is discarded.</summary>
    private async Task SaveIgnoringDuplicateAsync()
    {
        try
        {
            await userContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            userContext.ChangeTracker.Clear();
        }
    }
}
