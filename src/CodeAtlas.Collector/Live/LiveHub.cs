using Microsoft.AspNetCore.SignalR;

namespace CodeAtlas.Collector.Live;

/// <summary>Viewers connect here; the server only pushes.</summary>
public sealed class LiveHub : Hub;

internal sealed class LiveNotifier(IHubContext<LiveHub> hub, Store.TraceStore store)
{
    public Task NotifyAsync(IReadOnlyList<string> traceIds)
    {
        var summaries = traceIds
            .Select(id => store.Get(id) is { Count: > 0 } spans ? Analysis.TraceAnalyzer.Summarize(id, spans) : null)
            .OfType<Analysis.TraceSummary>()
            .ToArray();
        return summaries.Length == 0 ? Task.CompletedTask : hub.Clients.All.SendAsync("traces-updated", summaries);
    }
}
