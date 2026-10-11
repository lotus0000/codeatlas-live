using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace CodeAtlas.Collector.Store;

/// <summary>
/// Bounded in-memory store. Spans are upserted by id, so batching, retries and out-of-order arrival are normal.
/// Thread-safe; all public members take the same lock.
/// </summary>
internal sealed class TraceStore(StoreLimits limits)
{
    private readonly Dictionary<string, Dictionary<string, SpanRecord>> _traces = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();
    private readonly HashSet<string> _truncated = new();
    private readonly Lock _lock = new();

    /// <summary>Adds every span of the request; returns the ids of the traces it touched.</summary>
    public IReadOnlyList<string> Add(ExportTraceServiceRequest request)
    {
        var records = new List<SpanRecord>();
        foreach (var resourceSpans in request.ResourceSpans)
        {
            var resource = Attributes(resourceSpans.Resource?.Attributes ?? []);
            var service = resource.GetValueOrDefault("service.name") as string ?? "unknown";
            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            foreach (var span in scopeSpans.Spans)
                records.Add(ToRecord(span, service, scopeSpans.Scope?.Name ?? "", resource));
        }

        lock (_lock)
        {
            var touched = new List<string>();
            foreach (var record in records)
            {
                if (!_traces.TryGetValue(record.TraceId, out var spans))
                {
                    _traces[record.TraceId] = spans = new Dictionary<string, SpanRecord>();
                    _nodes[record.TraceId] = _order.AddFirst(record.TraceId);
                    while (_order.Count > limits.MaxTraces) Evict(_order.Last!.Value);
                }
                if (spans.TryGetValue(record.SpanId, out var existing))
                {
                    // Several hosts in one process may export the same span; keep the most detailed copy.
                    if (record.Attributes.Count >= existing.Attributes.Count) spans[record.SpanId] = record;
                }
                else if (spans.Count >= limits.MaxSpansPerTrace) _truncated.Add(record.TraceId);
                else spans[record.SpanId] = record;
                if (!touched.Contains(record.TraceId)) touched.Add(record.TraceId);
            }
            return touched;
        }
    }

    public IReadOnlyList<SpanRecord>? Get(string traceId)
    {
        lock (_lock) return _traces.TryGetValue(traceId, out var spans) ? spans.Values.ToList() : null;
    }

    public IReadOnlyList<string> RecentTraceIds(int limit)
    {
        lock (_lock) return _order.Take(limit).ToList();
    }

    public bool IsTruncated(string traceId)
    {
        lock (_lock) return _truncated.Contains(traceId);
    }

    private void Evict(string traceId)
    {
        _traces.Remove(traceId);
        _truncated.Remove(traceId);
        if (_nodes.Remove(traceId, out var node)) _order.Remove(node);
    }

    private SpanRecord ToRecord(ProtoSpan span, string service, string scope, IReadOnlyDictionary<string, object?> resource) => new(
        Hex(span.TraceId),
        Hex(span.SpanId),
        span.ParentSpanId.IsEmpty ? null : Hex(span.ParentSpanId),
        span.Name,
        span.Kind switch
        {
            ProtoSpan.Types.SpanKind.Server => "server",
            ProtoSpan.Types.SpanKind.Client => "client",
            ProtoSpan.Types.SpanKind.Internal => "internal",
            ProtoSpan.Types.SpanKind.Producer => "producer",
            ProtoSpan.Types.SpanKind.Consumer => "consumer",
            _ => "unspecified",
        },
        service,
        scope,
        span.StartTimeUnixNano,
        Math.Max(span.EndTimeUnixNano, span.StartTimeUnixNano),
        span.Status?.Code switch
        {
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error => "error",
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Ok => "ok",
            _ => "unset",
        },
        string.IsNullOrEmpty(span.Status?.Message) ? null : span.Status.Message,
        Attributes(span.Attributes),
        span.Events.Select(e => new SpanEventRecord(e.Name, e.TimeUnixNano, Attributes(e.Attributes))).ToList(),
        resource);

    private Dictionary<string, object?> Attributes(IEnumerable<KeyValue> values)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kv in values) result[kv.Key] = Value(kv.Value);
        return result;
    }

    private object? Value(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => Cut(value.StringValue),
        AnyValue.ValueOneofCase.IntValue => value.IntValue,
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
        AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(Value).ToList(),
        AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.Span),
        _ => null,
    };

    private string Cut(string s) => s.Length <= limits.MaxAttributeLength ? s : s[..limits.MaxAttributeLength] + "…";

    private static string Hex(ByteString bytes) => Convert.ToHexStringLower(bytes.Span);
}
