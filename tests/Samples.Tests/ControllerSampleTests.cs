using System.Net;
using System.Net.Http.Json;
using ControllerSample.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Samples.Tests;

public sealed class ControllerSampleTests
{
    private static WebApplicationFactory<OrderService> Start() => new WebApplicationFactory<OrderService>().WithWebHostBuilder(b => b
        .UseEnvironment("Development")
        .UseSetting("CodeAtlas:Enabled", "false"));

    [Fact]
    public async Task Creates_and_lists_orders()
    {
        await using var app = Start();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new { sku = "KB-01", quantity = 2 });
        var orders = await client.GetFromJsonAsync<Order[]>("/api/orders");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(orders!, o => o is { Sku: "KB-01", Quantity: 2 });
    }

    [Fact]
    public async Task A_non_positive_quantity_throws_in_the_service_and_returns_500()
    {
        await using var app = Start();

        var response = await app.CreateClient().PostAsJsonAsync("/api/orders", new { sku = "KB-01", quantity = 0 });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("ArgumentOutOfRangeException", await response.Content.ReadAsStringAsync());
    }
}
