using ControllerSample.Services;
using Microsoft.AspNetCore.Mvc;

namespace ControllerSample.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(OrderService orders) : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<Order> List() => orders.All();

    [HttpPost]
    public IActionResult Create(CreateOrder request)
    {
        var order = orders.Create(request.Sku, request.Quantity);
        return CreatedAtAction(nameof(List), null, order);
    }
}

public sealed record CreateOrder(string Sku, int Quantity);
