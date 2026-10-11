using CodeAtlas.Collector.Source;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.Collector.Editor;

internal sealed record SessionToken(string Value);

public sealed record OpenRequest(string Assembly, Guid Mvid, int Token);

internal static class EditorEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // No CORS on this route: only same-origin pages can read the token.
        app.MapGet("/api/session", (SessionToken session) => Results.Ok(new { token = session.Value }));

        app.MapPost("/api/open", async (HttpRequest request, OpenRequest body, SessionToken session, CollectorOptions options, PdbSourceMap map, EditorLauncher editor) =>
        {
            if (request.Headers["X-CodeAtlas-Token"] != session.Value)
                return Results.Problem("Missing or wrong session token.", statusCode: StatusCodes.Status401Unauthorized);
            if (!OriginAllowed(request, options))
                return Results.Problem("This page's origin may not open files.", statusCode: StatusCodes.Status403Forbidden);

            // Only an identity comes in; file and line come from the verified PDB mapping.
            var location = map.Resolve(body.Assembly, body.Mvid, body.Token);
            if (!location.Found) return Results.Problem(location.Reason, statusCode: StatusCodes.Status422UnprocessableEntity);

            var (ok, message) = await editor.OpenAsync(location.File!, location.NavigationLine);
            return ok
                ? Results.Ok(new { message, file = location.File, line = location.NavigationLine })
                : Results.Problem(message, statusCode: StatusCodes.Status502BadGateway);
        }).Accepts<OpenRequest>("application/json");
    }

    // Same-origin page (Origin authority equals Host), the configured public URL, or a non-browser client (no Origin).
    private static bool OriginAllowed(HttpRequest request, CollectorOptions options)
    {
        if (request.Headers.Origin is not { Count: > 0 } values) return true;
        if (!Uri.TryCreate(values.ToString(), UriKind.Absolute, out var origin)) return false;
        if (options.PublicUrl is { } publicUrl && origin.GetLeftPart(UriPartial.Authority) == publicUrl.GetLeftPart(UriPartial.Authority)) return true;
        return string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
