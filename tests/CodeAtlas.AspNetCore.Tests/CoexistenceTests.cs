using System.Collections.Concurrent;
using System.Diagnostics;
using CodeAtlas.AspNetCore.Tests.Support;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tests;

/// <summary>
/// GATE (spec §5.4, option B): beside an app with its own tracing, CodeAtlas is a pure observer. The app's sampling
/// decisions, exports, enrich callbacks, processors, activity flags and parent links must be identical with and without
/// it, and CodeAtlas shows exactly what the app samples. If any test here fails, stop and report to the maintainer.
/// </summary>
public class CoexistenceTests
{
    private static readonly ActivitySource AppSource = new("App.Work");

    public enum AppSampling { Everything, Nothing, ParentBased }

    // Fixed ids, so two runs can be compared request by request.
    private static readonly (string Path, string? TraceParent)[] Requests =
    [
        ("/work", "00-11111111111111111111111111111111-1111111111111111-01"),
        ("/work", "00-22222222222222222222222222222222-2222222222222222-00"),
        ("/work", null),
        ("/work", "00-33333333333333333333333333333333-3333333333333333-01"),
        ("/fail", "00-44444444444444444444444444444444-4444444444444444-01"),
    ];

    private sealed class Counters
    {
        public int Enrich;
        public int ProcessorStart;
        public int ProcessorEnd;
    }

    private sealed class CountingProcessor(Counters counters) : BaseProcessor<Activity>
    {
        public override void OnStart(Activity data) => Interlocked.Increment(ref counters.ProcessorStart);
        public override void OnEnd(Activity data) => Interlocked.Increment(ref counters.ProcessorEnd);
    }

    private sealed record Observation(
        List<string> Exported, List<string> InsideHandler, List<string> Downstream,
        (int Enrich, int Start, int End) Calls, TestApp App, AlwaysOnLoggerProvider Logs);

    private static Sampler SamplerFor(AppSampling sampling) => sampling switch
    {
        AppSampling.Everything => new AlwaysOnSampler(),
        AppSampling.Nothing => new AlwaysOffSampler(),
        _ => new ParentBasedSampler(new AlwaysOffSampler()),
    };

