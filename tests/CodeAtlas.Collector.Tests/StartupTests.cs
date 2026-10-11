using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeAtlas.Collector.Tests.Support;

namespace CodeAtlas.Collector.Tests;

public class StartupTests
{
    [Theory]
    [InlineData("http://0.0.0.0:4318")]
    [InlineData("http://192.168.1.10:4318")]
    [InlineData("http://example.com:4318")]
    public void Refuses_to_listen_anywhere_but_loopback(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CollectorApp.Create([$"--urls={url}"]));
        Assert.Contains("loopback", error.Message);
    }

    [Fact]
    public async Task Answers_info_on_loopback()
    {
        await using var host = await CollectorHost.StartAsync("--source-root=/tmp");

        var info = await host.Client.GetFromJsonAsync<JsonElement>("api/info");

        Assert.Equal("0.1.0", info.GetProperty("version").GetString());
        Assert.Equal(["/tmp"], info.GetProperty("sourceRoots").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Rejects_requests_for_other_host_names()
    {
        await using var host = await CollectorHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/info");
        request.Headers.Host = "evil.example";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_public_url_adds_its_host_and_path_base()
    {
        await using var host = await CollectorHost.StartAsync("--public-url=https://code.example/absproxy/4318/");
        using var request = new HttpRequestMessage(HttpMethod.Get, "absproxy/4318/api/info");
        request.Headers.Host = "code.example";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Serves_the_viewer_folder()
    {
        await using var host = await CollectorHost.StartAsync();

        var html = await host.Client.GetStringAsync("");

        Assert.Contains("CodeAtlas", html);
    }
}
