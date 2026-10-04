using System.Diagnostics;
using System.Globalization;

namespace Jiten.Api.Middleware;

/// <summary>Feeds the frontend's sampled API timings</summary>
public class ServerTimingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
            return next(context);

        var start = Stopwatch.GetTimestamp();
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            var origin = headers.AccessControlAllowOrigin.ToString();
            if (origin.Length == 0)
                return Task.CompletedTask;

            // Browsers hide phase timings and Server-Timing from cross-origin callers the response does not name here.
            headers["Timing-Allow-Origin"] = origin;
            var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            headers["Server-Timing"] = route is null ? $"app;dur={duration}" : $"app;dur={duration};desc=\"{route}\"";
            return Task.CompletedTask;
        });

        return next(context);
    }
}
