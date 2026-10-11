#if CODEATLAS
using CodeAtlas.AspNetCore;
#endif

namespace MinimalApiSample.Orders;

public static class OrderEndpoints
{
    public static void MapOrders(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        Traced(api.MapGet("/products", ListProducts));
        Traced(api.MapPost("/orders", CreateOrder));
    }

    // In Debug, CodeAtlas records a span around the handler itself (docs/integration.md, "handler spans").
    private static RouteHandlerBuilder Traced(RouteHandlerBuilder endpoint) =>
#if CODEATLAS
        endpoint.WithCodeAtlasHandlerSpan();
#else
        endpoint;
#endif

    public static async Task<IResult> ListProducts(OrderService orders, CancellationToken cancellationToken) =>
        Results.Ok(await orders.ListProductsAsync(cancellationToken));

    public static async Task<IResult> CreateOrder(CreateOrderRequest request, OrderService orders, CancellationToken cancellationToken)
    {
        if (request.Quantity <= 0) return Results.BadRequest(new { error = "Quantity must be positive." });
        var id = await orders.CreateAsync(request.Sku, request.Quantity, cancellationToken);
        return Results.Created($"/api/orders/{id}", new { id });
    }
}

public sealed record CreateOrderRequest(string Sku, int Quantity);
