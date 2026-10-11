using System.Net.Http.Json;
using System.Security.Cryptography;
using CodeAtlas.AspNetCore.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace CodeAtlas.AspNetCore.Tests.Support;

internal sealed class TestApp : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required RecordingSink Sink { get; init; }

    public static async Task<TestApp> StartAsync(
        Action<WebApplication> map,
        Action<WebApplicationBuilder>? configure = null,
        Action<CodeAtlasOptions>? codeAtlas = null,
        string environment = "Development",
        bool addCodeAtlas = true,
        bool codeAtlasFirst = false,
        ISpanSink? sink = null,
        bool realHttpSink = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(TestApp).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var recording = new RecordingSink();

        void AddAtlas() => builder.AddCodeAtlas(o =>
        {
            if (!realHttpSink) o.SinkFactory = _ => sink ?? recording;
            o.BatchDelay = TimeSpan.FromMilliseconds(10);
            codeAtlas?.Invoke(o);
        });

        if (addCodeAtlas && codeAtlasFirst) AddAtlas();
        configure?.Invoke(builder);
        if (addCodeAtlas && !codeAtlasFirst) AddAtlas();

        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return new TestApp { App = app, Client = app.GetTestClient(), Sink = recording };
    }

    /// <summary>Sends a request with a fresh sampled traceparent and returns the trace id to look the spans up by.</summary>
    public async Task<(HttpResponseMessage Response, string TraceId)> SendAsync(HttpMethod method, string url, object? json = null)
    {
        var traceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var parentId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("traceparent", $"00-{traceId}-{parentId}-01");
        if (json is not null) request.Content = JsonContent.Create(json);
        HttpResponseMessage response;
        try { response = await Client.SendAsync(request); }
        catch (Exception) { response = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError); }
        return (response, traceId);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
