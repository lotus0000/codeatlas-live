using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.Collector.Api;

internal static class TraceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/info", (CollectorOptions options) => Results.Ok(new
        {
            version = CollectorApp.Version,
            sourceRoots = options.SourceRoots,
            editor = new { kind = options.EditorKind, available = false },
        }));
    }
}
