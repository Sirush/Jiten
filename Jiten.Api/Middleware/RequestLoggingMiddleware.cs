using System.Diagnostics;
using System.Security.Claims;

namespace Jiten.Api.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly long _slowMs;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger, IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _slowMs = configuration.GetValue<long?>("RequestLogging:SlowMs") ?? 1000;
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

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            var statusCode = context.Response.StatusCode;
            var elapsedMs = stopwatch.ElapsedMilliseconds;

            // Only the tail is logged: AspNetCore OTel instrumentation already spans every request,
            // so a line per request duplicates the trace. RequestLogging:SlowMs=0 restores logging all.
            if (statusCode >= 400 || elapsedMs >= _slowMs)
            {
                _logger.LogInformation(
                    "Request: {Method} {Path} | RequestId: {RequestId} | UserId: {UserId} | ClientIp: {ClientIp} | StatusCode: {StatusCode} | Duration: {Duration}ms | RouteValues: {RouteValues} | Query: {QueryParams}",
                    context.Request.Method,
                    path,
                    Activity.Current?.Id ?? context.TraceIdentifier,
                    GetUserId(context) ?? "anonymous",
                    GetClientIp(context),
                    statusCode,
                    elapsedMs,
                    ExtractRouteValues(context),
                    ExtractQueryParameters(context));
            }
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
