using CodeAtlas.Collector.Analysis;
using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace CodeAtlas.Collector.Tests;

public class AnalyzerTests
{
    private static readonly string T = TestOtlp.TraceId(1);
    private static readonly DateTimeOffset Later = DateTimeOffset.FromUnixTimeMilliseconds((long)(TestOtlp.Epoch / 1_000_000) + 60_000);

    private static IReadOnlyList<SpanRecord> Spans(params (string Service, string Scope, OpenTelemetry.Proto.Trace.V1.Span Span)[] spans)
    {
        var store = new TraceStore(new StoreLimits());
        foreach (var (service, scope, span) in spans) store.Add(TestOtlp.Request(service, scope, [span]));
        return store.Get(T)!;
    }

    private static IEnumerable<NodeView> All(IEnumerable<NodeView> nodes) => nodes.SelectMany(n => All(n.Children).Prepend(n));

    // A browser interaction → fetch → server → endpoint (routing) → handler (executed) → database.
    private static IReadOnlyList<SpanRecord> CreateOrder(bool withHandler = true) => Spans(
        ("web", "codeatlas-web", TestOtlp.Span(T, TestOtlp.SpanId(1), null, "interaction: Create order", SpanKind.Internal, 0, 0,
            ("codeatlas.interaction.name", "Create order"), ("codeatlas.correlation", "explicit"))),
        ("web", "codeatlas-web", TestOtlp.Span(T, TestOtlp.SpanId(2), TestOtlp.SpanId(1), "POST /api/orders", SpanKind.Client, 1, 50,
            ("http.request.method", "POST"), ("codeatlas.correlation", "explicit"), ("http.response.status_code", 201))),
        ("api", "Microsoft.AspNetCore", TestOtlp.Span(T, TestOtlp.SpanId(3), TestOtlp.SpanId(2), "POST /api/orders", SpanKind.Server, 2, 48,
            ("http.request.method", "POST"), ("http.route", "/api/orders"), ("http.response.status_code", 201))),
        ("api", "CodeAtlas.Components", TestOtlp.Span(T, TestOtlp.SpanId(4), TestOtlp.SpanId(3), "endpoint POST /api/orders", SpanKind.Internal, 3, 47,
            ("codeatlas.component", "endpoint"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.mvid", "m"),
            ("codeatlas.code.token", 100663300), ("codeatlas.code.assembly_path", "/app/Shop.dll"), ("codeatlas.code.evidence", "routing"))),
        withHandler
            ? ("api", "CodeAtlas.Components", TestOtlp.Span(T, TestOtlp.SpanId(5), TestOtlp.SpanId(4), "handler OrderEndpoints.Create", SpanKind.Internal, 4, 46,
                ("codeatlas.component", "handler"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.mvid", "m"),
                ("codeatlas.code.token", 100663300), ("codeatlas.code.assembly_path", "/app/Shop.dll"), ("codeatlas.code.evidence", "executed")))
            : ("api", "Microsoft.AspNetCore", TestOtlp.Span(T, TestOtlp.SpanId(9), TestOtlp.SpanId(4), "unused", SpanKind.Internal, 4, 4)),
        ("api", "Npgsql", TestOtlp.Span(T, TestOtlp.SpanId(6), withHandler ? TestOtlp.SpanId(5) : TestOtlp.SpanId(4), "INSERT orders", SpanKind.Client, 10, 30,
            ("db.system.name", "postgresql"), ("db.query.summary", "INSERT orders"))));

    [Fact]
    public void Builds_the_tree_from_span_ids_with_kinds()
    {
        var view = TraceAnalyzer.Analyze(T, CreateOrder(), truncated: false, Later);

        var root = Assert.Single(view.Roots);
        Assert.Equal("interaction", root.Kind);
        Assert.Equal(["interaction", "browser-request", "server", "endpoint", "handler", "database"],
            All(view.Roots).Select(n => n.Kind));
        Assert.All(All(view.Roots), n => Assert.Equal("executed", n.Evidence));
    }

    [Fact]
    public void Method_identity_keeps_its_evidence_kind_and_correlation_is_exposed()
    {
        var nodes = All(TraceAnalyzer.Analyze(T, CreateOrder(), false, Later).Roots).ToList();

        Assert.Equal("routing", nodes.Single(n => n.Kind == "endpoint").Code!.Evidence);
        Assert.Equal("executed", nodes.Single(n => n.Kind == "handler").Code!.Evidence);
        Assert.Equal(100663300, nodes.Single(n => n.Kind == "handler").Code!.Token);
        Assert.Equal("explicit", nodes.Single(n => n.Kind == "browser-request").Correlation);
        Assert.Null(nodes.Single(n => n.Kind == "database").Code);
    }

    [Fact]
    public void Summary_names_the_interaction_request_and_sampling()
    {
        var summary = TraceAnalyzer.Summarize(T, CreateOrder());

        Assert.Equal("Create order", summary.Interaction);
        Assert.Equal("explicit", summary.Correlation);
        Assert.Equal(("POST", "/api/orders", 201L), (summary.Method, summary.Route, summary.StatusCode));
        Assert.Equal(50, summary.DurationMs);
        Assert.False(summary.HasError);
        Assert.Equal(6, summary.SpanCount);
        Assert.Equal("unknown", summary.Sampling); // these test spans carry no codeatlas.sampling resource attribute
    }

    [Fact]
    public void A_missing_parent_is_a_root_with_a_gap()
    {
        var view = TraceAnalyzer.Analyze(T, Spans(("api", "s", TestOtlp.Span(T, TestOtlp.SpanId(2), TestOtlp.SpanId(1), "orphan"))), false, Later);

        var gap = Assert.Single(Assert.Single(view.Roots).Gaps);
        Assert.Equal("missing-parent", gap.Kind);
        Assert.Equal("Parent span not received yet: it may still be in an export batch, sampled out, or its export failed.", gap.Message);
    }

    [Fact]
    public void Uncovered_time_is_reported_with_honest_wording()
    {
        var view = TraceAnalyzer.Analyze(T, CreateOrder(), false, Later);

        var handler = All(view.Roots).Single(n => n.Kind == "handler");
        var gap = Assert.Single(handler.Gaps);
        Assert.Equal("uncovered-time", gap.Kind);
        Assert.Equal("22.0 ms of this span is not covered by any child span. That time is unexplained by telemetry; it does not mean nothing ran.", gap.Message);
    }

    [Fact]
    public void An_http_client_span_without_a_server_child_gets_a_gap_only_after_five_seconds()
    {
        var spans = Spans(("web", "codeatlas-web", TestOtlp.Span(T, TestOtlp.SpanId(1), null, "GET /x", SpanKind.Client, 0, 10, ("http.request.method", "GET"))));
        var justEnded = DateTimeOffset.FromUnixTimeMilliseconds((long)(TestOtlp.Epoch / 1_000_000) + 1_000);

        Assert.Empty(TraceAnalyzer.Analyze(T, spans, false, justEnded).Roots[0].Gaps);
        var gap = Assert.Single(TraceAnalyzer.Analyze(T, spans, false, Later).Roots[0].Gaps);
        Assert.Equal("No server span received for this request. The backend may not be instrumented, the span may be sampled out, or its export was delayed or failed.", gap.Message);
    }

    [Fact]
    public void Database_client_spans_never_get_the_no_server_span_gap()
    {
        var spans = Spans(("api", "Npgsql", TestOtlp.Span(T, TestOtlp.SpanId(1), null, "SELECT t", SpanKind.Client, 0, 1, ("db.system.name", "postgresql"))));

        Assert.Empty(TraceAnalyzer.Analyze(T, spans, false, Later).Roots[0].Gaps);
    }

    [Fact]
    public void An_endpoint_without_a_handler_or_action_says_the_handler_was_not_recorded()
    {
        var view = TraceAnalyzer.Analyze(T, CreateOrder(withHandler: false), false, Later);

        var endpoint = All(view.Roots).Single(n => n.Kind == "endpoint");
        Assert.Contains(endpoint.Gaps, g => g.Kind == "handler-not-recorded" && g.Message.StartsWith("The handler's execution was not recorded"));
    }

    [Fact]
    public void Parent_cycles_terminate_and_are_reported()
    {
        var spans = Spans(
            ("api", "s", TestOtlp.Span(T, TestOtlp.SpanId(1), TestOtlp.SpanId(1), "self")),
            ("api", "s", TestOtlp.Span(T, TestOtlp.SpanId(2), TestOtlp.SpanId(3), "a")),
            ("api", "s", TestOtlp.Span(T, TestOtlp.SpanId(3), TestOtlp.SpanId(2), "b")),
            ("api", "s", TestOtlp.Span(T, TestOtlp.SpanId(4), TestOtlp.SpanId(2), "below a")));

        var view = TraceAnalyzer.Analyze(T, spans, false, Later);

        Assert.Equal(["self", "a", "b"], view.Roots.Select(r => r.Span.Name).Order().ToList().OrderBy(n => n == "self" ? 0 : n == "a" ? 1 : 2));
        Assert.All(view.Roots, r => Assert.Contains(r.Gaps, g => g.Kind == "parent-cycle"
            && g.Message == "This span's parent links form a cycle, so where it belongs in the tree is unknown."));
        var below = Assert.Single(view.Roots.Single(r => r.Span.Name == "a").Children);
        Assert.Equal("below a", below.Span.Name);
        Assert.Empty(below.Gaps);
    }

    [Fact]
    public void App_sampling_is_reported_from_the_resource()
    {
        var spans = Spans(("api", "Microsoft.AspNetCore", TestOtlp.Span(T, TestOtlp.SpanId(1), null, "GET /", SpanKind.Server)));
        var withSampling = new TraceStore(new StoreLimits());
        withSampling.Add(TestOtlp.Request("api", "Microsoft.AspNetCore", [TestOtlp.Span(T, TestOtlp.SpanId(1), null, "GET /", SpanKind.Server)], ("codeatlas.sampling", "app")));

        Assert.Equal("app", TraceAnalyzer.Summarize(T, withSampling.Get(T)!).Sampling);
        Assert.Equal("unknown", TraceAnalyzer.Summarize(T, spans).Sampling);
    }
}
