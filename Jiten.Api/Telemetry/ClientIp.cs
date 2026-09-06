namespace Jiten.Api.Telemetry;

public static class ClientIp
{
    private static readonly string[] ProxyHeaders = ["X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP"];

    /// <summary>First hop of the proxy headers, falling back to the connection peer (Traefik behind a proxy).</summary>
    public static string Resolve(HttpContext context)
    {
        foreach (var header in ProxyHeaders)
        {
            var value = context.Request.Headers[header].FirstOrDefault();
            if (string.IsNullOrEmpty(value)) continue;
            var ip = value.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(ip) && ip != "unknown") return ip;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
