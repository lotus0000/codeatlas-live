using System.Collections.Concurrent;
using System.Diagnostics;
using CodeAtlas.AspNetCore.Export;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests.Support;

internal sealed record RecordedSpan(string Scope, Span Span, Resource Resource)
{
    public string Name => Span.Name;
    public string TraceId => Convert.ToHexStringLower(Span.TraceId.Span);
    public string SpanId => Convert.ToHexStringLower(Span.SpanId.Span);
    public string ParentSpanId => Convert.ToHexStringLower(Span.ParentSpanId.Span);
    public bool IsError => Span.Status?.Code == Status.Types.StatusCode.Error;

    public object? Attr(string key)
    {
        var value = Span.Attributes.FirstOrDefault(a => a.Key == key)?.Value;
        return value?.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            null => null,
            _ => value.ToString(),
        };
    }
}

internal sealed class RecordingSink : ISpanSink
{
    private readonly ConcurrentQueue<RecordedSpan> _spans = new();
    private readonly ConcurrentQueue<ExportTraceServiceRequest> _requests = new();

    public IReadOnlyList<RecordedSpan> Spans => [.. _spans];
    public IReadOnlyList<ExportTraceServiceRequest> Requests => [.. _requests];

    public Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        foreach (var rs in request.ResourceSpans)
        foreach (var ss in rs.ScopeSpans)
        foreach (var span in ss.Spans)
            _spans.Enqueue(new RecordedSpan(ss.Scope.Name, span, rs.Resource));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RecordedSpan>> WaitForAsync(Func<IReadOnlyList<RecordedSpan>, bool> done, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            var now = Spans;
            if (done(now)) return now;
            await Task.Delay(20);
        }
        throw new TimeoutException("Spans received:\n" + string.Join('\n', Spans.Select(s => $"{s.Scope} | {s.Name} | {s.TraceId}")));
    }

    /// <summary>All spans of one trace, once its server span has arrived (children end, and are sent, before it).</summary>
    public async Task<IReadOnlyList<RecordedSpan>> WaitForTraceAsync(string traceId, int timeoutMs = 5000)
    {
        await WaitForAsync(s => s.Any(x => x.TraceId == traceId && x.Span.Kind == Span.Types.SpanKind.Server), timeoutMs);
        await Task.Delay(50);
        return Spans.Where(s => s.TraceId == traceId).ToList();
    }
}
