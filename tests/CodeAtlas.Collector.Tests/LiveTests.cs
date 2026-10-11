using CodeAtlas.Collector.Tests.Support;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodeAtlas.Collector.Tests;

public class LiveTests
{
    [Fact]
    public async Task Viewers_are_told_which_traces_changed()
    {
        await using var host = await CollectorHost.StartAsync();
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder().WithUrl($"{host.BaseUrl}/live").Build();
        connection.On<string[]>("traces-updated", ids => received.TrySetResult(ids));
        await connection.StartAsync();

        (await TestOtlp.PostAsync(host.Client, TestOtlp.Request("api", "s", [TestOtlp.Span(TestOtlp.TraceId(7), TestOtlp.SpanId(1), null, "x")]))).Dispose();

        Assert.Equal([TestOtlp.TraceId(7)], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Store_limits_come_from_configuration()
    {
        await using var host = await CollectorHost.StartAsync("--CodeAtlas:MaxTraces=2");

        for (var i = 1; i <= 3; i++)
            (await TestOtlp.PostAsync(host.Client, TestOtlp.Request("api", "s", [TestOtlp.Span(TestOtlp.TraceId(i), TestOtlp.SpanId(1), null, "x")]))).Dispose();

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await host.Client.GetAsync($"api/traces/{TestOtlp.TraceId(1)}/spans")).StatusCode);
    }
}
