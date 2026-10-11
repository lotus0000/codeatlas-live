using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace CodeAtlas.Collector.Tests;

public class StoreTests
{
    private static TraceStore Store(int maxTraces = 500, int maxSpans = 2000, int maxLength = 4096) =>
        new(new StoreLimits(maxTraces, maxSpans, maxLength));

    [Fact]
    public void Children_before_parents_are_kept_and_joined_by_id()
    {
        var store = Store();
        var t = TestOtlp.TraceId(1);

        store.Add(TestOtlp.Request("api", "s", [TestOtlp.Span(t, TestOtlp.SpanId(2), TestOtlp.SpanId(1), "child")]));
        store.Add(TestOtlp.Request("api", "s", [TestOtlp.Span(t, TestOtlp.SpanId(1), null, "parent", SpanKind.Server)]));

        var spans = store.Get(t)!;
        Assert.Equal(2, spans.Count);
        Assert.Equal(TestOtlp.SpanId(1), spans.Single(s => s.Name == "child").ParentSpanId);
    }

    [Fact]
    public void The_oldest_trace_is_evicted_beyond_the_limit()
    {
        var store = Store(maxTraces: 3);

        for (var i = 1; i <= 4; i++) store.Add(TestOtlp.Request("api", "s", [TestOtlp.Span(TestOtlp.TraceId(i), TestOtlp.SpanId(1), null, $"t{i}")]));

        Assert.Null(store.Get(TestOtlp.TraceId(1)));
        Assert.Equal([TestOtlp.TraceId(4), TestOtlp.TraceId(3), TestOtlp.TraceId(2)], store.RecentTraceIds(10));
    }

    [Fact]
    public void Spans_beyond_the_per_trace_limit_are_dropped_and_marked()
    {
        var store = Store(maxSpans: 2);
        var t = TestOtlp.TraceId(1);

        store.Add(TestOtlp.Request("api", "s", Enumerable.Range(1, 3).Select(i => TestOtlp.Span(t, TestOtlp.SpanId(i), null, $"s{i}"))));

        Assert.Equal(2, store.Get(t)!.Count);
        Assert.True(store.IsTruncated(t));
    }

    [Fact]
    public void Long_attribute_values_are_cut()
    {
        var store = Store(maxLength: 10);
        var t = TestOtlp.TraceId(1);

        store.Add(TestOtlp.Request("api", "s", [TestOtlp.Span(t, TestOtlp.SpanId(1), null, "x", attributes: ("big", new string('a', 50)))]));

        Assert.Equal("aaaaaaaaaa…", store.Get(t)![0].Attributes["big"]);
    }

    [Fact]
    public void A_more_detailed_copy_of_a_span_wins_over_a_poorer_one()
    {
        var store = Store();
        var t = TestOtlp.TraceId(1);
        var rich = TestOtlp.Span(t, TestOtlp.SpanId(1), null, "GET /orders", SpanKind.Server, 0, 1, ("http.route", "/orders"), ("http.response.status_code", 200));
        var poor = TestOtlp.Span(t, TestOtlp.SpanId(1), null, "Microsoft.AspNetCore.Hosting.HttpRequestIn", SpanKind.Server);

        store.Add(TestOtlp.Request("api", "s", [rich]));
        store.Add(TestOtlp.Request("api", "s", [poor]));

        Assert.Equal("GET /orders", store.Get(t)!.Single().Name);
    }

    [Fact]
    public async Task Concurrent_ingest_and_reads_are_safe()
    {
        var store = Store(maxTraces: 50);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var t = TestOtlp.TraceId(worker * 1000 + i % 60);
                store.Add(TestOtlp.Request("api", "s", [TestOtlp.Span(t, TestOtlp.SpanId(i + 1), null, "x")]));
                foreach (var id in store.RecentTraceIds(20)) _ = store.Get(id)?.Count;
            }
        })));

        Assert.Equal(50, store.RecentTraceIds(1000).Count);
    }
}
