using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MinimalApiSample.Orders;
using Npgsql;

namespace Samples.Tests;

public sealed class MinimalApiSampleTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private WebApplicationFactory<OrderService> Start() => new WebApplicationFactory<OrderService>().WithWebHostBuilder(b => b
        .UseEnvironment("Development")
        .UseSetting("ConnectionStrings:Shop", postgres.ConnectionString)
        .UseSetting("CodeAtlas:Enabled", "false"));

    private async Task<long> OrderCount()
    {
        await using var db = NpgsqlDataSource.Create(postgres.ConnectionString);
        return (long)(await db.CreateCommand("SELECT count(*) FROM orders").ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Lists_the_seeded_products()
    {
        await using var app = Start();

        var products = await app.CreateClient().GetFromJsonAsync<Product[]>("/api/products");

        Assert.Contains(products!, p => p.Sku == "KB-01" && p.Stock > 0);
        Assert.Contains(products!, p => p is { Sku: "MON-27", Stock: 0 });
    }

    [Fact]
    public async Task Creating_an_order_returns_201_and_stores_it()
    {
        await using var app = Start();
        var before = await OrderCount();

        var response = await app.CreateClient().PostAsJsonAsync("/api/orders", new { sku = "KB-01", quantity = 2 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<Created>())!.Id > 0);
        Assert.Equal(before + 1, await OrderCount());
    }

    [Fact]
    public async Task Out_of_stock_fails_with_500_and_stores_nothing()
    {
        await using var app = Start();
        var before = await OrderCount();

        var response = await app.CreateClient().PostAsJsonAsync("/api/orders", new { sku = "MON-27", quantity = 1 });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Out of stock: MON-27", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await OrderCount());
    }

    [Fact]
    public async Task A_non_positive_quantity_is_a_400()
    {
        await using var app = Start();

        var response = await app.CreateClient().PostAsJsonAsync("/api/orders", new { sku = "KB-01", quantity = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record Created(int Id);
}
