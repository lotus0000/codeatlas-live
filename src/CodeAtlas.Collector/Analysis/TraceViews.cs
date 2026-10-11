using System.Text.Json.Serialization;
using CodeAtlas.Collector.Store;

namespace CodeAtlas.Collector.Analysis;

public sealed record CodeRef(string Assembly, string Mvid, long Token, string Function, string Evidence);

public sealed record Gap(string Kind, string Message);

public sealed record NodeView(
    SpanRecord Span, string Kind, string Evidence, string? Correlation, CodeRef? Code,
    IReadOnlyList<Gap> Gaps, IReadOnlyList<NodeView> Children);

public sealed record TraceSummary(
    string TraceId,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong StartUnixNano,
    double DurationMs, string? Interaction, string? Correlation, string? Method, string? Route, long? StatusCode,
    bool HasError, int SpanCount, string Sampling);

public sealed record ExplainLine(string Kind, string Text, IReadOnlyList<string> SpanIds);

public sealed record TraceView(TraceSummary Summary, IReadOnlyList<NodeView> Roots, bool Truncated, IReadOnlyList<ExplainLine> Explanation);
