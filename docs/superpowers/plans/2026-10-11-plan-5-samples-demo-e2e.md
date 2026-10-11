# Plan 5 — Samples, Demo, E2E, Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two real sample apps, a one-command "Create Order" demo, a Playwright end-to-end suite with real processes, release verification for the samples, one command for every check, and the documentation a developer needs to use CodeAtlas Live on their own app.

**Architecture:**
- **Samples**
  - `samples/MinimalApiSample` is a Minimal API plus Npgsql "shop": products, plus orders that can fail with out-of-stock.
  - `samples/MinimalApiSample.Web` is its React app. It uses `@codeatlas/web` for:
    - an explicit fetch interaction,
    - an explicit Axios interaction,
    - a heuristic click.
  - `samples/ControllerSample` is a controller-based app with an in-memory service and its own OpenTelemetry metrics.
- **Wiring:** inside this repo the samples reference `CodeAtlas.AspNetCore` and `@codeatlas/web` as projects, in Debug only. External apps use the packages, as `docs/integration.md` describes.
- **Same-origin traffic:** the sample web app's Vite dev server proxies `api` to the backend and `codeatlas` to the collector, so every browser request is same-origin. This also works under code-server's `/absproxy/<port>/`.
- **Running it:**
  - `scripts/demo.sh` starts the processes for a person.
  - `tests/e2e/run.sh` starts Postgres, and Playwright's `webServer` starts the collector and samples for the tests.
- **Release checks:** `scripts/verify-release.sh` gains the samples' Release builds and the sample web production build.
- **One command:** `scripts/test-all.sh` runs everything.

**Tech Stack:**
- .NET 10 Minimal APIs and controllers, Npgsql 10.0.3.
- xUnit v3 with `Microsoft.AspNetCore.Mvc.Testing` 10.0.12 and Testcontainers.PostgreSql 4.15.0 (`postgres:18`).
- React 19.3, Vite 8.3, axios.
- `@playwright/test` 1.63.0 (its Chromium build 1243 matches the local Playwright cache).
- Docker, used only for the samples' Postgres.

**Spec:** `docs/superpowers/specs/2026-10-10-codeatlas-live-mvp-design.md`. The relevant parts:
- §3: samples.
- §4.1: layout.
- §5.1: required changes in a target app.
- §9: the demo.
- §10: tests, release verification, `test-all.sh`, the manual checklist.
- §11: limitations.

## Global Constraints

- **CodeAtlas in Debug only.** Every sample app references CodeAtlas inside `Condition="'$(Configuration)' == 'Debug'"` and calls it inside `#if CODEATLAS`. Release builds contain no CodeAtlas assembly, reference or package. Release web builds contain no runtime, no OpenTelemetry and no collector path.
- **Real demo data.** The demo and the E2E suite use real processes: Postgres, the collector, the samples and a browser. Nothing is simulated.
- **Docker only for the sample's database.** CodeAtlas itself never needs Docker.
- **Loopback only.** Every process listens on `127.0.0.1`. Remote use goes through code-server's `/absproxy/<port>/` (`CODEATLAS_PUBLIC_BASE`).
- **Placeholder credentials only.** The sample database credentials are local placeholders (`codeatlas` / `codeatlas-demo`, `codeatlas-e2e`). No real secrets are committed.
- **Ports**
  - Demo defaults: collector 4318, API 5180, web 5181, Postgres 55433.
  - E2E: collector 14318, API 15180, web 15181, controller 15182, Postgres 55434. These differ from the demo so both can run at once.
- **The code-server visual check stays pending.** `docs/manual-checks.md` lists it, and the maintainer confirms it in the final acceptance.
- **Sample source-mapping regression (spec §10):** 100 % of user-written methods in both samples map, and 0 map wrongly.

## Review Focus

- **Demo started from a code-server terminal behind `/absproxy/`.** Both printed URLs must work in the remote browser, and spans must still reach the collector → Task 6 step `demo.sh --check with CODEATLAS_PUBLIC_BASE`.
- **A request the app makes on page load, before any click.** It must show up in the viewer as a trace with no interaction, and must not be linked to a later click → Task 5 test `the page-load request is a trace without an interaction`.
- **Ctrl+C on the demo.** No `dotnet` child process may survive and keep a port, otherwise the next demo run fails with "address in use" → Task 6 step `stop leaves no listeners`.
- **Every request outside the machine blocked.** The viewer still renders Monaco and the source → Task 5 test `Monaco and source work with every outside request blocked`.
- **A Release publish of a sample next to a Debug one in the same tree.** The Release output must still contain no CodeAtlas → Task 7 checks run both configurations into separate folders.

## File Structure

```
samples/
  MinimalApiSample/
    MinimalApiSample.csproj  Program.cs  appsettings.json
    Data/Database.cs              schema + seed
    Orders/OrderEndpoints.cs      routes; handler spans in Debug
    Orders/OrderService.cs        SQL via Npgsql (no span of its own)
  MinimalApiSample.Web/
    package.json  tsconfig.json  vite.config.ts  index.html
    src/main.tsx  src/App.tsx  src/api.ts  src/styles.css
    test/release.test.ts          production bundle has no CodeAtlas
  ControllerSample/
    ControllerSample.csproj  Program.cs
    Controllers/OrdersController.cs  Services/OrderService.cs
tests/
  Samples.Tests/                  functional tests of both samples (Testcontainers Postgres)
  CodeAtlas.Collector.Tests/SampleSourceMapTests.cs   §10 regression over both samples
  e2e/
    package.json  tsconfig.json  playwright.config.ts  env.ts  run.sh  recorder-editor.sh
    specs/demo.spec.ts  specs/controller.spec.ts  specs/layout.spec.ts
scripts/
  demo.sh  test-all.sh  verify-release.sh (modify)
README.md                         what it is, the demo, how to integrate
docs/integration.md (modify)      browser and collector sections
docs/manual-checks.md             human checks (code-server visual open: pending the maintainer)
docs/limitations.md (modify)
```

---

### Task 1: MinimalApiSample (backend)

**Files:**
- Create:
  - `samples/MinimalApiSample/MinimalApiSample.csproj`, `Program.cs` and `appsettings.json`;
  - `samples/MinimalApiSample/Data/Database.cs`;
  - `samples/MinimalApiSample/Orders/OrderEndpoints.cs` and `Orders/OrderService.cs`.
  - In `tests/Samples.Tests/`: `Samples.Tests.csproj`, `AssemblyInfo.cs`, `PostgresFixture.cs` and `MinimalApiSampleTests.cs`.
- Modify: `CodeAtlas.Live.slnx` (through `dotnet sln add`).

**Interfaces:**
- Produces:
  - `GET /api/products` → `[{ sku, name, stock }]`.
  - `POST /api/orders` with `{ sku, quantity }`:
    - success → `201 { id }`;
    - `quantity ≤ 0` → 400;
    - out of stock → the handler throws `InvalidOperationException("Out of stock: <sku>")` → 500.
  - Seed data: `KB-01` (stock 1,000,000) and `MON-27` (stock 0).
  - Configuration: connection string `ConnectionStrings:Shop`.
  - Handler spans: `handler OrderEndpoints.ListProducts`, `handler OrderEndpoints.CreateOrder`.
  - Public types: `MinimalApiSample.Orders.OrderService`, the marker type for `WebApplicationFactory`.

- [ ] **Step 1: Write the failing tests**

`tests/Samples.Tests/Samples.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <NoWarn>$(NoWarn);xUnit1051</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.1" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageReference Include="Testcontainers.PostgreSql" Version="4.15.0" />
    <PackageReference Include="Npgsql" Version="10.0.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../samples/MinimalApiSample/MinimalApiSample.csproj" />
  </ItemGroup>
</Project>
```

`tests/Samples.Tests/AssemblyInfo.cs`:
```csharp
// One Postgres container per class and real hosts: run classes one after another.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
```

`tests/Samples.Tests/PostgresFixture.cs`:
```csharp
using Testcontainers.PostgreSql;

namespace Samples.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
```

`tests/Samples.Tests/MinimalApiSampleTests.cs`:
```csharp
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
```

Run: `dotnet sln CodeAtlas.Live.slnx add tests/Samples.Tests/Samples.Tests.csproj`

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project tests/Samples.Tests`
Expected: the restore/build fails because the referenced project `samples/MinimalApiSample/MinimalApiSample.csproj` does not exist.

- [ ] **Step 3: Implement the sample**

`samples/MinimalApiSample/MinimalApiSample.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Npgsql" Version="10.0.3" />
  </ItemGroup>

  <!-- CodeAtlas in Debug only. Inside this repo it is a project reference; your own app references the
       CodeAtlas.AspNetCore package the same way (docs/integration.md). -->
  <ItemGroup Condition="'$(Configuration)' == 'Debug'">
    <ProjectReference Include="../../src/CodeAtlas.AspNetCore/CodeAtlas.AspNetCore.csproj" />
  </ItemGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <DefineConstants>$(DefineConstants);CODEATLAS</DefineConstants>
  </PropertyGroup>
