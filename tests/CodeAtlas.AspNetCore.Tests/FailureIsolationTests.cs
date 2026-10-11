using System.Diagnostics;
using CodeAtlas.AspNetCore.Tests.Support;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeAtlas.AspNetCore.Tests;

public class FailureIsolationTests
{
    private static void Map(WebApplication app) => app.MapGet("/ping", () => "pong");

    private static CodeAtlasTracing Tracing(TestApp app) =>
        app.App.Services.GetServices<IHostedService>().OfType<CodeAtlasTracing>().Single();

    [Fact]
    public async Task A_stuck_collector_never_slows_or_fails_requests()
    {
        var sink = new BlockingSink();
        await using var app = await TestApp.StartAsync(Map, codeAtlas: o => o.QueueCapacity = 16, sink: sink);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            Assert.Equal("pong", await app.Client.GetStringAsync("/ping"));
        clock.Stop();
        sink.Release();

        Assert.True(clock.ElapsedMilliseconds < 5000, $"100 requests took {clock.ElapsedMilliseconds} ms");
        Assert.True(Tracing(app).Processor!.Dropped > 0);
    }

    [Fact]
    public async Task A_failing_collector_never_fails_requests()
    {
        var sink = new ThrowingSink();
        await using var app = await TestApp.StartAsync(Map, sink: sink);

        for (var i = 0; i < 20; i++)
            Assert.Equal("pong", await app.Client.GetStringAsync("/ping"));

        var clock = Stopwatch.StartNew();
        while (Tracing(app).Processor!.SendFailures == 0 && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.True(Tracing(app).Processor!.SendFailures > 0);
    }

    [Fact]
    public async Task An_unreachable_collector_with_the_real_sink_never_fails_requests()
    {
        var closedPort = FakeCollector.FreePort();
        await using var app = await TestApp.StartAsync(Map, codeAtlas: o => o.Endpoint = $"http://127.0.0.1:{closedPort}/v1/traces", realHttpSink: true);

        for (var i = 0; i < 20; i++)
            Assert.Equal("pong", await app.Client.GetStringAsync("/ping"));
    }

    [Fact]
    public async Task Exports_to_the_collector_are_not_traced_themselves()
    {
        using var collector = new FakeCollector();
        await using var app = await TestApp.StartAsync(Map, codeAtlas: o => o.Endpoint = collector.TracesUrl, realHttpSink: true);

        for (var i = 0; i < 5; i++) await app.Client.GetStringAsync("/ping");
        var clock = Stopwatch.StartNew();
        while (collector.Received.Count < 2 && clock.ElapsedMilliseconds < 5000) await Task.Delay(50);
        await Task.Delay(500);

        var urls = collector.Received
            .SelectMany(r => r.Request.ResourceSpans).SelectMany(r => r.ScopeSpans).SelectMany(s => s.Spans)
            .SelectMany(s => s.Attributes).Where(a => a.Key is "url.full" or "server.port").Select(a => a.Value.ToString());
        Assert.DoesNotContain(urls, u => u.Contains(collector.Port.ToString()));
    }
}
