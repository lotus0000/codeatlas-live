using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.Collector.Api;

internal static class TraceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/info", (CollectorOptions options, Editor.EditorLauncher editor) => Results.Ok(new
        {
            version = CollectorApp.Version,
            sourceRoots = options.SourceRoots,
            editor = new { kind = editor.Kind, available = editor.Available },
        }));

        app.MapGet("/api/traces", (Store.TraceStore store, int? limit) =>
            store.RecentTraceIds(Math.Clamp(limit ?? 50, 1, 500))
                .Select(id => store.Get(id) is { Count: > 0 } spans ? Analysis.TraceAnalyzer.Summarize(id, spans) : null)
                .OfType<Analysis.TraceSummary>());

        app.MapGet("/api/traces/{traceId}", (string traceId, Store.TraceStore store) =>
            store.Get(traceId) is { Count: > 0 } spans
                ? Results.Ok(Analysis.TraceAnalyzer.Analyze(traceId, spans, store.IsTruncated(traceId), DateTimeOffset.UtcNow))
                : Results.NotFound());
    }
}
