using System.Diagnostics;
using CodeAtlas.AspNetCore.Export;
using CodeAtlas.AspNetCore.Http;
using CodeAtlas.AspNetCore.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Resource.V1;

namespace CodeAtlas.AspNetCore.Tests;

public class ExportProcessorTests
{
    private static readonly Resource Resource = new() { Attributes = { SpanSerializer.Kv("service.name", "test") } };

    private static CodeAtlasExportProcessor Processor(ISpanSink sink, int capacity = 2048, int batchDelayMs = 10) =>
        new(new SpanSerializer(false, new HttpFactsStore()), sink, Resource, capacity, TimeSpan.FromMilliseconds(batchDelayMs), NullLogger.Instance);

    private static Activity Span(string name) => TestActivities.Finished("Test.Source", name);

    [Fact]
    public async Task Delivers_spans_grouped_by_scope_with_the_resource()
    {
        var sink = new RecordingSink();
        using var processor = Processor(sink);

        processor.OnEnd(Span("a"));
        processor.OnEnd(TestActivities.Finished("Other.Source", "b"));

        var spans = await sink.WaitForAsync(s => s.Count == 2);
        Assert.Equal(["Other.Source", "Test.Source"], spans.Select(s => s.Scope).Order());
        Assert.All(spans, s => Assert.Equal("test", s.Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue));
    }

    [Fact]
    public async Task Drops_the_oldest_spans_when_full_and_never_blocks_the_caller()
    {
        var sink = new BlockingSink();
        using var processor = Processor(sink, capacity: 4);
        processor.OnEnd(Span("first"));
        await sink.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) processor.OnEnd(Span($"n{i}"));
        clock.Stop();
        sink.Release();
        processor.Shutdown(5000);

        Assert.True(clock.ElapsedMilliseconds < 200, $"OnEnd took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(6, processor.Dropped);
        Assert.Equal(["first", "n6", "n7", "n8", "n9"], sink.Recorded.Spans.Select(s => s.Name));
    }

    [Fact]
    public async Task Sink_failures_are_counted_and_never_thrown()
    {
        var sink = new ThrowingSink();
        using var processor = Processor(sink);

        processor.OnEnd(Span("a"));
        processor.OnEnd(Span("b"));

        var clock = Stopwatch.StartNew();
        while (processor.SendFailures == 0 && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.True(processor.SendFailures >= 1);
    }

    [Fact]
    public void Shutdown_flushes_queued_spans()
    {
        var sink = new RecordingSink();
        var processor = Processor(sink, batchDelayMs: 60_000);

        processor.OnEnd(Span("a"));
        processor.OnEnd(Span("b"));
        Assert.True(processor.Shutdown(5000));

        Assert.Equal(["a", "b"], sink.Spans.Select(s => s.Name));
        processor.Dispose();
    }

    [Fact]
    public async Task Http_sink_posts_protobuf_to_the_collector()
    {
        using var collector = new FakeCollector();
        using var sink = new HttpSpanSink(new Uri(collector.TracesUrl), TimeSpan.FromSeconds(2));
        using var processor = Processor(sink);

        processor.OnEnd(Span("over-the-wire"));

        var clock = Stopwatch.StartNew();
        while (collector.Received.IsEmpty && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
        var (contentType, request) = Assert.Single(collector.Received);
        Assert.Equal("application/x-protobuf", contentType);
        Assert.Equal("over-the-wire", request.ResourceSpans[0].ScopeSpans[0].Spans[0].Name);
    }
}
