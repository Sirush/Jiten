using Jiten.Api.Services;
using Jiten.Api.Services.Stripe;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stripe;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/stripe")]
[Produces("application/json")]
public class StripeController(
    StripeService stripeService,
    IStripeGateway gateway,
    ICurrentUserService currentUserService,
    ILogger<StripeController> logger) : ControllerBase
{
    public record CheckoutRequest(string Plan);

    [HttpPost("checkout")]
    [Authorize]
    [EnableRateLimiting("fixed")]
    public async Task<IResult> Checkout([FromBody] CheckoutRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (!TryParsePlan(request.Plan, out var plan))
            return Results.BadRequest(new { error = "Unknown plan. Expected monthly, yearly or lifetime." });

        var outcome = await stripeService.CreateCheckoutAsync(userId, plan);
        return outcome.Success
            ? Results.Ok(new { url = outcome.Url })
            : Results.BadRequest(new { error = outcome.Error });
    }

    [HttpPost("portal")]
    [Authorize]
    [EnableRateLimiting("fixed")]
    public async Task<IResult> Portal()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var outcome = await stripeService.CreatePortalAsync(userId);
        return outcome.Success
            ? Results.Ok(new { url = outcome.Url })
            : Results.BadRequest(new { error = outcome.Error });
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IResult> Webhook()
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync();
        var signature = Request.Headers["Stripe-Signature"].ToString();

        StripeWebhookEvent evt;
        try
        {
            evt = gateway.ConstructEvent(payload, signature);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe webhook signature verification failed");
            return Results.BadRequest();
        }

        try
        {
            await stripeService.HandleWebhookAsync(evt);
        }
        catch (Exception ex)
        {
            // Return 500 so Stripe retries; the handler is idempotent so a replay is safe.
            logger.LogError(ex, "Stripe webhook {EventId} ({Type}) handling failed", evt.EventId, evt.RawType);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Results.Ok();
    }

    private static bool TryParsePlan(string? value, out CheckoutPlan plan)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "monthly": plan = CheckoutPlan.Monthly; return true;
            case "yearly": plan = CheckoutPlan.Yearly; return true;
            case "lifetime": plan = CheckoutPlan.Lifetime; return true;
            default: plan = default; return false;
        }
    }
}
