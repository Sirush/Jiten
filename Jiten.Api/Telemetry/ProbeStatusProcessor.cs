using System.Diagnostics;
using OpenTelemetry;

namespace Jiten.Api.Telemetry;

/// <summary>A HEAD answered 404 is an existence check, not a failed call</summary>
public sealed class ProbeStatusProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity.Kind != ActivityKind.Client) return;
        if (activity.GetTagItem("http.request.method") is not "HEAD") return;
        if (activity.GetTagItem("http.response.status_code") is not 404) return;

        activity.SetStatus(ActivityStatusCode.Unset);
        activity.SetTag("error.type", null);
    }
}
