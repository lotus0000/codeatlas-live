using CodeAtlas.Collector.Store;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.Collector.Ingest;

internal static class IngestEndpoints
{
    private static readonly byte[] EmptyResponse = new ExportTraceServiceResponse().ToByteArray();

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/traces", async (HttpRequest request, TraceStore store, Live.LiveNotifier live) =>
        {
            ExportTraceServiceRequest export;
            try { export = await OtlpReader.ReadAsync(request); }
            catch (BadHttpRequestException e) { return Results.Text(e.Message, statusCode: e.StatusCode); }

            await live.NotifyAsync(store.Add(export));
            return request.ContentType!.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                ? Results.Text("{}", "application/json")
                : Results.Bytes(EmptyResponse, "application/x-protobuf");
        }).RequireCors(CollectorApp.IngestCors);

        app.MapGet("/api/traces/{traceId}/spans", (string traceId, TraceStore store) =>
            store.Get(traceId) is { } spans ? Results.Ok(spans.OrderBy(s => s.StartUnixNano)) : Results.NotFound());
    }
}
