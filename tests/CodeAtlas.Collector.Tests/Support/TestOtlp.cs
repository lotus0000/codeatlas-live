using System.Net.Http.Headers;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.Collector.Tests.Support;

/// <summary>Builds OTLP requests. Times are milliseconds after a fixed epoch so tests read easily.</summary>
internal static class TestOtlp
{
    public const ulong Epoch = 1_700_000_000_000_000_000;

    public static Span Span(string traceId, string spanId, string? parent, string name, Span.Types.SpanKind kind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind.Internal,
        double startMs = 0, double endMs = 1, params (string Key, object Value)[] attributes)
    {
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(Convert.FromHexString(traceId)),
            SpanId = ByteString.CopyFrom(Convert.FromHexString(spanId)),
            ParentSpanId = parent is null ? ByteString.Empty : ByteString.CopyFrom(Convert.FromHexString(parent)),
            Name = name,
            Kind = kind,
            StartTimeUnixNano = Epoch + (ulong)(startMs * 1_000_000),
            EndTimeUnixNano = Epoch + (ulong)(endMs * 1_000_000),
        };
        foreach (var (key, value) in attributes) span.Attributes.Add(Kv(key, value));
        return span;
    }

    public static KeyValue Kv(string key, object value) => new()
    {
        Key = key,
        Value = value switch
        {
            string s => new AnyValue { StringValue = s },
            bool b => new AnyValue { BoolValue = b },
            int or long => new AnyValue { IntValue = Convert.ToInt64(value) },
            double d => new AnyValue { DoubleValue = d },
            _ => new AnyValue { StringValue = value.ToString() },
        },
    };

    public static ExportTraceServiceRequest Request(string service, string scope, IEnumerable<Span> spans, params (string Key, object Value)[] resource)
    {
        var resourceSpans = new ResourceSpans { Resource = new Resource { Attributes = { Kv("service.name", service) } } };
        foreach (var (key, value) in resource) resourceSpans.Resource.Attributes.Add(Kv(key, value));
        var scopeSpans = new ScopeSpans { Scope = new InstrumentationScope { Name = scope } };
        scopeSpans.Spans.AddRange(spans);
        resourceSpans.ScopeSpans.Add(scopeSpans);
        return new ExportTraceServiceRequest { ResourceSpans = { resourceSpans } };
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, ExportTraceServiceRequest request)
    {
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return await client.PostAsync("v1/traces", content);
    }

    public static string Id(int n, int bytes) => n.ToString("x").PadLeft(bytes * 2, '0');
    public static string TraceId(int n) => Id(n, 16);
    public static string SpanId(int n) => Id(n, 8);
}
