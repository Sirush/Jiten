using System.Diagnostics.Metrics;

namespace Jiten.Api.Telemetry;

public static class RateLimitTelemetry
{
    public const string MeterName = "Jiten.Api.RateLimit";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Tagged with the policy name and the partition kind (user, ip, ingest), never the partition key itself.</summary>
    public static readonly Counter<long> Rejected = Meter.CreateCounter<long>("jiten.ratelimit.rejected");
}
