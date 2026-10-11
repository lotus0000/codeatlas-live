using System.Net.Http.Json;
using CodeAtlas.AspNetCore;
using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeAtlas.Collector.Tests;

/// <summary>The real Plan 1 exporter talking to the real collector over HTTP.</summary>
public class RealExporterTests
{
    [Fact]
    public async Task Spans_from_CodeAtlas_AspNetCore_arrive_intact()
    {
        await using var collector = await CollectorHost.StartAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development, ApplicationName = "TargetApp" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddCodeAtlas(o => o.Endpoint = $"{collector.BaseUrl}/v1/traces");
        await using var target = builder.Build();
        target.MapGet("/orders/{id:int}", (int id) => id);
        await target.StartAsync();
        var traceId = TestOtlp.TraceId(0xabc);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/7");
        request.Headers.Add("traceparent", $"00-{traceId}-{TestOtlp.SpanId(9)}-01");
        (await target.GetTestClient().SendAsync(request)).Dispose();

        List<SpanRecord>? spans = null;
        for (var i = 0; i < 50 && spans is not { Count: > 0 }; i++)
        {
            await Task.Delay(100);
            using var response = await collector.Client.GetAsync($"api/traces/{traceId}/spans");
            if (response.IsSuccessStatusCode) spans = await response.Content.ReadFromJsonAsync<List<SpanRecord>>();
        }
        var server = Assert.Single(spans!, s => s.Kind == "server");
        Assert.Equal("GET /orders/{id:int}", server.Name);
        Assert.Equal("TargetApp", server.Service);
        Assert.Equal("codeatlas", server.Resource["codeatlas.sampling"]?.ToString());
        Assert.Contains(spans!, s => s.Attributes.ContainsKey("codeatlas.code.token"));
        await target.StopAsync();
    }
}