</Project>
```

`samples/MinimalApiSample/appsettings.json`:
```json
{
  "ConnectionStrings": {
    "Shop": "Host=127.0.0.1;Port=55433;Database=codeatlas_demo;Username=codeatlas;Password=codeatlas-demo"
  },
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }
}
```

`samples/MinimalApiSample/Program.cs`:
```csharp
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
```

`samples/MinimalApiSample/Data/Database.cs`:
```csharp
using Npgsql;

namespace MinimalApiSample.Data;

public static class Database
{
    /// <summary>Creates the two tables and the demo products once. Runs at start-up, outside any request.</summary>
    public static async Task EnsureCreatedAsync(NpgsqlDataSource db)
    {
        await using var command = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS products (sku text PRIMARY KEY, name text NOT NULL, stock int NOT NULL);
            CREATE TABLE IF NOT EXISTS orders (
                id serial PRIMARY KEY,
                sku text NOT NULL REFERENCES products (sku),
                quantity int NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now());
            INSERT INTO products (sku, name, stock) VALUES
                ('KB-01', 'Mechanical keyboard', 1000000),
                ('MON-27', '27-inch monitor', 0)
            ON CONFLICT (sku) DO NOTHING;
            """);
        await command.ExecuteNonQueryAsync();
    }
}
```

`samples/MinimalApiSample/Orders/OrderEndpoints.cs`:
```csharp
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
```

`samples/MinimalApiSample/Orders/OrderService.cs`:
```csharp
using Npgsql;

namespace MinimalApiSample.Orders;

/// <summary>
/// Plain application code. It has no span of its own: CodeAtlas shows its database calls under the handler that
/// called it, and its frames in exception stack traces.
/// </summary>
public sealed class OrderService(NpgsqlDataSource db)
{
    public async Task<IReadOnlyList<Product>> ListProductsAsync(CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand("SELECT sku, name, stock FROM products ORDER BY sku");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var products = new List<Product>();
        while (await reader.ReadAsync(cancellationToken))
            products.Add(new Product(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return products;
    }

    public async Task<int> CreateAsync(string sku, int quantity, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var check = new NpgsqlCommand("SELECT stock FROM products WHERE sku = $1", connection, transaction))
        {
            check.Parameters.Add(new NpgsqlParameter { Value = sku });
            var stock = await check.ExecuteScalarAsync(cancellationToken) as int? ?? throw new InvalidOperationException($"Unknown product: {sku}");
            if (stock < quantity) throw new InvalidOperationException($"Out of stock: {sku}");
        }

        await using (var take = new NpgsqlCommand("UPDATE products SET stock = stock - $1 WHERE sku = $2", connection, transaction))
        {
            take.Parameters.Add(new NpgsqlParameter { Value = quantity });
            take.Parameters.Add(new NpgsqlParameter { Value = sku });
            await take.ExecuteNonQueryAsync(cancellationToken);
        }

        int id;
        await using (var insert = new NpgsqlCommand("INSERT INTO orders (sku, quantity) VALUES ($1, $2) RETURNING id", connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = sku });
            insert.Parameters.Add(new NpgsqlParameter { Value = quantity });
            id = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        await transaction.CommitAsync(cancellationToken);
        return id;
    }
}

public sealed record Product(string Sku, string Name, int Stock);
```

Run: `dotnet sln CodeAtlas.Live.slnx add samples/MinimalApiSample/MinimalApiSample.csproj`

- [ ] **Step 4: Run to verify the tests pass**

Run: `dotnet test --project tests/Samples.Tests`
Expected: 4 tests pass. Docker must be running for Testcontainers.

- [ ] **Step 5: Commit**

```bash
git add samples/MinimalApiSample tests/Samples.Tests CodeAtlas.Live.slnx
git commit -m "feat(samples): MinimalApiSample shop with Npgsql and an out-of-stock failure"
```

---

### Task 2: ControllerSample

**Files:**
- Create: `samples/ControllerSample/ControllerSample.csproj`, `Program.cs`, `Controllers/OrdersController.cs`, `Services/OrderService.cs`, and `tests/Samples.Tests/ControllerSampleTests.cs`
- Modify: `tests/Samples.Tests/Samples.Tests.csproj` (project reference), `CodeAtlas.Live.slnx`

**Interfaces:**
- Produces:
  - `GET /api/orders` → `[{ id, sku, quantity }]`.
  - `POST /api/orders` with `{ sku, quantity }`:
    - success → 201;
    - `quantity ≤ 0` → `ArgumentOutOfRangeException` from `OrderService.Create` → 500.
  - Action span: `action OrdersController.Create`.
  - Own OpenTelemetry: metrics only, meter `ControllerSample`.
  - Marker type: `ControllerSample.Services.OrderService`.

- [ ] **Step 1: Write the failing tests**

`tests/Samples.Tests/ControllerSampleTests.cs`:
```csharp
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
```

In `tests/Samples.Tests/Samples.Tests.csproj`, add the following line to the `ItemGroup` that holds the MinimalApiSample reference:
```xml
    <ProjectReference Include="../../samples/ControllerSample/ControllerSample.csproj" />
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project tests/Samples.Tests`
Expected: the build fails because the referenced project `samples/ControllerSample/ControllerSample.csproj` does not exist.

- [ ] **Step 3: Implement the sample**

`samples/ControllerSample/ControllerSample.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <!-- The app's own OpenTelemetry (metrics). CodeAtlas must leave it alone, and Release must keep it. -->
  <ItemGroup>
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.19.1" />
  </ItemGroup>

  <!-- CodeAtlas in Debug only (see MinimalApiSample.csproj). -->
  <ItemGroup Condition="'$(Configuration)' == 'Debug'">
    <ProjectReference Include="../../src/CodeAtlas.AspNetCore/CodeAtlas.AspNetCore.csproj" />
  </ItemGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <DefineConstants>$(DefineConstants);CODEATLAS</DefineConstants>
  </PropertyGroup>
</Project>
```

`samples/ControllerSample/Program.cs`:
```csharp
#if CODEATLAS
using CodeAtlas.AspNetCore;
#endif
using ControllerSample.Services;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(OrderService.MeterName));
#if CODEATLAS
builder.AddCodeAtlas();
#endif

var app = builder.Build();
app.MapControllers();
app.Run();
```

`samples/ControllerSample/Controllers/OrdersController.cs`:
```csharp
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
```

`samples/ControllerSample/Services/OrderService.cs`:
```csharp
using System.Diagnostics.Metrics;

namespace ControllerSample.Services;

/// <summary>In-memory orders. No span of its own: a failure here appears as a frame in the action span's exception.</summary>
public sealed class OrderService
{
    public const string MeterName = "ControllerSample";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<int> Created = Meter.CreateCounter<int>("orders.created");

    private readonly Lock _lock = new();
    private readonly List<Order> _orders = [];
    private int _nextId;

    public IReadOnlyList<Order> All()
    {
        lock (_lock) return [.. _orders];
    }

    public Order Create(string sku, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        lock (_lock)
        {
            var order = new Order(++_nextId, sku, quantity);
            _orders.Add(order);
            Created.Add(1);
            return order;
        }
    }
}

public sealed record Order(int Id, string Sku, int Quantity);
```

Run: `dotnet sln CodeAtlas.Live.slnx add samples/ControllerSample/ControllerSample.csproj`

- [ ] **Step 4: Run to verify the tests pass**

Run: `dotnet test --project tests/Samples.Tests`
Expected: 6 tests pass.

If the build reports a duplicate `appsettings.json` from the two referenced web projects, that comes from MinimalApiSample's copy. Do not add an `appsettings.json` to ControllerSample.

- [ ] **Step 5: Commit**

```bash
git add samples/ControllerSample tests/Samples.Tests CodeAtlas.Live.slnx
git commit -m "feat(samples): ControllerSample with its own OpenTelemetry metrics"
```

---

### Task 3: Source-mapping regression over both samples (spec §10)

**Files:**
- Create: `tests/CodeAtlas.Collector.Tests/SampleSourceMapTests.cs`
- Modify: `tests/CodeAtlas.Collector.Tests/CodeAtlas.Collector.Tests.csproj` (build-order references to both samples)

**Interfaces:**
- Consumes:
  - `PdbSourceMap(IReadOnlyList<string> roots)`.
  - `PdbSourceMap.Resolve(string assembly, Guid mvid, int token)`, which returns a `SourceLocation` with `Found`, `File` and `NavigationLine` (Plan 2).
  - The Debug builds of both samples.
- Produces: the regression test.

- [ ] **Step 1: Write the test**

`tests/CodeAtlas.Collector.Tests/SampleSourceMapTests.cs`:
```csharp
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using CodeAtlas.Collector.Source;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Collector.Tests;

/// <summary>
/// Spec §10: every user-written method in the two samples maps to source, and named methods land on their own
/// declaration line (0 wrong). Methods are read from the samples' real Debug builds and their Portable PDBs.
/// </summary>
public class SampleSourceMapTests
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    public static TheoryData<string> Samples => new() { "MinimalApiSample", "ControllerSample" };

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CodeAtlas.Live.slnx"))) return dir.FullName;
        throw new InvalidOperationException("CodeAtlas.Live.slnx not found.");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_user_written_method_maps_to_its_own_declaration(string sample)
    {
        var sampleRoot = Path.Combine(RepoRoot(), "samples", sample);
        var dll = Path.Combine(sampleRoot, "bin", Configuration, "net10.0", sample + ".dll");
        Assert.True(File.Exists(dll), $"{dll} is missing: the sample was not built.");

        using var pe = new PEReader(File.OpenRead(dll));
        var metadata = pe.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        using var map = new PdbSourceMap([sampleRoot]);

        var failures = new List<string>();
        var checkedNamed = 0;
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = metadata.GetString(type.Name);
            // Compiler-generated types (state machines, closures) are reached through their user-written method.
            if (typeName.StartsWith('<') || IsCompilerGenerated(metadata, type.GetCustomAttributes())) continue;

            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0 || IsCompilerGenerated(metadata, method.GetCustomAttributes())) continue;
                var name = metadata.GetString(method.Name);
                var token = MetadataTokens.GetToken(methodHandle);
                var label = $"{typeName}.{name} (0x{token:x8})";

                var location = map.Resolve(dll, mvid, token);
                if (!location.Found)
                {
                    failures.Add($"{label}: not mapped — {location.Reason}");
                    continue;
                }
                if (name.StartsWith('<')) continue; // e.g. <Main>$ for top-level statements: mapped is all we can check.

                var expected = DeclarationLines(location.File!, name, typeName);
                if (!expected.Contains(location.NavigationLine))
                    failures.Add($"{label}: line {location.NavigationLine}, expected one of [{string.Join(", ", expected)}] in {location.File}");
                checkedNamed++;
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(checkedNamed >= 4, $"Only {checkedNamed} named methods were checked in {sample}; the scan is not finding the sample's code.");
    }

    private static bool IsCompilerGenerated(MetadataReader metadata, CustomAttributeHandleCollection attributes) =>
        attributes.Select(metadata.GetCustomAttribute).Any(a =>
            a.Constructor.Kind == HandleKind.MemberReference
            && metadata.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
            && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "CompilerGeneratedAttribute");

    /// <summary>Start lines of every declaration in the file that a method with this metadata name can come from.</summary>
    private static HashSet<int> DeclarationLines(string file, string name, string typeName)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        int Line(Microsoft.CodeAnalysis.SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var nodes = name switch
        {
            ".ctor" => root.DescendantNodes().Where(n =>
                (n is ConstructorDeclarationSyntax c && c.Identifier.Text == typeName)
                || (n is TypeDeclarationSyntax { ParameterList: not null } t && t.Identifier.Text == typeName)),
            _ when name.StartsWith("get_") || name.StartsWith("set_") => root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Identifier.Text == name[4..])
                .SelectMany(p => new Microsoft.CodeAnalysis.SyntaxNode[] { p }.Concat(p.AccessorList?.Accessors ?? [])),
            _ => root.DescendantNodes().Where(n =>
                (n is MethodDeclarationSyntax m && m.Identifier.Text == name)
                || (n is LocalFunctionStatementSyntax l && l.Identifier.Text == name)),
        };
        return nodes.Select(Line).ToHashSet();
    }
}
```

In `tests/CodeAtlas.Collector.Tests/CodeAtlas.Collector.Tests.csproj`, add the following to the `ItemGroup` with the project references:
```xml
    <!-- Built first (not referenced): SampleSourceMapTests reads their Debug output and PDBs. -->
    <ProjectReference Include="../../samples/MinimalApiSample/MinimalApiSample.csproj" ReferenceOutputAssembly="false" />
    <ProjectReference Include="../../samples/ControllerSample/ControllerSample.csproj" ReferenceOutputAssembly="false" />
```

- [ ] **Step 2: Run it and watch it catch a wrong mapping**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests -- --filter-class "*SampleSourceMapTests"`
Expected: 2 tests pass. The mapping already exists (Plan 2), so this is a regression test over new code.

To prove the test can fail, apply this mutation in `src/CodeAtlas.Collector/Source/PdbSourceMap.cs`: change the Roslyn result's navigation line to `start + 1`. In the `return new SourceLocation(true, …` that uses the Roslyn range, pass `enclosing.Value.Start + 1` as the navigation line. Run again.
Expected: FAIL. The failure lists methods such as `OrderEndpoints.CreateOrder … line N+1, expected one of [N]`.

Revert the mutation (`git checkout src/CodeAtlas.Collector/Source/PdbSourceMap.cs`) and run again.
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/CodeAtlas.Collector.Tests
git commit -m "test(collector): source-mapping regression over both samples (spec §10)"
```

---

### Task 4: MinimalApiSample.Web (React app)

**Files:**
- Create:
  - In `samples/MinimalApiSample.Web/`: `package.json`, `tsconfig.json`, `vite.config.ts` and `index.html`.
  - In `samples/MinimalApiSample.Web/src/`: `main.tsx`, `App.tsx`, `api.ts` and `styles.css`.
  - In `samples/MinimalApiSample.Web/test/`: `release.test.ts` and `app.test.tsx`.
- Modify: `.gitignore` (sample `dist`)

**Interfaces:**
- Consumes:
  - `@codeatlas/web`: `trackInteraction` and `instrumentAxios`;
  - `@codeatlas/web/runtime`: `initCodeAtlas` (Plan 3), linked as `file:../../src/web`, which needs `src/web/dist` to be built.
- Produces:
  - Buttons:
    - `Create Order`: explicit fetch, KB-01;
    - `Create Order (out of stock)`: explicit fetch, MON-27;
    - `Create Order (Axios)`: explicit Axios;
    - `Refresh products`: plain fetch, heuristic.
  - A status line (`role=status`) with `Order created (HTTP 201)`, `Order failed (HTTP 500)` and similar.
  - A product table.
  - Dev server env:
    - `DEV_PUBLIC_URL`: optional, for absproxy;
    - `SAMPLE_API`: default `http://127.0.0.1:5180`;
    - `CODEATLAS_COLLECTOR`: default `http://127.0.0.1:4318`.

- [ ] **Step 1: Build the browser package, create the app's package and install**

Run: `(cd src/web && npm ci && npm run build)`

`samples/MinimalApiSample.Web/package.json`:
```json
{
  "name": "minimal-api-sample-web",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc -p tsconfig.json --noEmit && vite build",
    "test": "vitest run"
  },
  "dependencies": {
    "@codeatlas/web": "file:../../src/web",
    "axios": "1.20.0",
    "react": "19.3.0",
    "react-dom": "19.3.0"
  },
  "devDependencies": {
    "@testing-library/dom": "10.4.2",
    "@testing-library/react": "16.3.3",
    "@testing-library/user-event": "14.6.7",
    "@types/node": "24.19.2",
    "@types/react": "19.3.0",
    "@types/react-dom": "19.3.0",
    "@vitejs/plugin-react": "6.1.2",
    "jsdom": "30.1.2",
    "typescript": "~6.0.2",
    "vite": "8.3.4",
    "vitest": "5.0.3"
  }
}
```
Run: `cd samples/MinimalApiSample.Web && npm install`
Expected: `package-lock.json` is created, and `node_modules/@codeatlas/web` is a symlink to `../../src/web`.

`samples/MinimalApiSample.Web/tsconfig.json`:
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "skipLibCheck": true,
    "noEmit": true,
    "isolatedModules": true,
    "types": ["vite/client", "node"]
  },
  "include": ["src", "test", "vite.config.ts"]
}
```

`samples/MinimalApiSample.Web/vite.config.ts`:
```ts
/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

export default defineConfig(({ command, mode }) => {
  // Behind a path proxy (code-server's /absproxy/5181/), DEV_PUBLIC_URL is the address the browser uses.
  const env = command === 'serve' ? loadEnv(mode, import.meta.dirname, '') : {}
  const publicUrl = env.DEV_PUBLIC_URL ? new URL(env.DEV_PUBLIC_URL) : undefined
  const base = publicUrl ? publicUrl.pathname.replace(/\/?$/, '/') : '/'
  const api = env.SAMPLE_API || 'http://127.0.0.1:5180'
  const collector = env.CODEATLAS_COLLECTOR || 'http://127.0.0.1:4318'

  return {
    base,
    plugins: [react()],
    server: {
      allowedHosts: publicUrl ? [publicUrl.hostname] : [],
      hmr: publicUrl ? { clientPort: Number(publicUrl.port) || (publicUrl.protocol === 'https:' ? 443 : 80) } : undefined,
      // Everything is same-origin for the browser: API calls carry their traceparent without CORS, and the
      // browser's spans reach the collector the same way (also through code-server's /absproxy/).
      proxy: {
        [`${base}api`]: { target: api, rewrite: (url: string) => url.slice(base.length - 1) },
        [`${base}codeatlas`]: { target: collector, rewrite: (url: string) => url.slice(`${base}codeatlas`.length) },
      },
    },
    test: { environment: 'jsdom' },
  }
})
```

`samples/MinimalApiSample.Web/index.html`:
```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Shop — CodeAtlas sample</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="./src/main.tsx"></script>
  </body>
</html>
```

Append `samples/MinimalApiSample.Web/dist/` to `.gitignore`.

- [ ] **Step 2: Write the failing tests**

`samples/MinimalApiSample.Web/test/app.test.tsx`:
```tsx
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { App } from '../src/App'

const products = [{ sku: 'KB-01', name: 'Mechanical keyboard', stock: 5 }, { sku: 'MON-27', name: '27-inch monitor', stock: 0 }]

function stubBackend() {
  const calls: { url: string; init?: RequestInit }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, init })
    if (url.endsWith('api/products')) return Response.json(products)
    const body = JSON.parse(String(init?.body))
    return body.sku === 'MON-27' ? Response.json({ title: 'Out of stock' }, { status: 500 }) : Response.json({ id: 7 }, { status: 201 })
  }))
  return calls
}

describe('shop', () => {
  afterEach(() => { cleanup(); vi.unstubAllGlobals() })

  it('lists products on load', async () => {
    stubBackend()
    render(<App />)
    expect(await screen.findByRole('cell', { name: 'KB-01' })).toBeTruthy()
  })

  it('Create Order posts one KB-01 and reports the status', async () => {
    const calls = stubBackend()
    render(<App />)
    await userEvent.click(await screen.findByRole('button', { name: 'Create Order' }))

    expect((await screen.findByRole('status')).textContent).toBe('Order created (HTTP 201)')
    const post = calls.find(c => c.init?.method === 'POST')!
    expect(post.url).toBe('/api/orders')
    expect(JSON.parse(String(post.init!.body))).toEqual({ sku: 'KB-01', quantity: 1 })
  })

  it('the out-of-stock button reports the failure', async () => {
    stubBackend()
    render(<App />)
    await userEvent.click(await screen.findByRole('button', { name: 'Create Order (out of stock)' }))

    expect((await screen.findByRole('status')).textContent).toBe('Order failed (HTTP 500)')
  })
})
```

`samples/MinimalApiSample.Web/test/release.test.ts`:
```ts
// @vitest-environment node
import { mkdtempSync, readdirSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'
import { describe, expect, it } from 'vitest'

const root = fileURLToPath(new URL('..', import.meta.url))

describe('production build', () => {
  it('contains the app but no CodeAtlas runtime, OpenTelemetry or collector path', async () => {
    const outDir = mkdtempSync(join(tmpdir(), 'sample-web-'))
    // Vitest sets NODE_ENV=test, which would keep import.meta.env.DEV branches; a real build has production.
    const nodeEnv = process.env.NODE_ENV
    process.env.NODE_ENV = 'production'
    try {
      await build({ root, mode: 'production', logLevel: 'silent', build: { outDir, emptyOutDir: true } })
    } finally {
      process.env.NODE_ENV = nodeEnv
    }
    const js = readdirSync(join(outDir, 'assets')).filter(f => f.endsWith('.js')).map(f => readFileSync(join(outDir, 'assets', f), 'utf8')).join('\n')

    expect(js).toContain('Create Order')
    expect(js).not.toMatch(/traceparent|codeatlas-web|interaction: |codeatlas\/v1/)
  }, 60_000)
})
```

- [ ] **Step 3: Run to verify failure**

Run: `cd samples/MinimalApiSample.Web && npx vitest run`
Expected: FAIL. `app.test.tsx` reports `Failed to resolve import "../src/App"`, and the release build fails because `src/main.tsx` is missing.

- [ ] **Step 4: Implement the app**

`samples/MinimalApiSample.Web/src/api.ts`:
```ts
import axios from 'axios'
import { instrumentAxios } from '@codeatlas/web'

/** Paths under the app's base, so the dev-server proxy also works behind code-server's /absproxy/<port>/. */
export const apiPath = (path: string) => `${import.meta.env.BASE_URL}api/${path}`

export const api = axios.create({ baseURL: `${import.meta.env.BASE_URL}api` })
// Requests made with ix.axiosConfig() get trace context; without the CodeAtlas runtime this does nothing.
instrumentAxios(api)

export interface Product {
  sku: string
  name: string
  stock: number
}
```

`samples/MinimalApiSample.Web/src/App.tsx`:
```tsx
import { trackInteraction } from '@codeatlas/web'
import { useCallback, useEffect, useState } from 'react'
import { api, apiPath, type Product } from './api'

export function App() {
  const [products, setProducts] = useState<Product[]>([])
  const [status, setStatus] = useState('')

  // Plain fetch: when it follows a click, CodeAtlas links it to that click by timing ("heuristic").
  const refresh = useCallback(async () => {
    const response = await fetch(apiPath('products'))
    if (response.ok) setProducts(await response.json())
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Explicit: every request made through ix belongs to this interaction, across awaits.
  const createOrder = (label: string, sku: string) =>
    trackInteraction(label, async ix => {
      const response = await ix.fetch(apiPath('orders'), {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ sku, quantity: 1 }),
      })
      // No product refresh here: it would be a newer trace, and the viewer's "Follow latest" would show it instead.
      setStatus(response.ok ? `Order created (HTTP ${response.status})` : `Order failed (HTTP ${response.status})`)
    })

  const createWithAxios = () =>
    trackInteraction('Create order (Axios)', async ix => {
      try {
        const response = await api.post('orders', { sku: 'KB-01', quantity: 1 }, ix.axiosConfig())
        setStatus(`Order created with Axios (HTTP ${response.status})`)
      } catch {
        setStatus('Order failed (Axios)')
      }
    })

  return (
    <main>
      <h1>Shop</h1>
      <p className="hint">A sample app for CodeAtlas Live. Click a button, then look at the viewer.</p>
      <div className="actions">
        <button type="button" onClick={() => void createOrder('Create order', 'KB-01')}>Create Order</button>
        <button type="button" onClick={() => void createOrder('Create order (out of stock)', 'MON-27')}>Create Order (out of stock)</button>
        <button type="button" onClick={() => void createWithAxios()}>Create Order (Axios)</button>
        <button type="button" onClick={() => void refresh()}>Refresh products</button>
      </div>
      <p role="status">{status}</p>
      <table>
        <thead>
          <tr><th>SKU</th><th>Product</th><th>Stock</th></tr>
        </thead>
        <tbody>
          {products.map(p => (
            <tr key={p.sku}><td>{p.sku}</td><td>{p.name}</td><td>{p.stock}</td></tr>
          ))}
        </tbody>
      </table>
    </main>
  )
}
```

`samples/MinimalApiSample.Web/src/main.tsx`:
```tsx
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import './styles.css'

// CodeAtlas runtime: development only. Production builds contain none of it (test/release.test.ts, verify-release.sh).
if (import.meta.env.DEV)
  void import('@codeatlas/web/runtime').then(m =>
    m.initCodeAtlas({ collectorUrl: `${import.meta.env.BASE_URL}codeatlas/v1/traces`, serviceName: 'MinimalApiSample.Web' }))

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
```

`samples/MinimalApiSample.Web/src/styles.css`:
```css
:root {
  color-scheme: light dark;
  font-family: system-ui, sans-serif;
}
body {
  margin: 0;
}
main {
  max-width: 44rem;
  margin: 3rem auto;
  padding: 0 1rem;
}
.hint {
  opacity: 0.7;
}
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin: 1.5rem 0 1rem;
}
button {
  font: inherit;
  padding: 0.5rem 0.9rem;
  border-radius: 0.5rem;
  border: 1px solid color-mix(in oklab, currentColor 25%, transparent);
  background: transparent;
  cursor: pointer;
}
button:hover {
  background: color-mix(in oklab, currentColor 8%, transparent);
}
table {
  width: 100%;
  border-collapse: collapse;
}
th,
td {
  text-align: left;
  padding: 0.4rem 0.5rem;
  border-bottom: 1px solid color-mix(in oklab, currentColor 15%, transparent);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd samples/MinimalApiSample.Web && npx vitest run && npx tsc -p tsconfig.json --noEmit`
Expected: 4 tests pass, and there are no type errors.

- [ ] **Step 6: Commit**

```bash
git add .gitignore samples/MinimalApiSample.Web
git commit -m "feat(samples): React shop app with explicit, Axios and heuristic interactions"
```

---

### Task 5: End-to-end suite (Playwright, real processes)

**Files:**
- Create:
  - In `tests/e2e/`: `package.json`, `tsconfig.json`, `playwright.config.ts`, `env.ts`, `run.sh` and `recorder-editor.sh`.
  - In `tests/e2e/specs/`: `demo.spec.ts`, `controller.spec.ts` and `layout.spec.ts`.
- Modify: `.gitignore`

**Interfaces:**
- Consumes:
  - The samples (Tasks 1, 2 and 4).
  - The collector flags (Plan 2): `--urls`, `--source-root`, `--editor code-server` and `--editor-command`.
  - The viewer roles (Plan 4):
    - `tree "Execution tree"`, `treeitem`, `complementary "Inspector"`;
    - `main "Trace"`, `tab "Explain"`, `region "Errors"`, `status "Live connection"`.
- Produces: `tests/e2e/run.sh [playwright args]`.

- [ ] **Step 1: Create the harness**

`tests/e2e/package.json`:
```json
{
  "name": "codeatlas-e2e",
  "private": true,
  "type": "module",
  "scripts": { "test": "playwright test" },
  "devDependencies": {
    "@playwright/test": "1.63.0",
    "@types/node": "24.19.2",
    "typescript": "~6.0.2"
  }
}
```
Run: `cd tests/e2e && npm install`. If Chromium build 1243 is not in `~/.cache/ms-playwright`, also run `npx playwright install chromium`.

`tests/e2e/tsconfig.json`:
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true,
    "types": ["node"]
  },
  "include": ["*.ts", "specs"]
}
```

`tests/e2e/env.ts`:
```ts
import { fileURLToPath } from 'node:url'

export const repo = fileURLToPath(new URL('../..', import.meta.url)).replace(/\/$/, '')
// Different from the demo's ports, so a running demo does not get in the way.
export const ports = { collector: 14318, api: 15180, web: 15181, controller: 15182, db: 55434 }
export const urls = {
  viewer: `http://127.0.0.1:${ports.collector}/`,
  app: `http://127.0.0.1:${ports.web}/`,
  controller: `http://127.0.0.1:${ports.controller}/`,
}
export const connectionString = `Host=127.0.0.1;Port=${ports.db};Database=codeatlas_e2e;Username=codeatlas;Password=codeatlas-e2e`
export const recorderFile = `${repo}/tests/e2e/.recorder/editor-args.txt`
```

`tests/e2e/recorder-editor.sh`:
```bash
#!/usr/bin/env bash
# Stands in for code-server in the E2E suite: records the arguments the collector passes, opens nothing.
mkdir -p "$(dirname "${CODEATLAS_RECORDER_FILE:?}")"
printf '%s\n' "$@" > "$CODEATLAS_RECORDER_FILE"
```
Run `chmod +x tests/e2e/recorder-editor.sh`.

`tests/e2e/playwright.config.ts`:
```ts
import { defineConfig } from '@playwright/test'
import { connectionString, ports, recorderFile, repo, urls } from './env'

const collectorEndpoint = `http://127.0.0.1:${ports.collector}/v1/traces`
const sampleEnv = (port: number) => ({
  ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
  ASPNETCORE_ENVIRONMENT: 'Development',
  CodeAtlas__Endpoint: collectorEndpoint,
})

export default defineConfig({
  testDir: './specs',
  workers: 1,
  timeout: 90_000,
  expect: { timeout: 20_000 },
  reporter: [['list']],
  use: { viewport: { width: 1440, height: 900 }, trace: 'retain-on-failure' },
  webServer: [
    {
      command: `dotnet run --project ${repo}/src/CodeAtlas.Collector -- --urls http://127.0.0.1:${ports.collector} --source-root ${repo}/samples --editor code-server --editor-command ${repo}/tests/e2e/recorder-editor.sh`,
      url: `${urls.viewer}api/info`,
      env: { CODEATLAS_RECORDER_FILE: recorderFile },
      timeout: 240_000,
    },
    {
      command: `dotnet run --project ${repo}/samples/MinimalApiSample --no-launch-profile`,
      url: `http://127.0.0.1:${ports.api}/api/products`,
      env: { ...sampleEnv(ports.api), ConnectionStrings__Shop: connectionString },
      timeout: 240_000,
    },
    {
      command: `dotnet run --project ${repo}/samples/ControllerSample --no-launch-profile`,
      url: `${urls.controller}api/orders`,
      env: sampleEnv(ports.controller),
      timeout: 240_000,
    },
    {
      command: `npx vite --port ${ports.web} --strictPort --host 127.0.0.1`,
      cwd: `${repo}/samples/MinimalApiSample.Web`,
      url: urls.app,
      env: { SAMPLE_API: `http://127.0.0.1:${ports.api}`, CODEATLAS_COLLECTOR: `http://127.0.0.1:${ports.collector}` },
      timeout: 120_000,
    },
  ],
})
```

`tests/e2e/run.sh`:
```bash
#!/usr/bin/env bash
# End-to-end tests with real processes: Postgres in Docker (the sample's database only), the collector, both samples,
# the sample web app and Chromium. Extra arguments go to Playwright (e.g. --grep "Create Order").
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"

db=codeatlas-e2e-db
docker rm -f "$db" > /dev/null 2>&1 || true
docker run -d --rm --name "$db" -p 127.0.0.1:55434:5432 \
  -e POSTGRES_USER=codeatlas -e POSTGRES_PASSWORD=codeatlas-e2e -e POSTGRES_DB=codeatlas_e2e postgres:18 > /dev/null
trap 'docker stop "$db" > /dev/null 2>&1 || true' EXIT
for _ in $(seq 1 120); do
  docker exec "$db" psql -U codeatlas -d codeatlas_e2e -c 'select 1' > /dev/null 2>&1 && break
  sleep 0.5
done

"$root/scripts/build-viewer.sh" > /dev/null
(cd "$root/src/web" && npm ci --silent && npm run build --silent)
(cd "$root/samples/MinimalApiSample.Web" && npm ci --silent)
cd "$root/tests/e2e"
npm ci --silent
rm -rf .recorder
npx playwright test "$@"
```
Run `chmod +x tests/e2e/run.sh`.

Append to `.gitignore`:
```
tests/e2e/test-results/
tests/e2e/playwright-report/
tests/e2e/.recorder/
```

- [ ] **Step 2: Write the specs**

`tests/e2e/specs/demo.spec.ts`:
```ts
import { readFileSync, rmSync } from 'node:fs'
import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { recorderFile, repo, urls } from '../env'

async function openBoth(context: BrowserContext) {
  const shop = await context.newPage()
  await shop.goto(urls.app)
  await expect(shop.getByRole('cell', { name: 'KB-01' })).toBeVisible()
  const atlas = await context.newPage()
  await atlas.goto(urls.viewer)
  await expect(atlas.getByRole('status', { name: 'Live connection' })).toHaveText(/Live/)
  return { shop, atlas }
}

const trace = (atlas: Page) => atlas.getByRole('main', { name: 'Trace' })
const rows = (atlas: Page) => atlas.getByRole('tree', { name: 'Execution tree' }).getByRole('treeitem')
const rowOf = (atlas: Page, kind: string) => rows(atlas).filter({ hasText: kind }).first()
const inspector = (atlas: Page) => atlas.getByRole('complementary', { name: 'Inspector' })

test('Create Order: click → tree → handler source → database span without SQL', async ({ context }) => {
  const { shop, atlas } = await openBoth(context)

  await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
  await expect(shop.getByRole('status')).toHaveText('Order created (HTTP 201)')

  await expect(trace(atlas).getByText('Create order', { exact: true })).toBeVisible()
  await expect(trace(atlas).getByText('explicit')).toBeVisible()
  await expect(trace(atlas).getByText('201')).toBeVisible()
  for (const kind of ['interaction', 'browser-request', 'server', 'endpoint', 'handler'])
    await expect(rows(atlas).filter({ hasText: kind }).first()).toBeVisible()
  await expect(rows(atlas).filter({ hasText: 'SELECT products' })).toHaveCount(1)
  await expect(rows(atlas).filter({ hasText: 'INSERT orders' })).toHaveCount(1)
  await expect(rows(atlas).filter({ hasText: 'route → OrderEndpoints.CreateOrder' })).toHaveCount(1)

  await rowOf(atlas, 'handler').click()
  await expect(inspector(atlas).getByRole('heading', { name: 'MinimalApiSample.Orders.OrderEndpoints.CreateOrder' })).toBeVisible()
  await expect(inspector(atlas).getByText(/MinimalApiSample\/Orders\/OrderEndpoints\.cs/)).toBeVisible()
  await expect(inspector(atlas).locator('.monaco-editor')).toContainText('CreateOrder')

  await rows(atlas).filter({ hasText: 'INSERT orders' }).click()
  await expect(inspector(atlas).getByText('SQL text, parameters and connection details are never exported.')).toBeVisible()
  await expect(inspector(atlas)).not.toContainText('VALUES')
})

test('the page-load request is a trace without an interaction', async ({ context }) => {
  const { atlas } = await openBoth(context)

  const list = atlas.getByRole('complementary', { name: 'Recent activity' })
  const pageLoad = list.getByRole('button').filter({ hasText: 'GET /api/products' }).first()
  await expect(pageLoad).toContainText('No interaction')
})

test('Out of stock: 500, the exception on the handler, and Explain names it', async ({ context }) => {
  const { shop, atlas } = await openBoth(context)

  await shop.getByRole('button', { name: 'Create Order (out of stock)' }).click()
  await expect(shop.getByRole('status')).toHaveText('Order failed (HTTP 500)')

  await expect(trace(atlas).getByText('Create order (out of stock)', { exact: true })).toBeVisible()
  await expect(trace(atlas).getByText('500')).toBeVisible()
  const handler = rowOf(atlas, 'handler')
  await expect(handler).toContainText('error')
  await handler.click()
  const exception = inspector(atlas).getByRole('region', { name: 'Exception' })
  await expect(exception).toContainText('System.InvalidOperationException')
  await expect(exception).toContainText('Out of stock: MON-27')
  await expect(exception).toContainText('OrderService.CreateAsync')

  await atlas.getByRole('tab', { name: 'Explain' }).click()
  await expect(atlas.getByRole('region', { name: 'Errors' })).toContainText('Out of stock: MON-27')
})

test('Refresh products is linked by timing; Axios with ix.axiosConfig() is linked explicitly', async ({ context }) => {
  const { shop, atlas } = await openBoth(context)

  await shop.getByRole('button', { name: 'Refresh products' }).click()
  await expect(trace(atlas).getByText('Refresh products', { exact: true })).toBeVisible()
  await expect(trace(atlas).getByText('heuristic')).toBeVisible()

  await shop.getByRole('button', { name: 'Create Order (Axios)' }).click()
  await expect(shop.getByRole('status')).toHaveText('Order created with Axios (HTTP 201)')
  await expect(trace(atlas).getByText('Create order (Axios)', { exact: true })).toBeVisible()
  await expect(trace(atlas).getByText('explicit')).toBeVisible()
  await expect(rows(atlas).filter({ hasText: 'handler' })).toHaveCount(1)
})

test('Open in code-server hands the editor the declaration line, as one argument', async ({ context }) => {
  rmSync(recorderFile, { force: true })
  const { shop, atlas } = await openBoth(context)
  await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
  await expect(trace(atlas).getByText('Create order', { exact: true })).toBeVisible()
  await rowOf(atlas, 'handler').click()

  await inspector(atlas).getByRole('button', { name: 'Open in code-server' }).click()

  await expect(inspector(atlas).getByRole('status')).toHaveText(/^Opened .*OrderEndpoints\.cs:\d+ in code-server$/)
  const file = `${repo}/samples/MinimalApiSample/Orders/OrderEndpoints.cs`
  const line = readFileSync(file, 'utf8').split('\n').findIndex(l => l.includes('public static async Task<IResult> CreateOrder(')) + 1
  expect(readFileSync(recorderFile, 'utf8').trim().split('\n')).toEqual(['-r', '-g', `${file}:${line}`])
})

test('Monaco and source work with every outside request blocked', async ({ context }) => {
  const blocked: string[] = []
  await context.route('**/*', route => {
    const host = new URL(route.request().url()).hostname
    if (host === '127.0.0.1' || host === 'localhost') return route.continue()
    blocked.push(route.request().url())
    return route.abort()
  })
  const { shop, atlas } = await openBoth(context)
  await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
  await expect(trace(atlas).getByText('Create order', { exact: true })).toBeVisible()

  await rowOf(atlas, 'handler').click()

  await expect(inspector(atlas).locator('.monaco-editor')).toContainText('CreateOrder')
  expect(blocked).toEqual([])
})
```

`tests/e2e/specs/controller.spec.ts`:
```ts
import { expect, test } from '@playwright/test'
import { urls } from '../env'

test('controller sample: action span with source, and a service exception as a stack frame', async ({ page, request }) => {
  await page.goto(urls.viewer)
  await expect(page.getByRole('status', { name: 'Live connection' })).toHaveText(/Live/)
  const trace = page.getByRole('main', { name: 'Trace' })
  const rows = page.getByRole('tree', { name: 'Execution tree' }).getByRole('treeitem')
  const inspector = page.getByRole('complementary', { name: 'Inspector' })

  expect((await request.post(`${urls.controller}api/orders`, { data: { sku: 'KB-01', quantity: 2 } })).status()).toBe(201)
  await expect(trace.getByText('201')).toBeVisible()
  await expect(trace.getByText('No interaction')).toBeVisible()
  await rows.filter({ hasText: 'action' }).first().click()
  await expect(inspector.getByRole('heading', { name: 'ControllerSample.Controllers.OrdersController.Create' })).toBeVisible()
  await expect(inspector.getByText(/ControllerSample\/Controllers\/OrdersController\.cs/)).toBeVisible()
  await expect(inspector.locator('.monaco-editor')).toContainText('Create(CreateOrder request)')

  expect((await request.post(`${urls.controller}api/orders`, { data: { sku: 'KB-01', quantity: 0 } })).status()).toBe(500)
  await expect(trace.getByText('500')).toBeVisible()
  await rows.filter({ hasText: 'action' }).first().click()
  const exception = inspector.getByRole('region', { name: 'Exception' })
  await expect(exception).toContainText('System.ArgumentOutOfRangeException')
  await expect(exception).toContainText('OrderService.Create')
})
```

`tests/e2e/specs/layout.spec.ts`:
```ts
import { expect, test } from '@playwright/test'
import { urls } from '../env'

for (const width of [1280, 1440]) {
  for (const colorScheme of ['light', 'dark'] as const) {
    test(`viewer at ${width}px, ${colorScheme}: no horizontal scroll`, async ({ browser }, testInfo) => {
      const context = await browser.newContext({ viewport: { width, height: 800 }, colorScheme })
      const shop = await context.newPage()
      await shop.goto(urls.app)
      await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
      const page = await context.newPage()
      await page.goto(urls.viewer)
      await expect(page.getByRole('tree', { name: 'Execution tree' })).toBeVisible()
      await page.getByRole('tree', { name: 'Execution tree' }).getByRole('treeitem').filter({ hasText: 'handler' }).first().click()
      await expect(page.locator('.monaco-editor')).toBeVisible()

      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      await testInfo.attach(`viewer-${width}-${colorScheme}`, { body: await page.screenshot(), contentType: 'image/png' })
      await context.close()
    })
  }
}
```

- [ ] **Step 3: Run the suite**

Run: `tests/e2e/run.sh`
Expected: 11 tests pass (6 demo, 1 controller, 4 layout).

A test that fails on a locator or a text is a finding about the viewer or the samples, as the spec describes them. Debug it with `npx playwright test --trace on` and the traces in `test-results/`. Change the code under test, not the assertion, unless the assertion contradicts the spec. If so, record a ruling.

- [ ] **Step 4: Prove the editor test can fail**

In `specs/demo.spec.ts`, temporarily change the expected argument to `${file}:${line + 1}`.

Run: `tests/e2e/run.sh --grep "Open in code-server"`
Expected: FAIL. The recorded line differs.

Revert the test edit, run again, and expect PASS.

- [ ] **Step 5: Commit**

```bash
git add .gitignore tests/e2e
git commit -m "test(e2e): real-process Playwright suite for the demo, failures, editor, offline Monaco and layout"
```

---

### Task 6: `scripts/demo.sh`

**Files:**
- Create: `scripts/demo.sh`
- Modify: `.gitignore` (`artifacts/` is already ignored; logs go to `artifacts/demo-logs`)

**Interfaces:**
- Consumes: the samples, the collector and `scripts/build-viewer.sh`.
- Produces:
  - Running `scripts/demo.sh` prints two URLs and runs until Ctrl+C.
  - `scripts/demo.sh --check` starts everything, verifies it, and stops.
  - Environment variables:
    - `CODEATLAS_PUBLIC_BASE`: e.g. `https://code.example.com/absproxy`.
    - `COLLECTOR_PORT`, `API_PORT`, `WEB_PORT` and `DB_PORT`.

- [ ] **Step 1: Write the script**

`scripts/demo.sh`:
```bash
#!/usr/bin/env bash
# The "Create Order" demo with real processes: Postgres in Docker (a dependency of the sample only — CodeAtlas needs
# no Docker), the collector, MinimalApiSample and its React app. Ctrl+C stops everything.
#
#   scripts/demo.sh
#   CODEATLAS_PUBLIC_BASE=https://code.example.com/absproxy scripts/demo.sh   # through code-server's /absproxy/<port>/
#   scripts/demo.sh --check                                                   # start, verify, stop
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
check=false
[ "${1:-}" = "--check" ] && check=true
collector_port="${COLLECTOR_PORT:-4318}"
api_port="${API_PORT:-5180}"
web_port="${WEB_PORT:-5181}"
db_port="${DB_PORT:-55433}"
base="${CODEATLAS_PUBLIC_BASE:-}"
base="${base%/}"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
logs="$root/artifacts/demo-logs"
mkdir -p "$logs"

for port in "$collector_port" "$api_port" "$web_port"; do
  if ss -ltn "sport = :$port" | grep -q LISTEN; then echo "demo: port $port is already in use" >&2; exit 1; fi
done

db=codeatlas-demo-db
pgids=()
cleanup() {
  # Each process runs in its own process group, so `dotnet run` and `vite` stop together with their children.
  for pgid in "${pgids[@]}"; do kill -TERM -- "-$pgid" 2> /dev/null || true; done
  docker stop "$db" > /dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM
start() { # start <log> <command...>
  local log="$1"; shift
  setsid "$@" > "$logs/$log" 2>&1 &
  pgids+=($!)
}
wait_for() { # wait_for <url> <what>
  for _ in $(seq 1 240); do curl -sf -o /dev/null "$1" && return 0; sleep 0.5; done
  echo "demo: $2 did not start; see $logs" >&2
  exit 1
}

echo "demo: building the viewer and the browser package…"
"$root/scripts/build-viewer.sh" > "$logs/build.log" 2>&1
(cd "$root/src/web" && npm ci --silent && npm run build --silent) >> "$logs/build.log" 2>&1
(cd "$root/samples/MinimalApiSample.Web" && npm ci --silent) >> "$logs/build.log" 2>&1

echo "demo: starting Postgres for the sample (Docker, port $db_port)…"
docker rm -f "$db" > /dev/null 2>&1 || true
docker run -d --rm --name "$db" -p "127.0.0.1:$db_port:5432" \
  -e POSTGRES_USER=codeatlas -e POSTGRES_PASSWORD=codeatlas-demo -e POSTGRES_DB=codeatlas_demo postgres:18 > /dev/null
for _ in $(seq 1 120); do
  docker exec "$db" psql -U codeatlas -d codeatlas_demo -c 'select 1' > /dev/null 2>&1 && break
  sleep 0.5
done

collector_args=(--urls "http://127.0.0.1:$collector_port" --source-root "$root/samples")
web_public=""
if [ -n "$base" ]; then
  collector_args+=(--public-url "$base/$collector_port/")
  web_public="$base/$web_port/"
fi

echo "demo: starting the collector, the sample API and the sample web app…"
start collector.log dotnet run --project "$root/src/CodeAtlas.Collector" -- "${collector_args[@]}"
start api.log env ASPNETCORE_URLS="http://127.0.0.1:$api_port" ASPNETCORE_ENVIRONMENT=Development \
  ConnectionStrings__Shop="Host=127.0.0.1;Port=$db_port;Database=codeatlas_demo;Username=codeatlas;Password=codeatlas-demo" \
  CodeAtlas__Endpoint="http://127.0.0.1:$collector_port/v1/traces" \
  dotnet run --project "$root/samples/MinimalApiSample" --no-launch-profile
start web.log env DEV_PUBLIC_URL="$web_public" SAMPLE_API="http://127.0.0.1:$api_port" \
  CODEATLAS_COLLECTOR="http://127.0.0.1:$collector_port" \
  bash -c "cd '$root/samples/MinimalApiSample.Web' && exec npx vite --port $web_port --strictPort --host 127.0.0.1"

web_path="/"
[ -n "$web_public" ] && web_path="$(python3 -c 'import sys,urllib.parse;print(urllib.parse.urlparse(sys.argv[1]).path)' "$web_public")"
wait_for "http://127.0.0.1:$collector_port/api/info" "the collector"
wait_for "http://127.0.0.1:$api_port/api/products" "the sample API"
wait_for "http://127.0.0.1:$web_port$web_path" "the sample web app"

app_url="${web_public:-http://127.0.0.1:$web_port/}"
viewer_url="http://127.0.0.1:$collector_port/"
[ -n "$base" ] && viewer_url="$base/$collector_port/"
echo
echo "  Window A — the app:     $app_url"
echo "  Window B — the viewer:  $viewer_url"
echo
echo "  Click \"Create Order\" in window A and watch window B. Logs: $logs"

if $check; then
  curl -sf "http://127.0.0.1:$web_port${web_path}api/products" | grep -q KB-01
  curl -sf "http://127.0.0.1:$collector_port/" | grep -q '<div id="root">'
  # A span sent through the app's proxy path reaches the collector.
  curl -sf -o /dev/null -X POST -H 'content-type: application/json' --data '{"resourceSpans":[]}' \
    "http://127.0.0.1:$web_port${web_path}codeatlas/v1/traces"
  echo "demo: check passed"
  exit 0
fi
wait
```
Run `chmod +x scripts/demo.sh`.

- [ ] **Step 2: Run the check, plain and behind a path base**

Run: `scripts/demo.sh --check`
Expected: two URLs, then `demo: check passed`.

Run: `CODEATLAS_PUBLIC_BASE=https://code.example.com/absproxy scripts/demo.sh --check`
Expected: the app URL is `https://code.example.com/absproxy/5181/` and the viewer URL is `https://code.example.com/absproxy/4318/`, followed by `demo: check passed`.

- [ ] **Step 3: Stop leaves no listeners**

Run: `scripts/demo.sh --check && sleep 2 && ss -ltn | grep -E ':(4318|5180|5181) ' || echo "no listeners"`
Expected: `no listeners`.
If a port is still listening, the process-group cleanup does not cover that process. Fix `cleanup`, not the check.

- [ ] **Step 4: Commit**

```bash
git add scripts/demo.sh
git commit -m "feat(demo): one-command Create Order demo, also behind code-server's absproxy"
```

---

### Task 7: Release verification for the samples, and one command for everything

**Files:**
- Modify: `scripts/verify-release.sh`
- Create: `scripts/test-all.sh`

**Interfaces:**
- Consumes: the samples and the sample web app.
- Produces:
  - `scripts/verify-release.sh`: 7 fixture checks plus 11 sample checks.
  - `scripts/test-all.sh`.

- [ ] **Step 1: Add the sample checks**

In `scripts/verify-release.sh`, insert before `exit $failed`:
```bash
# The samples: Debug and Release published into separate folders from the same tree.
for sample in MinimalApiSample ControllerSample; do
  for configuration in Debug Release; do
    dotnet publish "$root/samples/$sample" -c "$configuration" -o "$work/$sample-$configuration" --nologo -v q > /dev/null
  done
  check "$sample Debug contains CodeAtlas.AspNetCore.dll"          "[ -f \"\$work/$sample-Debug/CodeAtlas.AspNetCore.dll\" ]"
  check "$sample Release has no CodeAtlas assemblies"               "! ls \"\$work/$sample-Release\" | grep -qi codeatlas"
  check "$sample Release deps.json never mentions CodeAtlas"         "! grep -q 'CodeAtlas\.' \"\$work/$sample-Release/$sample.deps.json\""
  check "$sample Release app assembly has no CodeAtlas reference"    "! grep -aq 'CodeAtlas.AspNetCore' \"\$work/$sample-Release/$sample.dll\""
done
check "ControllerSample Release keeps its own OpenTelemetry.dll"   '[ -f "$work/ControllerSample-Release/OpenTelemetry.dll" ]'

# The sample web app's production build.
(cd "$root/src/web" && npm ci --silent && npm run build --silent) > /dev/null
(cd "$root/samples/MinimalApiSample.Web" && npm ci --silent && npx vite build --outDir "$work/web" --emptyOutDir --logLevel silent) > /dev/null
check "sample web build contains the app"                          'grep -rq "Create Order" "$work/web/assets"'
check "sample web build has no CodeAtlas runtime or collector path" '! grep -rqE "traceparent|codeatlas-web|interaction: |codeatlas/v1" "$work/web/assets"'
```

Run: `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH; scripts/verify-release.sh`
Expected: 18 lines, all `PASS`.

Prove the web check can fail: temporarily change `if (import.meta.env.DEV)` in `samples/MinimalApiSample.Web/src/main.tsx` to `if (true)` and run again.
Expected: `FAIL  sample web build has no CodeAtlas runtime or collector path`. Revert and run again: all `PASS`.

- [ ] **Step 2: One command for every check**

`scripts/test-all.sh`:
```bash
#!/usr/bin/env bash
# Every automated check, in one command. Needs .NET 10, Node and Docker (Postgres for the samples and Testcontainers).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
step() { printf '\n== %s\n' "$1"; }

step "@codeatlas/web";        (cd src/web && npm ci --silent && npx vitest run && npx tsc -p tsconfig.json --noEmit && npm run build --silent)
step "viewer";                (cd src/viewer && npm ci --silent && npx vitest run && npx tsc -p tsconfig.json --noEmit)
step "sample web app";        (cd samples/MinimalApiSample.Web && npm ci --silent && npx vitest run && npx tsc -p tsconfig.json --noEmit)
step ".NET tests";            dotnet test
step "release builds";        scripts/verify-release.sh
step "packed tool";           scripts/verify-tool.sh
step "demo starts and stops"; COLLECTOR_PORT=24318 API_PORT=25180 WEB_PORT=25181 DB_PORT=55435 scripts/demo.sh --check
step "end to end";            tests/e2e/run.sh
printf '\nAll checks passed.\n'
```
Run `chmod +x scripts/test-all.sh`.

Run: `scripts/test-all.sh 2>&1 | tail -3`
Expected: `All checks passed.`

- [ ] **Step 3: Commit**

```bash
git add scripts/verify-release.sh scripts/test-all.sh
git commit -m "build: release verification for the samples; test-all.sh runs every check"
```

---

### Task 8: Documentation

**Files:**
- Create: `README.md`, `docs/manual-checks.md`
- Modify: `docs/integration.md`, `docs/limitations.md`, `docs/superpowers/plans/README.md`

**Interfaces:**
- Consumes: every script, flag and file name above. The docs must only name things that exist.
- Produces: the user-facing documentation.

- [ ] **Step 1: Write the README**

`README.md`:
````markdown
# CodeAtlas Live

Click something in your React app and see what your ASP.NET Core backend actually executed — then open the exact
source of any step. Everything shown comes from recorded spans; what was not recorded is shown as unknown, never guessed.

CodeAtlas Live is a local development tool. Release builds of your app contain none of it.

## See it in 2 minutes

Needs .NET 10, Node 24 and Docker (Docker only runs the sample's Postgres).

```bash
scripts/demo.sh
```

Open the two printed URLs side by side, click **Create Order** in the app, and watch the viewer: the click, the request,
the endpoint, the handler, and its two database operations. Select the handler to see its source; **Open in
code-server** jumps to it. **Create Order (out of stock)** shows a failing request with its exception.

Working in code-server from another machine? Start it from a code-server terminal with your code-server address:

```bash
CODEATLAS_PUBLIC_BASE=https://code.example.com/absproxy scripts/demo.sh
```

## Use it in your own app

1. `scripts/pack-local.sh` builds the packages into `artifacts/` (nothing is published).
2. Backend: reference `CodeAtlas.AspNetCore` in Debug and call `builder.AddCodeAtlas()` — see
   [docs/integration.md](docs/integration.md).
3. Frontend (optional): add `@codeatlas/web` and load its runtime in development — same guide.
4. Run the collector: `dotnet run --project src/CodeAtlas.Collector -- --source-root /path/to/your/app`
   (or install the `codeatlas` tool), and open <http://127.0.0.1:4318>.

## What it shows, and what it does not

- Recorded: the browser interaction (explicit or linked by timing), the HTTP request, the endpoint, the controller
  action or Minimal API handler, database calls (Npgsql, as "operation + tables", never SQL text), outgoing HTTP calls,
  and exceptions.
- Not recorded in this version: methods called inside a handler (services, repositories). Their database calls appear
  under the handler, and they appear in exception stack traces. See [docs/limitations.md](docs/limitations.md).

## Repository

| Path | What |
|---|---|
| `src/CodeAtlas.AspNetCore` | Backend integration (Debug only) |
| `src/web` | `@codeatlas/web`, browser integration |
| `src/CodeAtlas.Collector` | The collector and the viewer it serves (`codeatlas` dotnet tool) |
| `src/viewer` | The viewer (React) |
| `samples/` | `MinimalApiSample` (+ `.Web`), `ControllerSample` |
| `scripts/test-all.sh` | Every automated check |
| `docs/manual-checks.md` | Checks that need a person |
````

- [ ] **Step 2: Extend the integration guide**

Append to `docs/integration.md`:
````markdown

## Browser: `@codeatlas/web`

`scripts/pack-local.sh` writes `artifacts/npm/codeatlas-web-0.1.0.tgz`. Add it to your app:

```bash
npm install /path/to/codeatlas-live/artifacts/npm/codeatlas-web-0.1.0.tgz
```

Load the runtime in development only — production builds then contain none of it:

```ts
if (import.meta.env.DEV)
  void import('@codeatlas/web/runtime').then(m =>
    m.initCodeAtlas({ collectorUrl: `${import.meta.env.BASE_URL}codeatlas/v1/traces`, serviceName: 'my-app-web' }))
```

and let the dev server forward that path to the collector, so the browser never talks to another origin (this also
works behind code-server's `/absproxy/<port>/`):

```ts
// vite.config.ts → server.proxy
'/codeatlas': { target: 'http://127.0.0.1:4318', rewrite: url => url.slice('/codeatlas'.length) },
```

Link requests to a click:

```ts
import { trackInteraction, instrumentAxios } from '@codeatlas/web'

// Explicit: every request made through ix belongs to "Create order", across any number of awaits.
await trackInteraction('Create order', async ix => {
  await ix.fetch('/api/orders', { method: 'POST', body })
})

// Axios: instrument the instance once; pass ix.axiosConfig() on the requests that belong to the interaction.
instrumentAxios(api)
await trackInteraction('Create order', ix => api.post('/orders', body, ix.axiosConfig()))
```

Without `trackInteraction`, a request that starts within 1 s of a click or submit is linked to it **by timing**; the
viewer marks those links "heuristic". Labels come from markup (`data-codeatlas`, `aria-label`, button text), never
from what a user typed. Cross-origin requests get a `traceparent` only when allowed with `propagateTo`.

`samples/MinimalApiSample.Web` shows all three styles.

## The collector

```bash
dotnet run --project src/CodeAtlas.Collector -- --source-root /path/to/your/app
# or: dotnet tool install CodeAtlas.Collector --tool-path ~/.codeatlas --add-source artifacts/nuget && ~/.codeatlas/codeatlas --source-root …
```

| Flag | Default | Purpose |
|---|---|---|
| `--urls` | `http://127.0.0.1:4318` | Where it listens; loopback only (it refuses anything else). |
| `--source-root` | current folder | Folders whose source may be shown and opened (comma-separated). |
| `--public-url` | — | The address your browser uses when the collector sits behind a path proxy, e.g. `https://code.example.com/absproxy/4318/`. |
| `--allowed-origin` | `http://localhost:5173,http://127.0.0.1:5173` | Origins whose browser spans may be sent directly (not needed with the dev-server proxy above). |
| `--editor` | `code-server` | `code-server`, `vscode`, `rider` or `none`. |
| `--editor-command` | found on `PATH` | Full path to the editor's command-line launcher. |

- **Per-user .NET install** (`~/.dotnet`): the `codeatlas` tool needs `DOTNET_ROOT=$HOME/.dotnet` to find the runtime.
- **Windows:** prefer the editors' `.exe` launchers with `--editor-command` (e.g. `Code.exe`, `rider64.exe`); `.cmd`
  launchers run through `cmd.exe`, which interprets `&` and `%` in file names. Source-root matching is case-sensitive in
  this version (see `docs/limitations.md`).
- **"Open in code-server"** starts `code-server -r -g file:line` on the collector's machine; start the collector from a
  code-server terminal so it reaches your code-server window.
````

- [ ] **Step 3: The manual checklist**

`docs/manual-checks.md`:
```markdown
# Manual checks

Checks a person makes before calling a version done. Everything else runs in `scripts/test-all.sh`.

| # | Check | How | Status |
|---|---|---|---|
| 1 | **code-server: "Open in code-server" opens the file at the declaration line, in the window you are using** | From a code-server terminal: `CODEATLAS_PUBLIC_BASE=https://<your-code-server>/absproxy scripts/demo.sh`. Open both URLs, click **Create Order**, select the handler, click **Open in code-server**. The editor shows `samples/MinimalApiSample/Orders/OrderEndpoints.cs` with the cursor on `CreateOrder`'s declaration line. | **pending (the maintainer)** |
| 2 | The viewer reads well in light and dark | Switch the theme button through system → light → dark with a trace selected; nothing is unreadable or invisible. | pending (the maintainer) |
| 3 | The whole demo flow, by a person | Create Order, Create Order (out of stock), Create Order (Axios), Refresh products: each appears in the viewer as described in README "See it in 2 minutes". | pending (the maintainer) |
| 4 | VS Code desktop | Collector with `--editor vscode` on a machine with VS Code; "Open in VS Code" opens the file at the line. | not run (no desktop VS Code here) |
| 5 | Rider on Windows | Collector with `--editor rider --editor-command "C:\…\rider64.exe"`; "Open in Rider" opens the file at the line. | not run (no Windows here) |
```

- [ ] **Step 4: Update limitations and the plan index**

In `docs/limitations.md`, append to the "By design" section (the list or table right under `## By design`) these two items, written in the section's existing format:
- The samples' database spans need Docker for Postgres; CodeAtlas itself does not.
- The demo's Postgres container is removed when the demo stops (`--rm`); sample data does not persist between runs.

In `docs/superpowers/plans/README.md`:
- Set plan 4's status to `done`.
- Set plan 5's file cell to `` `2026-10-11-plan-5-samples-demo-e2e.md` `` and its status to `implemented (branch plan-5-samples-demo-e2e)`.

- [ ] **Step 5: Check that the docs name only things that exist**

Run:
```bash
for p in scripts/demo.sh scripts/pack-local.sh scripts/test-all.sh scripts/verify-release.sh docs/integration.md docs/limitations.md docs/manual-checks.md \
         samples/MinimalApiSample/Orders/OrderEndpoints.cs samples/MinimalApiSample.Web src/CodeAtlas.Collector src/viewer src/web; do
  [ -e "$p" ] || echo "missing: $p"
done
for flag in --urls --source-root --public-url --allowed-origin --editor --editor-command; do
  grep -q -- "\"$flag\"" src/CodeAtlas.Collector/CollectorApp.cs || [ "$flag" = --urls ] || echo "unknown flag: $flag"
done
echo docs-check-done
```
Expected: only `docs-check-done`.

- [ ] **Step 6: Commit**

```bash
git add README.md docs
git commit -m "docs: README, browser and collector integration, manual checklist"
```
