using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CodeAtlas.AspNetCore.Http;

internal sealed record HttpFacts(string Method, string? Route, int StatusCode, bool UnhandledException);

/// <summary>
/// Side table keyed by the request's Activity object. CodeAtlas records HTTP facts here instead of adding tags to an
/// Activity that other tracing pipelines also see. Entries disappear with the Activity.
/// </summary>
internal sealed class HttpFactsStore
{
    private readonly ConditionalWeakTable<Activity, HttpFacts> _facts = new();

    public void Set(Activity activity, HttpFacts facts) => _facts.AddOrUpdate(activity, facts);

    public HttpFacts? Get(Activity activity) => _facts.TryGetValue(activity, out var facts) ? facts : null;
}
