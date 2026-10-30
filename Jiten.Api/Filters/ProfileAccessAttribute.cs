using Jiten.Api.Services;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jiten.Api.Helpers;

/// <summary>Resolves the route's profile before the action and answers the same 404 for a missing profile and a hidden one, section or field.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ProfileAccessAttribute : Attribute, IAsyncActionFilter
{
    private static readonly object ItemKey = new();

    private readonly ProfileSection? _section;
    private readonly ProfileField? _field;

    public ProfileAccessAttribute() { }

    public ProfileAccessAttribute(ProfileSection section) => _section = section;

    /// <summary>For endpoints whose whole payload is one field.</summary>
    public ProfileAccessAttribute(ProfileField field) => _field = field;

    public static ProfileContext Get(HttpContext httpContext) =>
        httpContext.Items[ItemKey] as ProfileContext
        ?? throw new InvalidOperationException($"The action is not marked with {nameof(ProfileAccessAttribute)}.");

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var profiles = context.HttpContext.RequestServices.GetRequiredService<IProfileAccessService>();
        var route = context.RouteData.Values;
        var ct = context.HttpContext.RequestAborted;

        var access = route["targetUserId"] is string userId
            ? await profiles.ResolveByIdAsync(userId, ct)
            : (route["handle"] ?? route["username"]) is string handle
                ? await profiles.ResolveAsync(handle, ct)
                : throw new InvalidOperationException("The route names no profile.");

        if (access == null || (_section is { } section && !access.CanSee(section)) || (_field is { } field && !access.Shows(field)))
        {
            context.Result = new NotFoundObjectResult(new { message = "Profile not found" });
            return;
        }

        context.HttpContext.Items[ItemKey] = access;
        await next();
    }
}
