using System.Diagnostics;

namespace CodeAtlas.AspNetCore.Tests.Support;

internal static class TestActivities
{
    /// <summary>A stopped, recorded activity from the named source, as an instrumentation library would produce it.</summary>
    public static Activity Finished(string source, string name, ActivityKind kind = ActivityKind.Internal, Action<Activity>? setup = null)
    {
        using var activitySource = new ActivitySource(source);
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => ReferenceEquals(s, activitySource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var activity = activitySource.StartActivity(name, kind)!;
        setup?.Invoke(activity);
        activity.Stop();
        return activity;
    }
}
