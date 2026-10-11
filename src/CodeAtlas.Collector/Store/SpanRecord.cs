using System.Text.Json.Serialization;

namespace CodeAtlas.Collector.Store;

public sealed record SpanEventRecord(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong TimeUnixNano,
    IReadOnlyDictionary<string, object?> Attributes);

/// <summary>One stored span. Nanosecond times travel as strings: they exceed JavaScript's exact integer range.</summary>
public sealed record SpanRecord(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Kind,
    string Service,
    string Scope,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong StartUnixNano,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong EndUnixNano,
    string Status,
    string? StatusMessage,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<SpanEventRecord> Events,
    IReadOnlyDictionary<string, object?> Resource)
{
    public double DurationMs => (EndUnixNano - StartUnixNano) / 1_000_000.0;
}
