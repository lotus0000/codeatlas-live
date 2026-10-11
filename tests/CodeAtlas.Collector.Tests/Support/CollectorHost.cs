using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace CodeAtlas.Collector.Tests.Support;

/// <summary>A real collector on 127.0.0.1 with a free port.</summary>
internal sealed class CollectorHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required string BaseUrl { get; init; }

    public static async Task<CollectorHost> StartAsync(params string[] args)
    {
        var app = CollectorApp.Create(["--urls=http://127.0.0.1:0", .. args], b => b.Logging.ClearProviders());
        await app.StartAsync();
        var baseUrl = app.Urls.First().TrimEnd('/');
        return new CollectorHost { App = app, Client = new HttpClient { BaseAddress = new Uri(baseUrl + "/") }, BaseUrl = baseUrl };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
