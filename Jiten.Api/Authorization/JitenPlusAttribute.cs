using Jiten.Api.Services;
using Jiten.Core.Data.Billing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jiten.Api.Authorization;

/// <summary>
/// Gates an endpoint behind a Jiten+ tier. Defaults to <see cref="JitenPlusTier.Trial"/> ;
/// use <c>[JitenPlus(JitenPlusTier.Full)]</c> for storage features. Unauthenticated
/// requests get 401; insufficient tier gets the §8 403 payload. Set <see cref="Feature"/> to name the gated
/// capability in that payload (e.g. <c>[JitenPlus(JitenPlusTier.Full, Feature = "card-images")]</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class JitenPlusAttribute(JitenPlusTier requiredTier = JitenPlusTier.Trial) : Attribute, IFilterFactory
{
    public JitenPlusTier RequiredTier { get; } = requiredTier;

    public string? Feature { get; set; }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var service = serviceProvider.GetRequiredService<IJitenPlusService>();
        var currentUser = serviceProvider.GetRequiredService<ICurrentUserService>();
        return new JitenPlusFilter(service, currentUser, RequiredTier, Feature);
    }
}

public sealed class JitenPlusFilter(
    IJitenPlusService service,
    ICurrentUserService currentUser,
    JitenPlusTier requiredTier,
    string? feature) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userId = currentUser.UserId;
        if (string.IsNullOrEmpty(userId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var currentTier = await service.GetTierAsync(userId);
        if (currentTier < requiredTier)
        {
            context.Result = new ObjectResult(new
            {
                jitenPlus = true,
                feature,
                requiredTier = requiredTier.ToString().ToLowerInvariant(),
                currentTier = currentTier.ToString().ToLowerInvariant(),
                message = BuildMessage(requiredTier, currentTier)
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }

    private static string BuildMessage(JitenPlusTier requiredTier, JitenPlusTier currentTier) =>
        requiredTier == JitenPlusTier.Full && currentTier == JitenPlusTier.Trial
            ? "This feature stores your data permanently and isn't part of the trial. It unlocks with any paid plan."
            : "This feature requires Jiten+.";
}
