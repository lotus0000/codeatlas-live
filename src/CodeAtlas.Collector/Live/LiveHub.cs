using Microsoft.AspNetCore.SignalR;

namespace CodeAtlas.Collector.Live;

/// <summary>Viewers connect here; the server only pushes.</summary>
public sealed class LiveHub : Hub;

internal sealed class LiveNotifier(IHubContext<LiveHub> hub)
{
    public Task NotifyAsync(IReadOnlyList<string> traceIds) =>
        traceIds.Count == 0 ? Task.CompletedTask : hub.Clients.All.SendAsync("traces-updated", traceIds.ToArray());
}
