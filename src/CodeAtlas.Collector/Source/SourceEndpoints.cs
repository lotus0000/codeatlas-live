using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.Collector.Source;

internal static class SourceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Identity only: the file comes from a verified PDB inside a source root, never from the request.
        app.MapGet("/api/source", (PdbSourceMap map, string assembly, Guid mvid, int token) =>
        {
            var location = map.Resolve(assembly, mvid, token);
            return Results.Ok(new { location, text = location.Found ? System.IO.File.ReadAllText(location.File!) : null });
        });
    }
}