    private static async Task<Observation> RunAsync(AppSampling sampling, bool withCodeAtlas, Action<CodeAtlasOptions>? options = null)
    {
        var exported = new List<Activity>();
        var counters = new Counters();
        var inside = new ConcurrentQueue<string>();
        var stub = new StubServer();
        var logs = new AlwaysOnLoggerProvider();
        var app = await TestApp.StartAsync(
            a =>
            {
                a.MapGet("/work", async (IHttpClientFactory factory) =>
                {
                    using (AppSource.StartActivity("app-work")) { }
                    var current = Activity.Current;
                    inside.Enqueue(current is null
                        ? "none"
                        : $"{current.Source.Name}|{current.DisplayName}|recorded={current.Recorded}|alldata={current.IsAllDataRequested}|tags={current.TagObjects.Count()}");
                    using var response = await factory.CreateClient().GetAsync($"{stub.Url}/downstream");
                    return "ok";
                });
                a.MapGet("/fail", (Func<string>)(() => throw new InvalidOperationException("app failure")));
            },
            b =>
            {
                b.Logging.AddProvider(logs);
                b.Services.AddHttpClient();
                b.Services.AddOpenTelemetry().WithTracing(t => t
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = true;
                        o.EnrichWithHttpRequest = (activity, _) =>
                        {
                            Interlocked.Increment(ref counters.Enrich);
                            activity.SetTag("app.custom", "x");
                        };
                    })
                    .AddSource("App.Work", "System.Net.Http")
                    .SetSampler(SamplerFor(sampling))
                    .AddProcessor(new CountingProcessor(counters))
                    .AddInMemoryExporter(exported));
            },
            options,
            addCodeAtlas: withCodeAtlas);
        app.App.Lifetime.ApplicationStopped.Register(stub.Dispose);

        foreach (var (path, traceParent) in Requests)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (traceParent is not null) request.Headers.Add("traceparent", traceParent);
            try { (await app.Client.SendAsync(request)).Dispose(); }
            catch (Exception) { }
        }
        await Task.Delay(300);

        lock (exported)
            return new Observation(Shape(exported, stub.Port), [.. inside], [.. stub.TraceParents.Select(Downstream)],
                (counters.Enrich, counters.ProcessorStart, counters.ProcessorEnd), app, logs);
    }

    // What the app's own tracing tool would show, without per-run span ids and timings.
    // The downstream stub listens on a new port each run; that port is normalized away.
    private static List<string> Shape(List<Activity> spans, int stubPort) => spans.Select(a =>
    {
        var parent = spans.FirstOrDefault(p => p.SpanId == a.ParentSpanId);
        var parentName = a.ParentSpanId == default ? "root" : parent?.DisplayName ?? "remote-or-MISSING";
        var tags = string.Join(",", a.TagObjects.Select(t => $"{t.Key}={t.Value}".Replace(stubPort.ToString(), "<stub>")).Order());
        var events = string.Join(",", a.Events.Select(e => e.Name));
        return $"{a.Source.Name}|{a.DisplayName}|{a.Kind}|{a.Status}|{a.StatusDescription}|flags={a.ActivityTraceFlags}" +
               $"|alldata={a.IsAllDataRequested}|trace={FixedTrace(a.TraceId.ToHexString())}|parent={parentName}|events={events}|{tags}";
    }).Order().ToList();

    private static string FixedTrace(string traceId) => traceId.Distinct().Count() == 1 ? traceId : "random";

    private static string Downstream(string traceParent)
    {
        if (traceParent.Length == 0) return "no traceparent";
        var parts = traceParent.Split('-');
        return $"trace={FixedTrace(parts[1])}|flags={parts[3]}";
    }

    [Theory]
    [InlineData(AppSampling.Everything)]
    [InlineData(AppSampling.Nothing)]
    [InlineData(AppSampling.ParentBased)]
    public async Task The_apps_own_tracing_behaves_identically_with_and_without_CodeAtlas(AppSampling sampling)
    {
        var without = await RunAsync(sampling, withCodeAtlas: false);
        await without.App.DisposeAsync();
        var with = await RunAsync(sampling, withCodeAtlas: true);
        await using var _ = with.App;

        Assert.Equal(without.Exported, with.Exported);
        Assert.Equal(without.InsideHandler, with.InsideHandler);
        Assert.Equal(without.Downstream, with.Downstream);
        Assert.Equal(without.Calls, with.Calls);

        // The scenario really exercises the app's pipeline.
        if (sampling == AppSampling.Everything) Assert.True(with.Calls.Enrich >= 5 && with.Exported.Count > 0);
        if (sampling == AppSampling.Nothing) Assert.Equal((0, 0, 0), with.Calls);
        if (sampling == AppSampling.ParentBased) Assert.Equal(3, with.Calls.Enrich);
    }

    [Theory]
    [InlineData(AppSampling.Everything, 5)]
    [InlineData(AppSampling.Nothing, 0)]
    [InlineData(AppSampling.ParentBased, 3)] // the three requests that arrive with a sampled traceparent
    public async Task CodeAtlas_shows_exactly_what_the_app_samples_and_says_so(AppSampling sampling, int expectedRequests)
    {
        var run = await RunAsync(sampling, withCodeAtlas: true);
        await using var _ = run.App;

        var servers = run.App.Sink.Spans.Where(s => s.Scope == "Microsoft.AspNetCore" && s.Span.Kind == Span.Types.SpanKind.Server).ToList();
        Assert.Equal(expectedRequests, servers.Count);
        Assert.All(servers, s => Assert.Equal("app", s.Resource.Attributes.Single(a => a.Key == "codeatlas.sampling").Value.StringValue));
        Assert.Contains(run.Logs.Messages, m => m.StartsWith("Warning") && m.Contains("follows your app's sampling"));
    }

    [Fact]
    public async Task Opted_in_wrapper_spans_never_change_the_apps_sampling_or_callbacks()
    {
        var without = await RunAsync(AppSampling.ParentBased, withCodeAtlas: false);
        await without.App.DisposeAsync();
        var with = await RunAsync(AppSampling.ParentBased, withCodeAtlas: true, o => o.WrapperSpansBesideAppTracing = true);
        await using var _ = with.App;

        Assert.True(with.App.App.Services.GetRequiredService<CodeAtlasRuntime>().WrapperSpansEnabled);
        Assert.Equal(without.Calls, with.Calls);
        Assert.Equal(without.Downstream, with.Downstream);
        // Wrapper spans exist only inside requests the app records.
        Assert.Equal(3, with.App.Sink.Spans.Count(s => (string?)s.Attr("codeatlas.component") == "endpoint"));
    }

    [Fact]
    public async Task CodeAtlas_exports_never_appear_in_the_apps_own_traces()
    {
        using var collector = new FakeCollector();
        var exported = new List<Activity>();
        await using var app = await TestApp.StartAsync(
            a => a.MapGet("/ping", () => "pong"),
            b => b.Services.AddOpenTelemetry().WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddSource("System.Net.Http")
                .SetSampler(new AlwaysOnSampler())
                .AddInMemoryExporter(exported)),
            o => o.Endpoint = collector.TracesUrl,
            realHttpSink: true);

        for (var i = 0; i < 3; i++) await app.Client.GetStringAsync("/ping");
        var clock = Stopwatch.StartNew();
        while (collector.Received.IsEmpty && clock.ElapsedMilliseconds < 5000) await Task.Delay(50);
        await Task.Delay(300);

        Assert.False(collector.Received.IsEmpty);
        lock (exported)
            Assert.DoesNotContain(exported, a => a.TagObjects.Any(t =>
                t.Key is "server.port" or "url.full" && t.Value?.ToString()?.Contains(collector.Port.ToString()) == true));
    }
}
