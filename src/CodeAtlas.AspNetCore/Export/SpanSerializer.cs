using System.Diagnostics;
using CodeAtlas.AspNetCore.Http;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace CodeAtlas.AspNetCore.Export;

internal sealed record SerializedSpan(string Scope, ProtoSpan Span);

/// <summary>
/// Copies a finished Activity into an OTLP span and applies CodeAtlas's privacy rules during the copy.
/// The Activity is never modified, so any other tracing pipeline in the app sees it exactly as before.
/// </summary>
internal sealed class SpanSerializer(bool keepDbStatements, HttpFactsStore facts)
{
    public const string AspNetCoreSource = "Microsoft.AspNetCore";

    private static readonly HashSet<string> DbText = ["db.statement", "db.query.text"];
    private const int MaxArrayItems = 128;
    private static readonly HashSet<string> Dropped =
        ["db.npgsql.data_source", "db.connection_string", "url.query", "user_agent.original", "client.address"];
    // Header values and end-user identity, whoever recorded them (the app's own instrumentation may add these).
    private static readonly string[] DroppedPrefixes = ["http.request.header.", "http.response.header.", "enduser."];

    public SerializedSpan Serialize(Activity activity)
    {
        var span = new ProtoSpan
        {
            TraceId = Id(activity.TraceId),
            SpanId = Id(activity.SpanId),
            ParentSpanId = activity.ParentSpanId == default ? ByteString.Empty : Id(activity.ParentSpanId),
            Name = activity.DisplayName,
            Kind = Kind(activity.Kind),
            StartTimeUnixNano = Nanos(activity.StartTimeUtc),
            EndTimeUnixNano = Nanos(activity.StartTimeUtc + activity.Duration),
        };

        foreach (var (key, value) in activity.TagObjects)
        {
            if (value is null || Dropped.Contains(key) || DroppedPrefixes.Any(key.StartsWith)) continue;
            if (DbText.Contains(key) && value is string sql)
            {
                var summary = SqlSummary.Summarize(sql);
                span.Name = summary;
                span.Attributes.Add(Kv("db.query.summary", summary));
                if (keepDbStatements) span.Attributes.Add(Kv(key, sql));
                continue;
            }
            TryAdd(span.Attributes, key, key == "url.full" && value is string url ? StripQuery(url) : value);
        }

        foreach (var e in activity.Events)
        {
            var ev = new ProtoSpan.Types.Event { Name = e.Name, TimeUnixNano = Nanos(e.Timestamp.UtcDateTime) };
            foreach (var (key, value) in e.Tags)
                if (value is not null) TryAdd(ev.Attributes, key, value);
            span.Events.Add(ev);
        }

        span.Status = activity.Status switch
        {
            ActivityStatusCode.Error => new Status { Code = Status.Types.StatusCode.Error, Message = activity.StatusDescription ?? "" },
            ActivityStatusCode.Ok => new Status { Code = Status.Types.StatusCode.Ok },
            _ => new Status(),
        };

        if (activity.Source.Name == AspNetCoreSource && facts.Get(activity) is { } http) ApplyHttpFacts(span, http);
        return new SerializedSpan(activity.Source.Name, span);
    }

    public static KeyValue Kv(string key, object value) => new() { Key = key, Value = Any(value) };

    // One bad value (e.g. a ToString() that throws) costs that attribute, never the span.
    private static void TryAdd(Google.Protobuf.Collections.RepeatedField<KeyValue> attributes, string key, object value)
    {
        try { attributes.Add(Kv(key, value)); }
        catch (Exception) { }
    }

    private static void ApplyHttpFacts(ProtoSpan span, HttpFacts http)
    {
        void AddIfMissing(string key, object value)
        {
            if (span.Attributes.All(a => a.Key != key)) span.Attributes.Add(Kv(key, value));
        }

        AddIfMissing("http.request.method", http.Method);
        AddIfMissing("http.response.status_code", http.StatusCode);
        if (http.Route is not null)
        {
            AddIfMissing("http.route", http.Route);
            span.Name = $"{http.Method} {http.Route}";
        }
        if (http.UnhandledException)
        {
            span.Attributes.Add(Kv("codeatlas.http.unhandled_exception", true));
            span.Status = new Status { Code = Status.Types.StatusCode.Error, Message = "Unhandled exception" };
        }
        else if (http.StatusCode >= 500 && span.Status.Code == Status.Types.StatusCode.Unset)
        {
            span.Status = new Status { Code = Status.Types.StatusCode.Error, Message = $"HTTP {http.StatusCode}" };
        }
    }

    private static AnyValue Any(object value) => value switch
    {
        string s => new AnyValue { StringValue = s },
        bool b => new AnyValue { BoolValue = b },
        int or long or short or byte or sbyte or ushort or uint => new AnyValue { IntValue = Convert.ToInt64(value) },
        double or float or decimal => new AnyValue { DoubleValue = Convert.ToDouble(value) },
        // Only real arrays: any other sequence may be lazy (a query, an iterator) and must never be enumerated here.
        Array items => ArrayOf(items),
        _ => new AnyValue { StringValue = value.ToString() ?? "" },
    };

    private static AnyValue ArrayOf(Array items)
    {
        var array = new ArrayValue();
        foreach (var item in items)
        {
            if (array.Values.Count == MaxArrayItems) break;
            if (item is not null) array.Values.Add(Any(item));
        }
        return new AnyValue { ArrayValue = array };
    }

    private static string StripQuery(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    private static ByteString Id(ActivityTraceId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.CopyTo(bytes);
        return ByteString.CopyFrom(bytes);
    }

    private static ByteString Id(ActivitySpanId id)
    {
        Span<byte> bytes = stackalloc byte[8];
        id.CopyTo(bytes);
        return ByteString.CopyFrom(bytes);
    }

    private static ulong Nanos(DateTime utc) => (ulong)(utc - DateTime.UnixEpoch).Ticks * 100;

    private static ProtoSpan.Types.SpanKind Kind(ActivityKind kind) => kind switch
    {
        ActivityKind.Server => ProtoSpan.Types.SpanKind.Server,
        ActivityKind.Client => ProtoSpan.Types.SpanKind.Client,
        ActivityKind.Producer => ProtoSpan.Types.SpanKind.Producer,
        ActivityKind.Consumer => ProtoSpan.Types.SpanKind.Consumer,
        _ => ProtoSpan.Types.SpanKind.Internal,
    };
}
