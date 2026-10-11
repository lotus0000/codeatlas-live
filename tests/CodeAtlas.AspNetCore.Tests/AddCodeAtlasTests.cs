using CodeAtlas.AspNetCore.Tests.Support;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tests;

public class AddCodeAtlasTests
{
    private static void Map(WebApplication app)
    {
        app.MapGet("/orders/{id:int}", (int id) => id);
        app.MapGet("/boom", (Func<string>)(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Does_nothing_outside_Development()
    {
        await using var app = await TestApp.StartAsync(Map, environment: "Production");

        await app.SendAsync(HttpMethod.Get, "/orders/1");
        await Task.Delay(300);

        Assert.Null(app.App.Services.GetService<CodeAtlasRuntime>());
        Assert.Empty(app.Sink.Spans);
    }

    [Fact]
    public async Task Config_switch_enables_it_outside_Development()
    {
        await using var app = await TestApp.StartAsync(Map, b => b.Configuration["CodeAtlas:Enabled"] = "true", environment: "Production");

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/1");

        Assert.NotEmpty(await app.Sink.WaitForTraceAsync(traceId));
    }

    [Fact]
    public async Task Records_the_server_span_with_route_method_status_and_resource()
    {
        await using var app = await TestApp.StartAsync(Map);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/7");

        var server = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        Assert.Equal("Microsoft.AspNetCore", server.Scope);
        Assert.Equal("GET /orders/{id:int}", server.Name);
        Assert.Equal("/orders/{id:int}", server.Attr("http.route"));
        Assert.Equal(200L, server.Attr("http.response.status_code"));
        Assert.Equal("CodeAtlas.AspNetCore.Tests", server.Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue);
        Assert.False(server.Resource.Attributes.Single(a => a.Key == "codeatlas.app_has_tracing").Value.BoolValue);
    }

    [Fact]
    public async Task Continues_the_incoming_traceparent()
    {
        await using var app = await TestApp.StartAsync(Map);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/1");

        var server = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        Assert.Equal(traceId, server.TraceId);
        Assert.Equal(16, server.ParentSpanId.Length);
    }

    // In Development the framework's developer exception page turns the exception into a 500 response inside the
    // pipeline, so the exception never escapes it: the server span is an error, but not "unhandled".
    [Fact]
    public async Task Exceptions_caught_by_the_developer_page_mark_the_server_span_as_a_500_error()
    {
        await using var app = await TestApp.StartAsync(Map);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/boom");

        var server = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        Assert.True(server.IsError);
        Assert.Equal(500L, server.Attr("http.response.status_code"));
        Assert.Null(server.Attr("codeatlas.http.unhandled_exception"));
    }

    [Fact]
    public async Task Exceptions_escaping_the_whole_pipeline_are_marked_unhandled()
    {
        await using var app = await TestApp.StartAsync(Map, b => b.Configuration["CodeAtlas:Enabled"] = "true", environment: "Production");

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/boom");

        var server = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        Assert.True(server.IsError);
        Assert.Equal(true, server.Attr("codeatlas.http.unhandled_exception"));
        Assert.Equal(500L, server.Attr("http.response.status_code"));
    }

    [Fact]
    public async Task Parallel_requests_get_their_own_http_facts()
    {
        await using var app = await TestApp.StartAsync(a => a.MapGet("/items/{i:int}", (int i) => i % 2 == 0 ? Results.Ok(i) : Results.NotFound()));

        var sent = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => app.SendAsync(HttpMethod.Get, $"/items/{i}")));

        await app.Sink.WaitForAsync(s => s.Count(x => x.Span.Kind == Span.Types.SpanKind.Server) >= 50);
        for (var i = 0; i < 50; i++)
        {
            var server = app.Sink.Spans.Single(s => s.TraceId == sent[i].TraceId && s.Span.Kind == Span.Types.SpanKind.Server);
            Assert.Equal("/items/{i:int}", server.Attr("http.route"));
            Assert.Equal(i % 2 == 0 ? 200L : 404L, server.Attr("http.response.status_code"));
        }
    }

    [Fact]
    public async Task Metrics_only_OpenTelemetry_is_not_app_tracing()
    {
        await using var app = await TestApp.StartAsync(Map, b => b.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("App")));

        var runtime = app.App.Services.GetRequiredService<CodeAtlasRuntime>();
        Assert.False(runtime.AppHasTracing);
        Assert.True(runtime.WrapperSpansEnabled);
    }

