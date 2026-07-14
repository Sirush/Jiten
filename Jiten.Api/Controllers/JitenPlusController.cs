using Jiten.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/jiten-plus")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("fixed")]
public class JitenPlusController(
    IJitenPlusService jitenPlusService,
    ICurrentUserService currentUserService) : ControllerBase
{
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
}
