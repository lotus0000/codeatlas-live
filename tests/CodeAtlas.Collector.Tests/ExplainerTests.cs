using CodeAtlas.Collector.Analysis;
using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace CodeAtlas.Collector.Tests;

public class ExplainerTests
{
    private static readonly string T = TestOtlp.TraceId(1);
    private static readonly DateTimeOffset Later = DateTimeOffset.FromUnixTimeMilliseconds((long)(TestOtlp.Epoch / 1_000_000) + 60_000);

    private static TraceView View(string correlation = "explicit", bool fail = false, string sampling = "codeatlas")
    {
        var store = new TraceStore(new StoreLimits());
        void Add(string service, string scope, OpenTelemetry.Proto.Trace.V1.Span span) =>
            store.Add(TestOtlp.Request(service, scope, [span], ("codeatlas.sampling", sampling)));
        Add("web", "codeatlas-web", TestOtlp.Span(T, TestOtlp.SpanId(1), null, "interaction: Create order", SpanKind.Internal, 0, 0,
            ("codeatlas.interaction.name", "Create order"), ("codeatlas.correlation", correlation)));
        Add("web", "codeatlas-web", TestOtlp.Span(T, TestOtlp.SpanId(2), TestOtlp.SpanId(1), "POST /api/orders", SpanKind.Client, 1, 60,
            ("http.request.method", "POST"), ("codeatlas.correlation", correlation)));
        Add("api", "Microsoft.AspNetCore", TestOtlp.Span(T, TestOtlp.SpanId(3), TestOtlp.SpanId(2), "POST /api/orders", SpanKind.Server, 2, 58,
            ("http.request.method", "POST"), ("http.route", "/api/orders"), ("http.response.status_code", fail ? 500 : 201)));
        Add("api", "CodeAtlas.Components", TestOtlp.Span(T, TestOtlp.SpanId(4), TestOtlp.SpanId(3), "endpoint POST /api/orders", SpanKind.Internal, 3, 57,
            ("codeatlas.component", "endpoint"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.token", 1), ("codeatlas.code.evidence", "routing")));
        var handler = TestOtlp.Span(T, TestOtlp.SpanId(5), TestOtlp.SpanId(4), "handler OrderEndpoints.Create", SpanKind.Internal, 4, 56,
            ("codeatlas.component", "handler"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.token", 1), ("codeatlas.code.evidence", "executed"));
        if (fail)
        {
            handler.Status = new OpenTelemetry.Proto.Trace.V1.Status { Code = OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error, Message = "Out of stock" };
            handler.Events.Add(new OpenTelemetry.Proto.Trace.V1.Span.Types.Event
            {
                Name = "exception",
                Attributes = { TestOtlp.Kv("exception.type", "System.InvalidOperationException"), TestOtlp.Kv("exception.message", "Out of stock") },
            });
        }
        Add("api", "CodeAtlas.Components", handler);
        Add("api", "Npgsql", TestOtlp.Span(T, TestOtlp.SpanId(6), TestOtlp.SpanId(5), "SELECT products", SpanKind.Client, 5, 15, ("db.system.name", "postgresql")));
        Add("api", "Npgsql", TestOtlp.Span(T, TestOtlp.SpanId(7), TestOtlp.SpanId(5), "INSERT orders", SpanKind.Client, 16, 40, ("db.system.name", "postgresql")));
        Add("api", "Npgsql", TestOtlp.Span(T, TestOtlp.SpanId(8), TestOtlp.SpanId(5), "SELECT products", SpanKind.Client, 41, 45, ("db.system.name", "postgresql")));
        Add("api", "System.Net.Http", TestOtlp.Span(T, TestOtlp.SpanId(9), TestOtlp.SpanId(5), "GET", SpanKind.Client, 46, 55,
            ("http.request.method", "GET"), ("url.full", "https://stock.example/api/check"), ("http.response.status_code", 200)));
        return TraceAnalyzer.Analyze(T, store.Get(T)!, false, Later);
    }

    private static string Text(TraceView view, string kind) => string.Join(" | ", view.Explanation.Where(l => l.Kind == kind).Select(l => l.Text));

    [Fact]
    public void Explains_a_successful_request_from_its_spans()
    {
        var view = View();

        Assert.Equal("The interaction \"Create order\" triggered this request (explicit link).", Text(view, "entry"));
        Assert.Equal("POST /api/orders returned 201 in 56.0 ms.", Text(view, "request"));
        Assert.Equal("Executed Shop.OrderEndpoints.Create (handler).", Text(view, "executed"));
        Assert.Equal("3 database operations: SELECT products ×2, INSERT orders ×1.", Text(view, "database"));
        Assert.Equal("1 outgoing HTTP call: GET https://stock.example/api/check → 200.", Text(view, "http"));
        Assert.Equal("Slowest recorded operations: POST /api/orders (59.0 ms), POST /api/orders (56.0 ms), endpoint POST /api/orders (54.0 ms).", Text(view, "slowest"));
        Assert.Equal("", Text(view, "routing"));
        Assert.Equal("", Text(view, "exception"));
    }

    [Fact]
    public void Every_line_points_at_the_spans_it_is_based_on()
    {
        var view = View();

        Assert.Equal([TestOtlp.SpanId(5)], view.Explanation.Single(l => l.Kind == "executed").SpanIds);
        Assert.Equal([TestOtlp.SpanId(6), TestOtlp.SpanId(7), TestOtlp.SpanId(8)], view.Explanation.Single(l => l.Kind == "database").SpanIds.Order());
    }

    [Fact]
    public void A_heuristic_link_is_worded_as_likely_not_proven()
    {
        Assert.Equal("The interaction \"Create order\" likely triggered this request (linked by timing, not proven).", Text(View("heuristic"), "entry"));
    }

    [Fact]
    public void Failures_name_the_recorded_exception()
    {
        var view = View(fail: true);

        Assert.Equal("POST /api/orders returned 500 in 56.0 ms.", Text(view, "request"));
        Assert.Equal("handler OrderEndpoints.Create recorded System.InvalidOperationException: Out of stock.", Text(view, "exception"));
    }

    [Fact]
    public void Routing_without_execution_says_it_is_not_recorded()
    {
        var store = new TraceStore(new StoreLimits());
        store.Add(TestOtlp.Request("api", "CodeAtlas.Components", [
            TestOtlp.Span(T, TestOtlp.SpanId(4), null, "endpoint POST /api/orders", SpanKind.Internal, 0, 5,
                ("codeatlas.component", "endpoint"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.token", 1), ("codeatlas.code.evidence", "routing")),
        ]));

        var view = TraceAnalyzer.Analyze(T, store.Get(T)!, false, Later);

        Assert.Equal("Routed to Shop.OrderEndpoints.Create; whether it ran is not recorded.", Text(view, "routing"));
        Assert.Contains("No browser interaction was captured for this trace.", Text(view, "entry"));
    }

    [Fact]
    public void Gaps_and_app_sampling_become_visibility_lines()
    {
        var view = View(sampling: "app");

        // handler 4–56 ms; its children cover 5–15, 16–40, 41–45 and 46–55 → 5.0 ms uncovered.
        Assert.Contains("handler OrderEndpoints.Create: 5.0 ms of this span is not covered", Text(view, "visibility"));
        Assert.Equal("Only requests that your app's own sampler records are visible.", Text(view, "sampling"));
    }
}
