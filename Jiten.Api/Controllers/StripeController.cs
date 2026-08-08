using Jiten.Api.Services;
using Jiten.Api.Services.Legal;
using Jiten.Api.Services.Stripe;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/stripe")]
[Produces("application/json")]
public class StripeController(
    StripeService stripeService,
    IStripeGateway gateway,
    ICurrentUserService currentUserService,
    IBillingAlertService alerts,
    UserDbContext userContext,
    IOptions<LegalDocumentsOptions> legalOptions,
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

        // A sale needs recorded CGV acceptance (evidence for CGV art. 5.3); the pricing page collects it inline.
        var cgvVersion = legalOptions.Value.CgvVersion;
        var cgvAccepted = await userContext.UserLegalDocumentStates
                                           .AnyAsync(s => s.UserId == userId && s.Document == LegalDocument.Cgv &&
                                                          s.Version == cgvVersion && s.AcceptedAt != null);
        if (!cgvAccepted)
            return Results.Conflict(new { error = "cgv-acceptance-required", version = cgvVersion });

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
            BillingTelemetry.WebhookSignatureRejected.Add(1);
            // A misconfigured WebhookSecret rejects every event while Stripe reports the charge as succeeded,
            // so this must page rather than sit in the logs.
            await alerts.RaiseAsync("webhook-signature",
                                    "Stripe webhook signature rejected",
                                    $"Verification failed — check Stripe:WebhookSecret against the live endpoint's signing secret.\n{ex.Message}");
            return Results.BadRequest();
        }

        try
        {
            await stripeService.HandleWebhookAsync(evt);
        }
        catch (Exception ex)
        {
            // Return 500 so Stripe retries; the handler is idempotent so a replay is safe.
            BillingTelemetry.WebhookFailed.Add(1, new KeyValuePair<string, object?>("event.type", evt.RawType));
            logger.LogError(ex, "Stripe webhook {EventId} ({Type}) handling failed", evt.EventId, evt.RawType);
            await alerts.RaiseAsync($"webhook-failed:{evt.RawType}",
                                    "Stripe webhook handling failed",
                                    $"Event {evt.EventId} ({evt.RawType}) threw; Stripe will retry.\n{ex.Message}");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        BillingTelemetry.WebhookHandled.Add(1, new KeyValuePair<string, object?>("event.type", evt.RawType));
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
