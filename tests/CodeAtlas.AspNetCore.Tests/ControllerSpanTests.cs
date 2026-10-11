using System.Reflection;
using CodeAtlas.AspNetCore.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.AspNetCore.Tests;

[ApiController]
[Route("api/orders")]
public class TestOrdersController : ControllerBase
{
    [HttpGet("{id:int}")]
    public IActionResult Get(int id) => Ok(id);

    [HttpGet("by-ref/{reference}")]
    public IActionResult Get(string reference) => Ok(reference);

    [HttpPost("fail")]
    public IActionResult Fail() => throw new InvalidOperationException("action failed");
}

public class ControllerSpanTests
{
    private static Task<TestApp> StartAsync() => TestApp.StartAsync(app => app.MapControllers(), b => b.Services.AddControllers());

    private static MethodInfo Overload(Type parameter) => typeof(TestOrdersController).GetMethod("Get", [parameter])!;

    [Theory]
    [InlineData("/api/orders/5", typeof(int))]
    [InlineData("/api/orders/by-ref/abc", typeof(string))]
    public async Task Action_span_identifies_the_exact_overload(string url, Type parameter)
    {
        await using var app = await StartAsync();

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, url);

        var spans = await app.Sink.WaitForTraceAsync(traceId);
        var endpoint = spans.Single(s => (string?)s.Attr("codeatlas.component") == "endpoint");
        var action = spans.Single(s => (string?)s.Attr("codeatlas.component") == "action");
        Assert.Equal(endpoint.SpanId, action.ParentSpanId);
        Assert.Equal("action TestOrdersController.Get", action.Name);
        Assert.Equal("executed", action.Attr("codeatlas.code.evidence"));
        Assert.Equal((long)Overload(parameter).MetadataToken, action.Attr("codeatlas.code.token"));
        Assert.Equal("routing", endpoint.Attr("codeatlas.code.evidence"));
        Assert.Equal((long)Overload(parameter).MetadataToken, endpoint.Attr("codeatlas.code.token"));
    }

    [Fact]
    public async Task Action_exceptions_mark_the_action_span()
    {
        await using var app = await StartAsync();

        var (_, traceId) = await app.SendAsync(HttpMethod.Post, "/api/orders/fail");

        var action = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => (string?)s.Attr("codeatlas.component") == "action");
        Assert.True(action.IsError);
        Assert.Equal("action failed", action.Span.Status.Message);
    }
}
