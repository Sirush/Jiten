using System.Diagnostics;
using Hangfire.Common;
using Hangfire.Server;

namespace Jiten.Api.Telemetry;

public sealed class HangfireActivityFilter : JobFilterAttribute, IServerFilter
{
    public const string SourceName = "Jiten.Api.Hangfire";

    private static readonly ActivitySource Source = new(SourceName);
    private const string ItemKey = "kami.activity";

    public void OnPerforming(PerformingContext context)
    {
        var job = context.BackgroundJob.Job;
        var name = $"{job.Type.Name}.{job.Method.Name}";
        var activity = Source.StartActivity(name, ActivityKind.Consumer, parentContext: default);
        if (activity is null) return;

        activity.SetTag("job.system", "hangfire");
        activity.SetTag("job.name", name);
        activity.SetTag("job.id", context.BackgroundJob.Id);
        activity.SetTag("job.queue", ResolveQueue(job));
        activity.SetTag("job.server", context.ServerId);
        activity.SetTag("job.attempt", context.GetJobParameter<int>("RetryCount") + 1);
        var recurringId = context.GetJobParameter<string>("RecurringJobId");
        if (!string.IsNullOrEmpty(recurringId)) activity.SetTag("job.recurring_id", recurringId);
        context.Items[ItemKey] = activity;
    }

    public void OnPerformed(PerformedContext context)
    {
        if (!context.Items.TryGetValue(ItemKey, out var value) || value is not Activity activity) return;

        if (context.Canceled)
        {
            activity.SetTag("job.outcome", "cancelled");
        }
        else if (context.Exception is { } ex)
        {
            activity.SetTag("job.outcome", "failed");
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity.AddException(ex);
        }
        else
        {
            activity.SetTag("job.outcome", "succeeded");
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        activity.Dispose();
    }

    private static string ResolveQueue(Job job)
    {
        if (!string.IsNullOrEmpty(job.Queue)) return job.Queue;
        var attr = job.Method.GetCustomAttributes(typeof(Hangfire.QueueAttribute), true).FirstOrDefault() as Hangfire.QueueAttribute
                   ?? job.Type.GetCustomAttributes(typeof(Hangfire.QueueAttribute), true).FirstOrDefault() as Hangfire.QueueAttribute;
        return attr?.Queue ?? "default";
    }
}
