using System.Security.Cryptography;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Services;
using Jiten.Api.Services.Stripe;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    private const string PromoCodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int PromoCodeLength = 10;

    // ---- Reward grants ----

    [HttpPost("jiten-plus/grant")]
    public async Task<IActionResult> GrantJitenPlus(
        [FromBody] GrantJitenPlusRequest request,
        [FromServices] NotificationService notificationService,
        [FromServices] IEmailService emailService,
        [FromServices] IJitenPlusService jitenPlus,
        [FromServices] IStripeGateway stripeGateway)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var idOrName = request.UserIdOrName.Trim();
        var user = await userContext.Users
                                    .FirstOrDefaultAsync(u => u.Id == idOrName || u.UserName == idOrName);
        if (user is null)
            return NotFound(new { Message = "User not found" });

        var kind = request.Kind.Trim().ToLowerInvariant();
        string summary;

        if (kind == "lifetime")
        {
            if (user.IsLifetime)
                return BadRequest(new { Message = "User already has lifetime Jiten+." });

            user.IsLifetime = true;
            user.LifetimeSource = LifetimeSource.ContributorGrant;

            var subToCancel = user.StripeSubscriptionActive ? user.StripeSubscriptionId : null;
            await userContext.SaveChangesAsync();
            if (!string.IsNullOrEmpty(subToCancel))
                await stripeGateway.CancelSubscriptionImmediatelyAsync(subToCancel);

            summary = "You've been given Jiten+ lifetime access as a thank-you.";
        }
        else if (kind == "days")
        {
            if (request.Days is not > 0)
                return BadRequest(new { Message = "Days must be greater than 0 for a days grant." });

            userContext.UserPromoCredits.Add(new UserPromoCredit
            {
                UserId = user.Id,
                PromoCodeId = null,
                Source = PromoCreditSource.AdminGrant,
                GrantsFullTier = request.GrantsFullTier,
                RemainingDays = request.Days.Value,
                GrantedAt = DateTime.UtcNow,
                ThankYouMessage = string.IsNullOrWhiteSpace(request.ThankYouMessage) ? null : request.ThankYouMessage.Trim()
            });
            await userContext.SaveChangesAsync();
            summary = $"You've been given {request.Days.Value} day{(request.Days.Value == 1 ? "" : "s")} of Jiten+ as a thank-you.";
        }
        else
        {
            return BadRequest(new { Message = "Kind must be 'days' or 'lifetime'." });
        }

        jitenPlus.InvalidateTier(user.Id);

        // Deliver the thank-you both in-app and by email, each carrying the personal message. The blank line
        // between the summary and the personal note is a markdown paragraph break the clients render (and it
        // keeps the admin's own line breaks intact).
        var personal = string.IsNullOrWhiteSpace(request.ThankYouMessage) ? null : request.ThankYouMessage.Trim();
        var notifMessage = personal is null ? summary : $"{summary}\n\n{personal}";
        if (notifMessage.Length > 500)
            notifMessage = notifMessage[..500];

        await notificationService.Notify(user.Id, NotificationType.General, "A Jiten+ gift for you", notifMessage, "/settings");
        await emailService.SendJitenPlusGrantAsync(user.Email, kind == "lifetime", request.Days, personal);

        logger.LogInformation("Admin granted Jiten+ ({Kind}) to user {UserId}", kind, user.Id);
        return Ok(new { Message = "Grant delivered.", UserId = user.Id, UserName = user.UserName, Kind = kind });
    }

    [HttpGet("jiten-plus/grants")]
    public async Task<IActionResult> GetJitenPlusGrants()
    {
        var dayGrants = await userContext.UserPromoCredits.AsNoTracking()
            .Where(c => c.Source == PromoCreditSource.AdminGrant)
            .OrderByDescending(c => c.GrantedAt)
            .Join(userContext.Users, c => c.UserId, u => u.Id, (c, u) => new
            {
                type = "days",
                c.UserId,
                userName = u.UserName,
                grantedAt = (DateTime?)c.GrantedAt,
                days = (int?)c.RemainingDays,
                c.GrantsFullTier,
                remainingDays = (int?)c.RemainingDays,
                c.ThankYouMessage
            })
            .ToListAsync();

        // Contributor lifetime grants aren't credits; the User flag is the only persisted record (no timestamp/message).
        var lifetimeGrants = await userContext.Users.AsNoTracking()
            .Where(u => u.IsLifetime && u.LifetimeSource == LifetimeSource.ContributorGrant)
            .OrderBy(u => u.UserName)
            .Select(u => new
            {
                type = "lifetime",
                UserId = u.Id,
                userName = u.UserName,
                grantedAt = (DateTime?)null,
                days = (int?)null,
                GrantsFullTier = true,
                remainingDays = (int?)null,
                ThankYouMessage = (string?)null
            })
            .ToListAsync();

        return Ok(new { dayGrants, lifetimeGrants });
    }

    [HttpPost("jiten-plus/revoke-lifetime")]
    public async Task<IActionResult> RevokeLifetime(
        [FromBody] RevokeLifetimeRequest request,
        [FromServices] IJitenPlusService jitenPlus)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var idOrName = request.UserIdOrName.Trim();
        var user = await userContext.Users
                                    .FirstOrDefaultAsync(u => u.Id == idOrName || u.UserName == idOrName);
        if (user is null)
            return NotFound(new { Message = "User not found" });

        if (!user.IsLifetime)
            return BadRequest(new { Message = "User does not have lifetime access." });

        // Only an admin oops-fix is revocable. A purchased lifetime is money the user paid — refund it in
        // Stripe instead; revoking here would silently strip paid-for access.
        if (user.LifetimeSource != LifetimeSource.ContributorGrant)
            return BadRequest(new { Message = "This lifetime was purchased and cannot be revoked here. Issue a refund in Stripe instead." });

        user.IsLifetime = false;
        user.LifetimeSource = null;
        await userContext.SaveChangesAsync();

        jitenPlus.InvalidateTier(user.Id);

        // No notification/email: this is an administrative correction of a mistaken grant, not a user-facing event.
        logger.LogInformation("Admin revoked contributor lifetime Jiten+ from user {UserId}", user.Id);
        return Ok(new { Message = "Lifetime access revoked.", UserId = user.Id, UserName = user.UserName });
    }

    // ---- Promo code management ----

    [HttpPost("promo-codes")]
    public async Task<IActionResult> CreatePromoCode([FromBody] CreatePromoCodeRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        string code;
        if (!string.IsNullOrWhiteSpace(request.Code))
        {
            code = request.Code.Trim().ToUpperInvariant();
            if (!IsValidCodeFormat(code))
                return BadRequest(new { Message = "Code must be 8-12 uppercase letters/digits." });
            if (await userContext.PromoCodes.AnyAsync(p => p.Code == code))
                return BadRequest(new { Message = "A code with that value already exists." });
        }
        else
        {
            code = await GenerateUniqueCodeAsync(new HashSet<string>());
        }

        var promo = new PromoCode
        {
            Code = code,
            Description = request.Description?.Trim(),
            DurationDays = request.DurationDays,
            MaxUses = request.MaxUses,
            ExpiresAt = ToUtc(request.ExpiresAt),
            GrantsFullTier = request.GrantsFullTier,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        userContext.PromoCodes.Add(promo);
        await userContext.SaveChangesAsync();

        logger.LogInformation("Admin created promo code {Code}", promo.Code);
        return Ok(ToPromoDto(promo, 0));
    }

    [HttpGet("promo-codes")]
    public async Task<IActionResult> GetPromoCodes()
    {
        var codes = await userContext.PromoCodes.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                p.CodeId,
                p.Code,
                p.Description,
                p.DurationDays,
                p.MaxUses,
                p.CurrentUses,
                p.ExpiresAt,
                p.CreatedAt,
                p.IsActive,
                p.GrantsFullTier,
                redemptions = userContext.UserPromoCredits.Count(c => c.PromoCodeId == p.CodeId)
            })
            .ToListAsync();

        return Ok(codes);
    }

    [HttpPut("promo-codes/{id:int}")]
    public async Task<IActionResult> UpdatePromoCode(int id, [FromBody] UpdatePromoCodeRequest request)
    {
        var promo = await userContext.PromoCodes.FirstOrDefaultAsync(p => p.CodeId == id);
        if (promo is null)
            return NotFound(new { Message = "Promo code not found" });

        if (request.Description is not null)
            promo.Description = request.Description.Trim();
        if (request.MaxUses.HasValue)
            promo.MaxUses = request.MaxUses.Value;
        if (request.ExpiresAt.HasValue)
            promo.ExpiresAt = ToUtc(request.ExpiresAt);
        if (request.IsActive.HasValue)
            promo.IsActive = request.IsActive.Value;
        if (request.GrantsFullTier.HasValue)
            promo.GrantsFullTier = request.GrantsFullTier.Value;

        await userContext.SaveChangesAsync();
        logger.LogInformation("Admin updated promo code {Code}", promo.Code);

        var redemptions = await userContext.UserPromoCredits.CountAsync(c => c.PromoCodeId == promo.CodeId);
        return Ok(ToPromoDto(promo, redemptions));
    }

    [HttpDelete("promo-codes/{id:int}")]
    public async Task<IActionResult> DeletePromoCode(int id)
    {
        var promo = await userContext.PromoCodes.FirstOrDefaultAsync(p => p.CodeId == id);
        if (promo is null)
            return NotFound(new { Message = "Promo code not found" });

        // Soft delete: deactivate so existing redemptions and their FK stay intact.
        promo.IsActive = false;
        await userContext.SaveChangesAsync();
        logger.LogInformation("Admin deactivated promo code {Code}", promo.Code);
        return Ok(new { Message = "Promo code deactivated." });
    }

    [HttpGet("promo-codes/{id:int}/usage")]
    public async Task<IActionResult> GetPromoCodeUsage(int id)
    {
        var promo = await userContext.PromoCodes.AsNoTracking().FirstOrDefaultAsync(p => p.CodeId == id);
        if (promo is null)
            return NotFound(new { Message = "Promo code not found" });

        var redemptions = await userContext.UserPromoCredits.AsNoTracking()
            .Where(c => c.PromoCodeId == id)
            .OrderByDescending(c => c.GrantedAt)
            .Join(userContext.Users, c => c.UserId, u => u.Id, (c, u) => new
            {
                c.UserId,
                userName = u.UserName,
                redeemedAt = c.GrantedAt,
                c.RemainingDays,
                c.FullyUsedAt
            })
            .ToListAsync();

        return Ok(new
        {
            promo.CodeId,
            promo.Code,
            promo.MaxUses,
            promo.CurrentUses,
            redemptions
        });
    }

    [HttpPost("promo-codes/bulk-generate")]
    public async Task<IActionResult> BulkGeneratePromoCodes([FromBody] BulkGeneratePromoCodesRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var generated = new HashSet<string>();
        var codes = new List<PromoCode>();
        var now = DateTime.UtcNow;
        var expires = ToUtc(request.ExpiresAt);
        var description = request.Description?.Trim();

        for (var i = 0; i < request.Count; i++)
        {
            var code = await GenerateUniqueCodeAsync(generated);
            generated.Add(code);
            codes.Add(new PromoCode
            {
                Code = code,
                Description = description,
                DurationDays = request.DurationDays,
                MaxUses = request.MaxUses,
                ExpiresAt = expires,
                GrantsFullTier = request.GrantsFullTier,
                IsActive = true,
                CreatedAt = now
            });
        }

        userContext.PromoCodes.AddRange(codes);
        await userContext.SaveChangesAsync();

        logger.LogInformation("Admin bulk-generated {Count} promo codes", codes.Count);
        return Ok(new { Count = codes.Count, Codes = codes.Select(c => c.Code).ToList() });
    }

    // ---- helpers ----

    private static object ToPromoDto(PromoCode p, int redemptions) => new
    {
        p.CodeId,
        p.Code,
        p.Description,
        p.DurationDays,
        p.MaxUses,
        p.CurrentUses,
        p.ExpiresAt,
        p.CreatedAt,
        p.IsActive,
        p.GrantsFullTier,
        redemptions
    };

    private static bool IsValidCodeFormat(string code) =>
        code.Length is >= 8 and <= 12 && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static string GenerateCode()
    {
        var chars = new char[PromoCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = PromoCodeAlphabet[RandomNumberGenerator.GetInt32(PromoCodeAlphabet.Length)];
        return new string(chars);
    }

    private async Task<string> GenerateUniqueCodeAsync(HashSet<string> alreadyGenerated)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = GenerateCode();
            if (alreadyGenerated.Contains(code))
                continue;
            if (!await userContext.PromoCodes.AnyAsync(p => p.Code == code))
                return code;
        }

        throw new InvalidOperationException("Could not generate a unique promo code after 20 attempts.");
    }

    private static DateTime? ToUtc(DateTime? value)
    {
        if (!value.HasValue) return null;
        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }
}