    [Fact]
    public async Task Detects_app_tracing_even_when_AddCodeAtlas_comes_first()
    {
        await using var app = await TestApp.StartAsync(Map, b => b.Services.AddOpenTelemetry().WithTracing(t => t.AddSource("App")), codeAtlasFirst: true);

        var runtime = app.App.Services.GetRequiredService<CodeAtlasRuntime>();
        Assert.True(runtime.AppHasTracing);
        Assert.False(runtime.WrapperSpansEnabled);
    }

    [Fact]
    public async Task Enabled_false_turns_it_off_even_in_Development()
    {
        await using var app = await TestApp.StartAsync(Map, b => b.Configuration["CodeAtlas:Enabled"] = "false");

        await app.SendAsync(HttpMethod.Get, "/orders/1");
        await Task.Delay(300);

        Assert.Null(app.App.Services.GetService<CodeAtlasRuntime>());
        Assert.Empty(app.Sink.Spans);
    }

    [Fact]
    public async Task Calling_AddCodeAtlas_twice_registers_it_once_and_warns()
    {
        var sink = new RecordingSink();
        var logs = new AlwaysOnLoggerProvider();
        await using var app = await TestApp.StartAsync(
            Map,
            b =>
            {
                b.Logging.AddProvider(logs);
                b.AddCodeAtlas(o => { o.SinkFactory = _ => sink; o.BatchDelay = TimeSpan.FromMilliseconds(10); });
            },
            sink: sink);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/1");
        await sink.WaitForTraceAsync(traceId);
        await Task.Delay(300);

        Assert.Single(app.App.Services.GetServices<IHostedService>().OfType<CodeAtlasTracing>());
        Assert.Single(sink.Spans, s => s.TraceId == traceId && s.Span.Kind == Span.Types.SpanKind.Server);
        Assert.Single(sink.Spans, s => s.TraceId == traceId && (string?)s.Attr("codeatlas.component") == "endpoint");
        Assert.Contains(logs.Messages, m => m.StartsWith("Warning") && m.Contains("AddCodeAtlas was called more than once"));
    }

    [Fact]
    public async Task An_unreadable_Enabled_value_is_ignored_with_a_warning()
    {
        var logs = new AlwaysOnLoggerProvider();
        await using var app = await TestApp.StartAsync(Map, b =>
        {
            b.Logging.AddProvider(logs);
            b.Configuration["CodeAtlas:Enabled"] = "yes please";
        });

        var (response, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/1");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.NotEmpty(await app.Sink.WaitForTraceAsync(traceId)); // Development default still applies
        Assert.Contains(logs.Messages, m => m.StartsWith("Warning") && m.Contains("CodeAtlas:Enabled"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1/v1/traces")]
    public async Task An_invalid_endpoint_disables_CodeAtlas_instead_of_crashing_the_app(string endpoint)
    {
        var logs = new AlwaysOnLoggerProvider();
        await using var app = await TestApp.StartAsync(Map, b =>
        {
            b.Logging.AddProvider(logs);
            b.Configuration["CodeAtlas:Endpoint"] = endpoint;
        }, realHttpSink: true);

        var (response, _) = await app.SendAsync(HttpMethod.Get, "/orders/1");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Null(app.App.Services.GetServices<IHostedService>().OfType<CodeAtlasTracing>().Single().Processor);
        Assert.Contains(logs.Messages, m => m.StartsWith("Warning") && m.Contains("CodeAtlas is disabled") && m.Contains(endpoint));
    }

    [Fact]
    public async Task Invalid_queue_and_timeout_settings_fall_back_to_defaults()
    {
        var logs = new AlwaysOnLoggerProvider();
        await using var app = await TestApp.StartAsync(Map, b => b.Logging.AddProvider(logs), o =>
        {
            o.QueueCapacity = 0;
            o.ExportTimeout = TimeSpan.FromSeconds(-1);
        });

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/1");

        Assert.NotEmpty(await app.Sink.WaitForTraceAsync(traceId));
        Assert.Contains(logs.Messages, m => m.Contains("QueueCapacity"));
        Assert.Contains(logs.Messages, m => m.Contains("ExportTimeout"));
    }
}
