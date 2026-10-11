#if CODEATLAS
using CodeAtlas.AspNetCore;
#endif
using MinimalApiSample.Data;
using MinimalApiSample.Orders;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Shop") ?? throw new InvalidOperationException("ConnectionStrings:Shop is not set.")));
builder.Services.AddScoped<OrderService>();
#if CODEATLAS
builder.AddCodeAtlas();
#endif

var app = builder.Build();
await Database.EnsureCreatedAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
app.MapOrders();
app.Run();
