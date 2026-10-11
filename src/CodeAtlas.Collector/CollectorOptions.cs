using Microsoft.Extensions.Configuration;

namespace CodeAtlas.Collector;

public sealed record StoreLimits(int MaxTraces = 500, int MaxSpansPerTrace = 2000, int MaxAttributeLength = 4096);

/// <summary>Validated settings. Throws on anything that would weaken the loopback-only guarantee.</summary>
public sealed record CollectorOptions(
    IReadOnlyList<Uri> Urls,
    IReadOnlyList<string> SourceRoots,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> AllowedHosts,
    Uri? PublicUrl,
    string EditorKind,
    string? EditorCommand,
    StoreLimits Limits)
{
    private static readonly HashSet<string> LoopbackHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];

    /// <summary>Path prefix the app is served under when behind a path proxy, e.g. "/absproxy/4318".</summary>
    public string? PathBase => PublicUrl is null || PublicUrl.AbsolutePath == "/" ? null : PublicUrl.AbsolutePath.TrimEnd('/');

    public static CollectorOptions From(IConfiguration configuration, string currentDirectory)
    {
        var urls = (configuration["urls"] ?? "http://127.0.0.1:4318")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : throw new InvalidOperationException($"'{u}' is not a URL."))
            .ToList();
        foreach (var url in urls)
            if (!LoopbackHosts.Contains(url.Host))
                throw new InvalidOperationException(
                    $"The CodeAtlas collector only listens on loopback addresses (127.0.0.1, localhost, [::1]); '{url}' is not one. " +
                    "To reach it from another machine, use a proxy you control, such as code-server's /absproxy/, and pass --public-url.");

        var publicUrl = configuration["CodeAtlas:PublicUrl"] is { Length: > 0 } raw
            ? Uri.TryCreate(raw, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"
                ? parsed
                : throw new InvalidOperationException($"--public-url '{raw}' is not an absolute http(s) URL.")
            : null;

        var roots = List(configuration["CodeAtlas:SourceRoots"]);
        var hosts = new List<string> { "localhost", "127.0.0.1", "[::1]" };
        if (publicUrl is not null) hosts.Add(publicUrl.Host);

        return new CollectorOptions(
            urls,
            roots.Count > 0 ? roots.Select(Path.GetFullPath).ToList() : [Path.GetFullPath(currentDirectory)],
            List(configuration["CodeAtlas:AllowedOrigins"] ?? "http://localhost:5173,http://127.0.0.1:5173"),
            hosts,
            publicUrl,
            configuration["CodeAtlas:Editor:Kind"] ?? "code-server",
            configuration["CodeAtlas:Editor:Command"],
            new StoreLimits());
    }

    private static List<string> List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
