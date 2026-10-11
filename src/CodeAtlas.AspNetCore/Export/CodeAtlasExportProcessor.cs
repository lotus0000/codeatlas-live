using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Export;

/// <summary>
/// Serializes each finished activity immediately (a snapshot, so later changes by others cannot leak in), queues it in
/// a bounded channel that drops the oldest entry when full, and sends batches from a background task.
/// Nothing here can block or fail a request.
/// </summary>
internal sealed class CodeAtlasExportProcessor : BaseProcessor<Activity>
{
    private const int MaxBatch = 512;

    private readonly SpanSerializer _serializer;
    private readonly ISpanSink _sink;
    private readonly Resource _resource;
    private readonly TimeSpan _batchDelay;
    private readonly ILogger _logger;
    private readonly Channel<SerializedSpan> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _flushNow = new();
    private readonly Task _loop;
    private long _dropped;
    private long _sendFailures;
    private long _lastWarning;

    public CodeAtlasExportProcessor(SpanSerializer serializer, ISpanSink sink, Resource resource, int capacity, TimeSpan batchDelay, ILogger logger)
    {
        _serializer = serializer;
        _sink = sink;
        _resource = resource;
        _batchDelay = batchDelay;
        _logger = logger;
        _queue = Channel.CreateBounded<SerializedSpan>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
        _loop = Task.Run(RunAsync);
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    public long SendFailures => Interlocked.Read(ref _sendFailures);

    public override void OnEnd(Activity activity)
    {
        try { _queue.Writer.TryWrite(_serializer.Serialize(activity)); }
        catch (Exception e) { Warn("serialization", e); }
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        _queue.Writer.TryComplete();
        _flushNow.Cancel(); // end a pending batch delay so queued spans go out now
        var finished = _loop.Wait(timeoutMilliseconds < 0 ? Timeout.Infinite : timeoutMilliseconds);
        _stopping.Cancel();
        return finished;
    }

    private async Task RunAsync()
    {
        // Our own HTTP export must never be traced (it would feed itself).
        using var suppress = SuppressInstrumentationScope.Begin();
        Activity.Current = null;
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stopping.Token))
            {
                try { await Task.Delay(_batchDelay, _flushNow.Token); }
                catch (OperationCanceledException) when (_flushNow.IsCancellationRequested) { }
                var batch = new List<SerializedSpan>();
                while (batch.Count < MaxBatch && reader.TryRead(out var span)) batch.Add(span);
                if (batch.Count > 0) await SendAsync(batch);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task SendAsync(List<SerializedSpan> batch)
    {
        var resourceSpans = new ResourceSpans { Resource = _resource };
        foreach (var group in batch.GroupBy(s => s.Scope))
        {
            var scope = new ScopeSpans { Scope = new InstrumentationScope { Name = group.Key } };
            scope.Spans.AddRange(group.Select(s => s.Span));
            resourceSpans.ScopeSpans.Add(scope);
        }
        try
        {
            await _sink.SendAsync(new ExportTraceServiceRequest { ResourceSpans = { resourceSpans } }, _stopping.Token);
        }
        catch (Exception e) when (!_stopping.IsCancellationRequested)
        {
            Interlocked.Increment(ref _sendFailures);
            Warn("export", e);
        }
    }

    // At most one log line a minute, at Debug: an offline collector is normal and must not spam the app's logs.
    private void Warn(string stage, Exception e)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWarning);
        if (last != 0 && now - last < 60_000) return;
        Interlocked.Exchange(ref _lastWarning, now);
        _logger.LogDebug(e, "CodeAtlas {Stage} failed; CodeAtlas telemetry is being dropped. The application is not affected.", stage);
    }
}
