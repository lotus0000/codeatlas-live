using System.Globalization;

namespace CodeAtlas.Collector.Analysis;

/// <summary>
/// Deterministic explanation built only from recorded spans. Each line lists the spans it is based on.
/// Routing metadata and timing-based links are worded as such; nothing unrecorded is stated as fact.
/// </summary>
public static class Explainer
{
    private static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    public static IReadOnlyList<ExplainLine> Explain(TraceSummary summary, IReadOnlyList<NodeView> roots)
    {
        var nodes = Flatten(roots).ToList();
        var lines = new List<ExplainLine>();

        var interaction = nodes.FirstOrDefault(n => n.Kind == "interaction");
        lines.Add(interaction is null
            ? new("entry", "No browser interaction was captured for this trace.", [])
            : new("entry", interaction.Correlation == "explicit"
                ? $"The interaction \"{summary.Interaction}\" triggered this request (explicit link)."
                : $"The interaction \"{summary.Interaction}\" likely triggered this request (linked by timing, not proven).",
                [interaction.Span.SpanId]));

        foreach (var server in nodes.Where(n => n.Kind == "server"))
        {
            var a = server.Span.Attributes;
            lines.Add(new("request",
                $"{a.GetValueOrDefault("http.request.method")} {a.GetValueOrDefault("http.route") ?? server.Span.Name} returned {a.GetValueOrDefault("http.response.status_code")} in {Ms(server.Span.DurationMs)} ms.",
                [server.Span.SpanId]));
        }

        foreach (var node in nodes.Where(n => n.Code?.Evidence == "executed"))
            lines.Add(new("executed", $"Executed {node.Code!.Function} ({node.Kind}).", [node.Span.SpanId]));
        foreach (var node in nodes.Where(n => n.Code?.Evidence == "routing" && !Flatten(n.Children).Any(c => c.Code?.Evidence == "executed")))
            lines.Add(new("routing", $"Routed to {node.Code!.Function}; whether it ran is not recorded.", [node.Span.SpanId]));

        var database = nodes.Where(n => n.Kind == "database").ToList();
        if (database.Count > 0)
        {
            var groups = database.GroupBy(n => n.Span.Name).Select(g => $"{g.Key} ×{g.Count()}");
            lines.Add(new("database",
                $"{database.Count} database operation{(database.Count == 1 ? "" : "s")}: {string.Join(", ", groups)}.",
                database.Select(n => n.Span.SpanId).ToList()));
        }

        var calls = nodes.Where(n => n.Kind == "http-client").ToList();
        if (calls.Count > 0)
        {
            var described = calls.Select(n =>
                $"{n.Span.Attributes.GetValueOrDefault("http.request.method")} {n.Span.Attributes.GetValueOrDefault("url.full") ?? n.Span.Name} → {n.Span.Attributes.GetValueOrDefault("http.response.status_code") ?? "no status"}");
            lines.Add(new("http",
                $"{calls.Count} outgoing HTTP call{(calls.Count == 1 ? "" : "s")}: {string.Join(", ", described)}.",
                calls.Select(n => n.Span.SpanId).ToList()));
        }

        var slowest = nodes.Where(n => n.Kind != "interaction").OrderByDescending(n => n.Span.DurationMs).Take(3).ToList();
        if (slowest.Count > 0)
            lines.Add(new("slowest",
                $"Slowest recorded operations: {string.Join(", ", slowest.Select(n => $"{n.Span.Name} ({Ms(n.Span.DurationMs)} ms)"))}.",
                slowest.Select(n => n.Span.SpanId).ToList()));

        foreach (var node in nodes)
        foreach (var ev in node.Span.Events.Where(e => e.Name == "exception"))
            lines.Add(new("exception",
                $"{node.Span.Name} recorded {ev.Attributes.GetValueOrDefault("exception.type")}: {ev.Attributes.GetValueOrDefault("exception.message")}.",
                [node.Span.SpanId]));

        foreach (var node in nodes)
        foreach (var gap in node.Gaps)
            lines.Add(new("visibility", $"{node.Span.Name}: {gap.Message}", [node.Span.SpanId]));

        if (summary.Sampling == "app")
            lines.Add(new("sampling", "Only requests that your app's own sampler records are visible.", []));

        return lines;
    }

    private static IEnumerable<NodeView> Flatten(IEnumerable<NodeView> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
