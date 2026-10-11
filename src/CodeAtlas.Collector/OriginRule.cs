using Microsoft.AspNetCore.Http;

namespace CodeAtlas.Collector;

/// <summary>
/// Which browser pages may use the collector's own endpoints (live updates, opening files): a same-origin page
/// (Origin authority equals Host), the configured public URL, or a non-browser client (no Origin header).
/// CORS does not cover WebSockets, so this check is applied explicitly.
/// </summary>
internal static class OriginRule
{
    public static bool Allowed(HttpRequest request, CollectorOptions options)
    {
        if (request.Headers.Origin is not { Count: > 0 } values) return true;
        if (!Uri.TryCreate(values.ToString(), UriKind.Absolute, out var origin)) return false;
        if (options.PublicUrl is { } publicUrl && origin.GetLeftPart(UriPartial.Authority) == publicUrl.GetLeftPart(UriPartial.Authority)) return true;
        return string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
