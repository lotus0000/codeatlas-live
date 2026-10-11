using System.Reflection;
using CodeAtlas.AspNetCore.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests;

public record OrderRequest(int Quantity);

public static class OrderEndpoints
{
    public static IResult Create(OrderRequest request) =>
        request.Quantity > 99 ? throw new InvalidOperationException("Too many") : Results.Created("/orders/1", request);
}

public class EndpointSpanTests
{
    private static async ValueTask<object?> Validate(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.GetArgument<OrderRequest>(0).Quantity <= 0 ? Results.BadRequest() : await next(context);

    private static readonly MethodInfo Create = typeof(OrderEndpoints).GetMethod(nameof(OrderEndpoints.Create))!;

    private static Task<TestApp> StartAsync() => TestApp.StartAsync(
        app =>
        {
            app.MapPost("/orders", OrderEndpoints.Create).AddEndpointFilter(Validate).WithCodeAtlasHandlerSpan();
            var group = app.MapGroup("/g").WithCodeAtlasHandlerSpan();
            group.MapPost("/orders", OrderEndpoints.Create).AddEndpointFilter(Validate);
            app.MapGet("/lambda", () => "hi");
            app.MapHealthChecks("/health");
        },
        b => b.Services.AddHealthChecks());

    private static RecordedSpan One(IEnumerable<RecordedSpan> spans, string component) =>
        spans.Single(s => (string?)s.Attr("codeatlas.component") == component);

    [Fact]
    public async Task Endpoint_span_carries_routing_identity_and_handler_span_carries_executed_identity()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Post, "/orders", new OrderRequest(1));

        Assert.Equal(201, (int)response.StatusCode);
        var spans = await app.Sink.WaitForTraceAsync(traceId);
        var server = spans.Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        var endpoint = One(spans, "endpoint");
        var handler = One(spans, "handler");
        Assert.Equal(server.SpanId, endpoint.ParentSpanId);
        Assert.Equal(endpoint.SpanId, handler.ParentSpanId);
        Assert.Equal("routing", endpoint.Attr("codeatlas.code.evidence"));
        Assert.Equal("executed", handler.Attr("codeatlas.code.evidence"));
        Assert.Equal("handler OrderEndpoints.Create", handler.Name);
        foreach (var span in new[] { endpoint, handler })
        {
            Assert.Equal((long)Create.MetadataToken, span.Attr("codeatlas.code.token"));
            Assert.Equal(Create.Module.ModuleVersionId.ToString(), span.Attr("codeatlas.code.mvid"));
            Assert.Equal(Create.Module.Assembly.Location, span.Attr("codeatlas.code.assembly_path"));
            Assert.Equal("CodeAtlas.AspNetCore.Tests.OrderEndpoints.Create", span.Attr("codeatlas.code.function"));
        }
    }

    [Fact]
    public async Task Validation_failure_has_an_endpoint_span_and_no_handler_span()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Post, "/orders", new OrderRequest(0));

        Assert.Equal(400, (int)response.StatusCode);
        var spans = await app.Sink.WaitForTraceAsync(traceId);
        Assert.Single(spans, s => (string?)s.Attr("codeatlas.component") == "endpoint");
        Assert.DoesNotContain(spans, s => (string?)s.Attr("codeatlas.component") == "handler");
    }

    [Fact]
    public async Task Handler_exceptions_are_recorded_on_handler_and_endpoint_spans()
    {
        await using var app = await StartAsync();

        var (_, traceId) = await app.SendAsync(HttpMethod.Post, "/orders", new OrderRequest(100));

        var spans = await app.Sink.WaitForTraceAsync(traceId);
        foreach (var component in new[] { "handler", "endpoint" })
        {
            var span = One(spans, component);
            Assert.True(span.IsError);
            Assert.Equal("Too many", span.Span.Events.Single(e => e.Name == "exception").Attributes.Single(a => a.Key == "exception.message").Value.StringValue);
        }
    }

    [Fact]
    public async Task Group_level_handler_span_also_covers_the_endpoints_own_filters()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Post, "/g/orders", new OrderRequest(0));

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Single(await app.Sink.WaitForTraceAsync(traceId), s => (string?)s.Attr("codeatlas.component") == "handler");
    }

    [Fact]
    public async Task Lambda_endpoint_identity_is_the_compiled_lambda_method()
    {
        await using var app = await StartAsync();
        var lambda = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/lambda").Metadata.GetMetadata<MethodInfo>()!;

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/lambda");

        var endpoint = One(await app.Sink.WaitForTraceAsync(traceId), "endpoint");
        Assert.Equal((long)lambda.MetadataToken, endpoint.Attr("codeatlas.code.token"));
        Assert.Contains("<", (string)endpoint.Attr("codeatlas.code.function")!);
    }

    [Fact]
    public async Task Endpoints_without_a_method_still_get_a_span()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Get, "/health");

        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        var endpoint = One(await app.Sink.WaitForTraceAsync(traceId), "endpoint");
        Assert.Null(endpoint.Attr("codeatlas.code.token"));
    }
}
