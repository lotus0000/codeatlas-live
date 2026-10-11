using System.Text;
using CodeAtlas.AspNetCore.Tests.Support;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests;

public class HttpClientSpanTests
{
    [Fact]
    public async Task Outgoing_call_is_a_child_of_the_handler_without_query_string_and_propagates_the_trace()
    {
        using var stub = new StubServer();
        await using var app = await TestApp.StartAsync(
            a => a.MapGet("/call", async (IHttpClientFactory factory) =>
            {
                using var response = await factory.CreateClient().GetAsync($"{stub.Url}/data?token=SECRET-TOKEN");
                return Results.Ok((int)response.StatusCode);
            }).WithCodeAtlasHandlerSpan(),
            b => b.Services.AddHttpClient());

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/call");

        var spans = await app.Sink.WaitForTraceAsync(traceId);
        var handler = spans.Single(s => (string?)s.Attr("codeatlas.component") == "handler");
        var call = spans.Single(s => s.Scope == "System.Net.Http");
        Assert.Equal(Span.Types.SpanKind.Client, call.Span.Kind);
        Assert.Equal(handler.SpanId, call.ParentSpanId);
        Assert.Equal($"{stub.Url}/data", call.Attr("url.full"));
        Assert.Equal(200L, call.Attr("http.response.status_code"));
        Assert.Contains(traceId, stub.TraceParents.Single());
        Assert.DoesNotContain("SECRET-TOKEN", string.Join("\n", app.Sink.Requests.Select(r => Encoding.UTF8.GetString(r.ToByteArray()))));
    }
}
