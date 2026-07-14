using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/jiten-plus")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("fixed")]
public class JitenPlusController(
    IJitenPlusService jitenPlusService,
    ICurrentUserService currentUserService,
    UserDbContext userContext,
    IEmailService emailService,
    ILogger<JitenPlusController> logger) : ControllerBase
{
    public record RedeemRequest(string? Code);

    [HttpGet("status")]
    public async Task<IResult> GetStatus()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var status = await jitenPlusService.GetStatusAsync(userId);

        return Results.Ok(new
        {
            tier = status.Tier.ToString().ToLowerInvariant(),
            sources = new
            {
                subscriptionActive = status.SubscriptionActive,
                plan = status.Plan?.ToString(),
                periodEnd = status.PeriodEnd,
                isLifetime = status.IsLifetime,
                lifetimeSource = status.LifetimeSource?.ToString(),
                promoCreditDays = status.PromoCreditDays,
                credits = status.Credits.Select(c => new
                {
                    c.UserPromoCreditId,
                    c.RemainingDays,
                    c.GrantsFullTier,
                    c.GrantedAt,
                    c.ThankYouMessage
                }),
                adminOverride = status.AdminOverride
            },
            quota = new
            {
                usedBytes = 0L,
                maxBytes = JitenPlusConstants.StorageQuotaBytes
            }
        });
    }

    /// <summary>
    /// Redeem a promo code for Jiten+ days. IP-rate-limited (auth policy) to blunt sweeps of guessable codes.
    /// </summary>
    [HttpPost("redeem")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> Redeem([FromBody] RedeemRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var code = request.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
            return Results.BadRequest(new { error = "Please enter a code." });

        var promo = await userContext.PromoCodes.FirstOrDefaultAsync(p => p.Code == code);
        if (promo is null || !promo.IsActive)
            return Results.BadRequest(new { error = "This code is not valid." });

        if (promo.ExpiresAt.HasValue && promo.ExpiresAt.Value < DateTime.UtcNow)
            return Results.BadRequest(new { error = "This code has expired." });

        if (promo.MaxUses.HasValue && promo.CurrentUses >= promo.MaxUses.Value)
            return Results.BadRequest(new { error = "This code has already been fully redeemed." });

        var alreadyRedeemed = await userContext.UserPromoCredits
                                               .AnyAsync(c => c.UserId == userId && c.PromoCodeId == promo.CodeId);
        if (alreadyRedeemed)
            return Results.BadRequest(new { error = "You have already redeemed this code." });

        // Atomically claim a use so concurrent redemptions can't exceed MaxUses.
        var claimed = await userContext.PromoCodes
                                       .Where(p => p.CodeId == promo.CodeId && (p.MaxUses == null || p.CurrentUses < p.MaxUses))
                                       .ExecuteUpdateAsync(s => s.SetProperty(p => p.CurrentUses, p => p.CurrentUses + 1));
        if (claimed == 0)
            return Results.BadRequest(new { error = "This code has already been fully redeemed." });

        var credit = new UserPromoCredit
        {
            UserId = userId,
            PromoCodeId = promo.CodeId,
            Source = PromoCreditSource.Redemption,
            GrantsFullTier = promo.GrantsFullTier,
            RemainingDays = promo.DurationDays,
            GrantedAt = DateTime.UtcNow
        };
        userContext.UserPromoCredits.Add(credit);

        try
        {
            await userContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race on the one-redemption-per-code unique index: give back the use we claimed.
            await userContext.PromoCodes
                             .Where(p => p.CodeId == promo.CodeId && p.CurrentUses > 0)
                             .ExecuteUpdateAsync(s => s.SetProperty(p => p.CurrentUses, p => p.CurrentUses - 1));
            return Results.BadRequest(new { error = "You have already redeemed this code." });
        }

        jitenPlusService.InvalidateTier(userId);

        var email = await userContext.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();
        var grantsFull = promo.GrantsFullTier;
        await emailService.SendPromoRedeemedAsync(email, promo.DurationDays, grantsFull);

        logger.LogInformation("User {UserId} redeemed promo code {Code} ({Days} days, full={Full})",
            userId, promo.Code, promo.DurationDays, grantsFull);

        return Results.Ok(new
        {
            tier = (grantsFull ? JitenPlusTier.Full : JitenPlusTier.Trial).ToString().ToLowerInvariant(),
            days = promo.DurationDays,
            grantsFullTier = grantsFull
        });
    }
}
