using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CodeAtlas.AspNetCore.Tests.Support;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tests;

/// <summary>
/// GATE (spec §5.4): an app with its own tracing must export exactly the same data with and without CodeAtlas.
/// If any test here fails, stop implementation and report the failing output to the maintainer.
/// </summary>
public class CoexistenceTests
{
    private static readonly ActivitySource AppSource = new("App.Work");

    private static Action<WebApplicationBuilder> AppTracing(List<Activity> exported, Sampler sampler) => b =>
        b.Services.AddOpenTelemetry().WithTracing(t => t
            .AddAspNetCoreInstrumentation(o =>
            {
                o.RecordException = true;
                o.EnrichWithHttpRequest = (activity, _) => activity.SetTag("app.custom", "x");
            })
            .AddSource("App.Work")
            .SetSampler(sampler)
            .AddInMemoryExporter(exported));

    private static void Map(WebApplication app)
    {
        app.MapGet("/work", () =>
        {
            using (AppSource.StartActivity("app-work")) { }
            return Results.Ok(new { recorded = Activity.Current?.Recorded ?? false });
        });
        app.MapGet("/fail", (Func<string>)(() => throw new InvalidOperationException("app failure")));
    }

    // What the app's own tracing tool would show, without per-run ids and timings.
    private static List<string> Shape(IEnumerable<Activity> spans)
    {
        var list = spans.ToList();
        return list.Select(a =>
        {
            var parent = list.FirstOrDefault(p => p.SpanId == a.ParentSpanId);
            var parentName = a.ParentSpanId == default ? "root" : parent?.DisplayName ?? "MISSING";
            var tags = string.Join(",", a.TagObjects.Select(t => $"{t.Key}={t.Value}").Order());
            var events = string.Join(",", a.Events.Select(e => e.Name));
            return $"{a.Source.Name}|{a.DisplayName}|{a.Kind}|{a.Status}|{a.StatusDescription}|recorded={a.Recorded}|parent={parentName}|events={events}|{tags}";
        }).Order().ToList();
    }

    private static async Task<(List<string> Shape, List<bool> Recorded, TestApp App)> RunAsync(Func<Sampler> sampler, bool withCodeAtlas, Action<CodeAtlasOptions>? options = null)
    {
        var exported = new List<Activity>();
        var app = await TestApp.StartAsync(Map, AppTracing(exported, sampler()), options, addCodeAtlas: withCodeAtlas);
        var recorded = new List<bool>();
        for (var i = 0; i < 3; i++)
            recorded.Add((await app.Client.GetFromJsonAsync<JsonElement>("/work")).GetProperty("recorded").GetBoolean());
        await app.SendAsync(HttpMethod.Get, "/fail");
        await Task.Delay(300);
        lock (exported) return (Shape(exported), recorded, app);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task App_exports_and_sampling_are_identical_with_and_without_CodeAtlas(bool sampleEverything)
    {
        Sampler Sampler() => sampleEverything ? new AlwaysOnSampler() : new AlwaysOffSampler();

        var (without, recordedWithout, plain) = await RunAsync(Sampler, withCodeAtlas: false);
        await plain.DisposeAsync();
        var (with, recordedWith, traced) = await RunAsync(Sampler, withCodeAtlas: true);
        await using var _ = traced;

        Assert.Equal(without, with);
        Assert.Equal(recordedWithout, recordedWith);
        // 4 server spans (/work ×3, /fail) + 3 app-work spans when sampled; nothing otherwise.
        Assert.Equal(sampleEverything ? 7 : 0, with.Count);
    }

    [Fact]
    public async Task CodeAtlas_still_sees_every_request_when_the_app_samples_nothing()
    {
        var (_, _, app) = await RunAsync(() => new AlwaysOffSampler(), withCodeAtlas: true);
        await using var _ = app;

        await app.Sink.WaitForAsync(s => s.Count(x => x.Span.Kind == Span.Types.SpanKind.Server) >= 4);
    }

    [Fact]
    public async Task Wrapper_spans_are_off_beside_app_tracing_and_can_be_opted_in()
    {
        var (_, _, off) = await RunAsync(() => new AlwaysOnSampler(), withCodeAtlas: true);
        Assert.False(off.App.Services.GetRequiredService<CodeAtlasRuntime>().WrapperSpansEnabled);
        await off.DisposeAsync();

        var (_, _, on) = await RunAsync(() => new AlwaysOnSampler(), withCodeAtlas: true, o => o.WrapperSpansBesideAppTracing = true);
        await using var _ = on;
        Assert.True(on.App.Services.GetRequiredService<CodeAtlasRuntime>().WrapperSpansEnabled);
    }
}
