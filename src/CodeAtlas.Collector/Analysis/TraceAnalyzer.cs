using System.Globalization;
using CodeAtlas.Collector.Store;

namespace CodeAtlas.Collector.Analysis;

/// <summary>
/// Turns stored spans into what the viewer shows. Everything here is derived from span data only; anything that cannot
/// be shown from evidence becomes a <see cref="Gap"/> worded as unknown.
/// </summary>
public static class TraceAnalyzer
{
    public const string MissingParent = "Parent span not received yet: it may still be in an export batch, sampled out, or its export failed.";
    public const string NoServerSpan = "No server span received for this request. The backend may not be instrumented, the span may be sampled out, or its export was delayed or failed.";
    public const string HandlerNotRecorded = "The handler's execution was not recorded: there is no handler or action span. A filter such as validation may have ended the request first, or WithCodeAtlasHandlerSpan is not enabled for this endpoint.";
    private static readonly TimeSpan ServerSpanGrace = TimeSpan.FromSeconds(5);

    public static TraceView Analyze(string traceId, IReadOnlyList<SpanRecord> spans, bool truncated, DateTimeOffset now)
    {
        var byId = spans.ToDictionary(s => s.SpanId);
        var children = spans
            .Where(s => s.ParentSpanId is { } p && byId.ContainsKey(p) && !InCycle(s, byId))
            .GroupBy(s => s.ParentSpanId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartUnixNano).ToList());
        var roots = spans.Where(s => s.ParentSpanId is not { } p || !byId.ContainsKey(p) || InCycle(s, byId)).OrderBy(s => s.StartUnixNano);
        var nowNanos = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000;

        NodeView Build(SpanRecord span)
        {
            var kids = children.GetValueOrDefault(span.SpanId)?.Select(Build).ToList() ?? [];
            var kind = KindOf(span);
            var gaps = new List<Gap>();
            if (span.ParentSpanId is { } parent && (!byId.ContainsKey(parent) || InCycle(span, byId))) gaps.Add(new("missing-parent", MissingParent));
            if (kind is "browser-request" or "http-client" && !Descendants(kids).Any(k => k.Kind == "server") && nowNanos > span.EndUnixNano + (ulong)ServerSpanGrace.Ticks * 100)
                gaps.Add(new("no-server-span", NoServerSpan));
            if (kids.Count > 0 && Uncovered(span, kids) is var ms and >= 1)
                gaps.Add(new("uncovered-time", $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms of this span is not covered by any child span. That time is unexplained by telemetry; it does not mean nothing ran."));
            var code = CodeOf(span);
            if (kind == "endpoint" && code?.Evidence == "routing" && !Descendants(kids).Any(k => k.Kind is "handler" or "action"))
                gaps.Add(new("handler-not-recorded", HandlerNotRecorded));
            return new NodeView(span, kind, "executed", span.Attributes.GetValueOrDefault("codeatlas.correlation") as string, code, gaps, kids);
        }

        return new TraceView(Summarize(traceId, spans), roots.Select(Build).ToList(), truncated, []);
    }

    public static TraceSummary Summarize(string traceId, IReadOnlyList<SpanRecord> spans)
    {
        var interaction = spans.FirstOrDefault(s => s.Attributes.ContainsKey("codeatlas.interaction.name"));
        var server = spans.Where(s => s.Kind == "server").OrderBy(s => s.StartUnixNano).FirstOrDefault();
        var start = spans.Min(s => s.StartUnixNano);
        var end = spans.Max(s => s.EndUnixNano);
        var sampling = spans.Select(s => s.Resource.GetValueOrDefault("codeatlas.sampling") as string).FirstOrDefault(s => s is not null) ?? "unknown";
        return new TraceSummary(
            traceId, start, (end - start) / 1_000_000.0,
            interaction?.Attributes["codeatlas.interaction.name"] as string,
            interaction?.Attributes.GetValueOrDefault("codeatlas.correlation") as string,
            server?.Attributes.GetValueOrDefault("http.request.method") as string,
            server?.Attributes.GetValueOrDefault("http.route") as string,
            server?.Attributes.GetValueOrDefault("http.response.status_code") as long?,
            spans.Any(s => s.Status == "error"),
            spans.Count,
            sampling);
    }

    internal static string KindOf(SpanRecord span) => span.Attributes switch
    {
        var a when a.ContainsKey("codeatlas.interaction.name") => "interaction",
        var a when a.GetValueOrDefault("codeatlas.component") is "endpoint" or "action" or "handler" => (string)a["codeatlas.component"]!,
        var a when a.ContainsKey("db.system.name") || a.ContainsKey("db.system") => "database",
        _ when span.Kind == "server" => "server",
        var a when span.Kind == "client" && a.ContainsKey("http.request.method") && span.Scope == "codeatlas-web" => "browser-request",
        var a when span.Kind == "client" && a.ContainsKey("http.request.method") => "http-client",
        _ => "internal",
    };

    private static CodeRef? CodeOf(SpanRecord span)
    {
        var a = span.Attributes;
        if (a.GetValueOrDefault("codeatlas.code.token") is not long token) return null;
        return new CodeRef(
            a.GetValueOrDefault("codeatlas.code.assembly_path") as string ?? "",
            a.GetValueOrDefault("codeatlas.code.mvid") as string ?? "",
            token,
            a.GetValueOrDefault("codeatlas.code.function") as string ?? "",
            a.GetValueOrDefault("codeatlas.code.evidence") as string ?? "executed");
    }

    private static bool InCycle(SpanRecord span, IReadOnlyDictionary<string, SpanRecord> byId)
    {
        var seen = new HashSet<string> { span.SpanId };
        for (var current = span; current.ParentSpanId is { } parent && byId.TryGetValue(parent, out var next); current = next)
            if (!seen.Add(next.SpanId)) return true;
        return false;
    }

    private static IEnumerable<NodeView> Descendants(IEnumerable<NodeView> nodes) => nodes.SelectMany(n => Descendants(n.Children).Prepend(n));

    private static double Uncovered(SpanRecord span, IReadOnlyList<NodeView> children)
    {
        ulong covered = 0, curStart = 0, curEnd = 0;
        var started = false;
        foreach (var child in children.OrderBy(c => c.Span.StartUnixNano))
        {
            var s = Math.Max(child.Span.StartUnixNano, span.StartUnixNano);
            var e = Math.Min(child.Span.EndUnixNano, span.EndUnixNano);
            if (e <= s) continue;
            if (!started || s > curEnd)
            {
                if (started) covered += curEnd - curStart;
                (curStart, curEnd, started) = (s, e, true);
            }
            else curEnd = Math.Max(curEnd, e);
        }
        if (started) covered += curEnd - curStart;
        return (span.EndUnixNano - span.StartUnixNano - covered) / 1_000_000.0;
    }
}
