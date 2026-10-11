using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using CodeAtlas.Collector.Tests.Support;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodeAtlas.Collector.Tests;

public class LiveTests
{
    [Fact]
    public async Task Viewers_are_told_which_traces_changed()
    {
        await using var host = await CollectorHost.StartAsync();
        var received = new TaskCompletionSource<CodeAtlas.Collector.Analysis.TraceSummary[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder().WithUrl($"{host.BaseUrl}/live").Build();
        connection.On<CodeAtlas.Collector.Analysis.TraceSummary[]>("traces-updated", s => received.TrySetResult(s));
        await connection.StartAsync();

        (await TestOtlp.PostAsync(host.Client, TestOtlp.Request("api", "s", [TestOtlp.Span(TestOtlp.TraceId(7), TestOtlp.SpanId(1), null, "x")]))).Dispose();

        Assert.Equal([TestOtlp.TraceId(7)], (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Select(s => s.TraceId));
    }

    [Fact]
    public async Task Store_limits_come_from_configuration()
    {
        await using var host = await CollectorHost.StartAsync("--CodeAtlas:MaxTraces=2");

        for (var i = 1; i <= 3; i++)
            (await TestOtlp.PostAsync(host.Client, TestOtlp.Request("api", "s", [TestOtlp.Span(TestOtlp.TraceId(i), TestOtlp.SpanId(1), null, "x")]))).Dispose();

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await host.Client.GetAsync($"api/traces/{TestOtlp.TraceId(1)}/spans")).StatusCode);
    }

    [Fact]
    public async Task A_page_from_another_origin_cannot_subscribe()
    {
        await using var host = await CollectorHost.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://evil.example");

        await Assert.ThrowsAnyAsync<WebSocketException>(() => socket.ConnectAsync(new Uri($"{host.BaseUrl.Replace("http", "ws")}/live"), CancellationToken.None));
    }

    [Fact]
    public async Task A_viewer_that_never_reads_does_not_slow_down_ingest()
    {
        await using var host = await CollectorHost.StartAsync();
        using var stalled = new ClientWebSocket();
        await stalled.ConnectAsync(new Uri($"{host.BaseUrl.Replace("http", "ws")}/live"), CancellationToken.None);
        await stalled.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
        var handshake = new byte[64];
        var reply = await stalled.ReceiveAsync(handshake, CancellationToken.None);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(handshake, 0, reply.Count)); // subscribed; from now on it never reads
        var route = new string('r', 4000);

        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 3000; i++)
        {
            var clock = Stopwatch.StartNew();
            (await TestOtlp.PostAsync(host.Client, TestOtlp.Request("api", "s",
                [TestOtlp.Span(TestOtlp.TraceId(i + 1), TestOtlp.SpanId(1), null, "x", OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind.Server, 0, 1, ("http.route", route))]))).Dispose();
            if (clock.Elapsed > slowest) slowest = clock.Elapsed;
        }

        Assert.True(slowest < TimeSpan.FromSeconds(2), $"slowest ingest took {slowest.TotalMilliseconds:0} ms");
    }
}
