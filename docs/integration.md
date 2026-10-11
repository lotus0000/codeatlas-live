# Integrating CodeAtlas Live into an ASP.NET Core app

CodeAtlas runs only in Debug builds and only in Development. Release builds contain none of it
(`scripts/verify-release.sh` proves this on every change).

## 1. Make the packages available

```bash
# in the codeatlas-live repo
scripts/pack-local.sh
```

Add a `nuget.config` next to your solution (or extend yours):

```xml
<configuration>
  <packageSources>
    <add key="codeatlas-local" value="/path/to/codeatlas-live/artifacts/nuget" />
  </packageSources>
</configuration>
```

## 2. Reference it in Debug only

```xml
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="CodeAtlas.AspNetCore" Version="0.1.0" />
</ItemGroup>
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <DefineConstants>$(DefineConstants);CODEATLAS</DefineConstants>
</PropertyGroup>
```

## 3. Turn it on

```csharp
#if CODEATLAS
using CodeAtlas.AspNetCore;
#endif

// after your own service registrations
#if CODEATLAS
builder.AddCodeAtlas();
#endif
```

## 4. Optional: handler spans for Minimal APIs

Controllers get an action span automatically. Minimal APIs get an endpoint span automatically; for a span around the
handler itself add one line after the endpoint's own filters:

```csharp
group.MapPost("/", Create).WithValidation<ProductRequest>()
#if CODEATLAS
    .WithCodeAtlasHandlerSpan()
#endif
    .RequirePermission(ProductPermissions.Create);
```

## What you get

| Span | Code change | Evidence |
|---|---|---|
| `POST /api/products/` (server) | none | executed |
| `endpoint …` (binding + filters + handler + result) | none | executed; its method is *routing metadata* |
| `action Controller.Method` | none | executed |
| `handler Type.Method` | the optional line above | executed |
| `SELECT products` (Npgsql) | none | executed |
| `GET https://…` (HttpClient) | none | executed |

## Options

| Option | Default | Config key |
|---|---|---|
| `Endpoint` | `http://127.0.0.1:4318/v1/traces` | `CodeAtlas:Endpoint` |
| `EnableOutsideDevelopment` | `false` | `CodeAtlas:Enabled` |
| `ServiceName` | application name | — |
| `KeepDbStatements` | `false` (SQL replaced by "operation + tables") | — |
| `WrapperSpansBesideAppTracing` | `false` | — |
| `QueueCapacity` / `ExportTimeout` | 2048 / 2 s | — |

## If your app already uses OpenTelemetry tracing

CodeAtlas runs its own private tracer provider and never changes your sampler, exporters, instrumentation or the data
they send, and its own export traffic never shows up in your HTTP client metrics. Because its endpoint/action/handler spans would become parents your own tool never receives, they are
**off** in that case (a warning is logged once). You still get server, database and HttpClient spans.

## Privacy

Never sent: SQL text and parameters, connection strings, URL query strings, request/response bodies, header values
(including `user_agent.original` and any `http.*.header.*` tags), client addresses, `enduser.*` tags and method
arguments. Sent: exception messages and stack traces (Development only). If your own instrumentation adds other tags
to request activities (for example through an enrichment callback), those tags are sent too.

## If the collector is not running

Nothing happens to your app: spans go to a bounded background queue and are dropped when it is full.

## Known behavior in Development

The framework's developer exception page handles exceptions inside the pipeline, so a failing request's server span is
an error with status 500, and the exception itself is recorded on the endpoint, action or handler span.
