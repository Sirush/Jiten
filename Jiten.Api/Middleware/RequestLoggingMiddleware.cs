using System.Diagnostics;
using System.Security.Claims;

namespace Jiten.Api.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip logging for certain paths
        var path = context.Request.Path.Value ?? "";
        if (ShouldSkipLogging(path))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var requestId = Activity.Current?.Id ?? context.TraceIdentifier;

        var routeValues = ExtractRouteValues(context);
        var queryParams = ExtractQueryParameters(context);
        var userId = GetUserId(context);
        var clientIp = GetClientIp(context);

        _logger.LogInformation(
            "Request started: {Method} {Path} | RequestId: {RequestId} | UserId: {UserId} | ClientIp: {ClientIp} | RouteValues: {RouteValues} | Query: {QueryParams}",
            context.Request.Method,
            path,
            requestId,
            userId ?? "anonymous",
            clientIp,
            routeValues,
            queryParams);

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            _logger.LogInformation(
                "Request completed: {Method} {Path} | RequestId: {RequestId} | UserId: {UserId} | StatusCode: {StatusCode} | Duration: {Duration}ms",
                context.Request.Method,
                path,
                requestId,
                userId ?? "anonymous",
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static bool ShouldSkipLogging(string path)
    {
        // Skip static files, swagger, and health checks
        return path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/static", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".map", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ExtractRouteValues(HttpContext context)
    {
        var result = new Dictionary<string, string>();

        if (context.Request.RouteValues != null)
        {
            foreach (var (key, value) in context.Request.RouteValues)
            {
                if (key != "controller" && key != "action" && value != null)
                {
                    // Only include non-sensitive route values
                    var valueStr = value.ToString() ?? "";
                    if (!IsSensitiveParameter(key))
                    {
                        result[key] = valueStr;
                    }
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string> ExtractQueryParameters(HttpContext context)
    {
        var result = new Dictionary<string, string>();

        foreach (var (key, value) in context.Request.Query)
        {
            // Exclude sensitive query parameters
            if (!IsSensitiveParameter(key))
            {
                result[key] = value.ToString();
            }
        }

        return result;
    }

    private static bool IsSensitiveParameter(string paramName)
    {
        var sensitiveParams = new[]
        {
            "password", "token", "secret", "key", "auth", "credential",
            "pwd", "pass", "apikey", "api_key", "accesstoken", "refreshtoken"
        };

        return sensitiveParams.Any(p =>
            paramName.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetUserId(HttpContext context)
    {
        return context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    private static string GetClientIp(HttpContext context)
    {
        // Check for forwarded headers (Traefik/proxy)
        var headers = new[] { "X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP" };

        foreach (var header in headers)
        {
            var value = context.Request.Headers[header].FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                var ip = value.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(ip) && ip != "unknown")
                {
                    return ip;
                }
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public static class RequestLoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RequestLoggingMiddleware>();
    }
}
