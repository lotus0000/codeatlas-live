using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace CodeAtlas.Collector.Live;

/// <summary>Viewers connect here; the server only pushes.</summary>
public sealed class LiveHub : Hub;

/// <summary>
/// Ingest only queues the ids of changed traces; a background loop coalesces them and pushes summaries. A viewer that
/// stops reading can delay other viewers by at most the send timeout, and can never slow down ingest.
/// </summary>
internal sealed class LiveNotifier(IHubContext<LiveHub> hub, Store.TraceStore store) : BackgroundService
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    private readonly Channel<string> _changed = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public Task NotifyAsync(IReadOnlyList<string> traceIds)
    {
        foreach (var id in traceIds) _changed.Writer.TryWrite(id);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _changed.Reader;
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            var ids = new HashSet<string>();
            while (ids.Count < 200 && reader.TryRead(out var id)) ids.Add(id);
            var summaries = ids
                .Select(id => store.Get(id) is { Count: > 0 } spans ? Analysis.TraceAnalyzer.Summarize(id, spans) : null)
                .OfType<Analysis.TraceSummary>()
                .ToArray();
            if (summaries.Length == 0) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(SendTimeout);
            try { await hub.Clients.All.SendAsync("traces-updated", summaries, timeout.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
        }
    }
}
