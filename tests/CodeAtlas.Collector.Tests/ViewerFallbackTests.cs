using Microsoft.Extensions.FileProviders;

namespace CodeAtlas.Collector.Tests;

public class ViewerFallbackTests
{
    [Fact]
    public void The_fallback_applies_only_to_the_root_and_only_without_a_built_viewer()
    {
        var dir = Directory.CreateTempSubdirectory("codeatlas-webroot-");
        try
        {
            using (var empty = new PhysicalFileProvider(dir.FullName))
            {
                Assert.True(ViewerFallback.Applies("/", empty));
                Assert.False(ViewerFallback.Applies("/api/info", empty));
            }
            File.WriteAllText(Path.Combine(dir.FullName, "index.html"), "<!doctype html>");
            using var built = new PhysicalFileProvider(dir.FullName);
            Assert.False(ViewerFallback.Applies("/", built));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_fallback_page_says_how_to_build_the_viewer()
    {
        Assert.Contains("scripts/build-viewer.sh", ViewerFallback.Html);
        Assert.Contains("CodeAtlas", ViewerFallback.Html);
    }
}
