using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using CodeAtlas.AspNetCore.Tests.Support;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tests;

/// <summary>Final-review findings: CodeAtlas must not change what the app or its neighbours observe.</summary>
public class NonInterferenceTests
{
    [Fact]
    public async Task Export_traffic_never_appears_in_the_apps_http_client_metrics()
    {
        using var collector = new FakeCollector();
        var metrics = new List<Metric>();
        await using var app = await TestApp.StartAsync(
            a => a.MapGet("/ping", () => "pong"),
            b => b.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("System.Net.Http").AddInMemoryExporter(metrics)),
            o => o.Endpoint = collector.TracesUrl,
            realHttpSink: true);

        for (var i = 0; i < 5; i++) await app.Client.GetStringAsync("/ping");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (collector.Received.IsEmpty && clock.ElapsedMilliseconds < 5000) await Task.Delay(50);
        Assert.False(collector.Received.IsEmpty);
        app.App.Services.GetRequiredService<MeterProvider>().ForceFlush();

        Assert.DoesNotContain(collector.Port.ToString(), ServerPorts(metrics));
    }

    private static List<string> ServerPorts(List<Metric> metrics)
    {
        var ports = new List<string>();
        foreach (var metric in metrics)
        foreach (ref readonly var point in metric.GetMetricPoints())
        foreach (var tag in point.Tags)
            if (tag.Key == "server.port") ports.Add(tag.Value?.ToString() ?? "");
        return ports;
    }

    [Theory]
    [InlineData("01")]
    [InlineData("00")]
    [InlineData(null)]
    public async Task The_sampled_flag_sent_downstream_is_unchanged_when_the_app_has_no_tracing(string? incomingFlag)
    {
        var without = await DownstreamTraceparentAsync(incomingFlag, withCodeAtlas: false);
        var with = await DownstreamTraceparentAsync(incomingFlag, withCodeAtlas: true);

        Assert.NotEqual("", without);
        Assert.Equal(without[^2..], with[^2..]);
    }

    private static async Task<string> DownstreamTraceparentAsync(string? incomingFlag, bool withCodeAtlas)
    {
        using var stub = new StubServer();
        await using var app = await TestApp.StartAsync(
            a => a.MapGet("/call", async (IHttpClientFactory factory) =>
            {
                using var response = await factory.CreateClient().GetAsync($"{stub.Url}/x");
                return "ok";
            }),
            b =>
            {
                b.Services.AddHttpClient();
                b.Logging.AddProvider(new AlwaysOnLoggerProvider());
            },
            addCodeAtlas: withCodeAtlas);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/call");
        if (incomingFlag is not null)
            request.Headers.Add("traceparent", $"00-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}-{incomingFlag}");
        (await app.Client.SendAsync(request)).Dispose();
        return stub.TraceParents.Single();
    }

    [Fact]
    public async Task Header_values_from_the_apps_own_instrumentation_never_leave()
    {
        await using var app = await TestApp.StartAsync(
            a => a.MapGet("/ping", () => "pong"),
            b => b.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().SetSampler(new AlwaysOnSampler())));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("User-Agent", "ProbeAgent/9.9-SECRETUA");

        (await app.Client.SendAsync(request)).Dispose();

        await app.Sink.WaitForAsync(s => s.Any(x => x.Scope == "Microsoft.AspNetCore"));
        Assert.DoesNotContain("SECRETUA", string.Join("\n", app.Sink.Requests.Select(r => Encoding.UTF8.GetString(r.ToByteArray()))));
    }
}
