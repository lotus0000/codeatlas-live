using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace CodeAtlas.Collector;

/// <summary>The page at "/" until the viewer has been built into wwwroot (a fresh clone has no build output).</summary>
internal static class ViewerFallback
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>CodeAtlas Live</title></head>
        <body style="font-family: system-ui, sans-serif; margin: 3rem; line-height: 1.5;">
        <h1>CodeAtlas collector is running</h1>
        <p>The viewer has not been built into this collector yet. In the codeatlas-live repository run
        <code>scripts/build-viewer.sh</code>, then reload this page.</p>
        </body>
        </html>
        """;

    public static bool Applies(PathString path, IFileProvider webRoot) =>
        path == "/" && !webRoot.GetFileInfo("index.html").Exists;
}
