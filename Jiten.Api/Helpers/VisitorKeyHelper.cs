using System.Net;
using System.Net.Sockets;

namespace Jiten.Api.Helpers;

public static class VisitorKeyHelper
{
    /// <summary>The proxy-resolved connection address is the only value a client cannot forge; headers are a fallback for calls arriving from inside the network.</summary>
    public static string For(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote != null && !IsInternal(remote)) return remote.ToString();

        foreach (var header in new[] { "CF-Connecting-IP", "X-Real-IP", "X-Forwarded-For" })
        {
            var value = context.Request.Headers[header].FirstOrDefault();
            if (string.IsNullOrEmpty(value)) continue;
            var ip = value.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(ip) && ip != "unknown") return ip;
        }

        return remote?.ToString() ?? "unknown";
    }

    private static bool IsInternal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
