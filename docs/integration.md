# Integrating CodeAtlas Live into an ASP.NET Core app

CodeAtlas runs only in Debug builds and, by default, only in Development. Release builds contain none of it
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
| `EnableOutsideDevelopment` | `false` | `CodeAtlas:Enabled` (`true` turns it on anywhere, `false` turns it off even in Development) |
| `ServiceName` | application name | — |
| `KeepDbStatements` | `false` (SQL replaced by "operation + tables") | — |
| `WrapperSpansBesideAppTracing` | `false` | — |
| `QueueCapacity` / `ExportTimeout` | 2048 / 2 s | — |

## Invalid settings

Settings never stop your app:

| Setting | When invalid |
|---|---|
| `CodeAtlas:Enabled` not `true`/`false` | ignored (warning); the Development default applies |
| `Endpoint` not an absolute `http(s)` URL | CodeAtlas disables itself (warning); the app runs normally |
| `QueueCapacity` < 1, `ExportTimeout` ≤ 0 | replaced by 2048 / 2 s (warning) |
| `AddCodeAtlas()` called more than once | only the first call counts (warning); no duplicate spans |

Warnings are logged once at start-up under the `CodeAtlas` prefix.

## If your app already uses OpenTelemetry tracing

CodeAtlas is then a pure observer: it follows **your** sampling. It shows only the requests your sampler records, and
never changes your sampler's decisions, exporters, enrichment callbacks, processors, the activities they see, or the
`traceparent` sent downstream. Its own export traffic never shows up in your traces or HTTP client metrics.
A warning at start-up says so, and the viewer shows a banner. To see every request in Development, sample everything
there (for example `AlwaysOnSampler` in `appsettings.Development.json`-driven configuration).

Endpoint/action/handler spans would become parents your own tool never receives, so they are **off** in that case.
`WrapperSpansBesideAppTracing = true` turns them on (inside sampled requests only) and accepts those broken parent
links in your own tool. You always get server, database and HttpClient spans.

## Privacy

Never sent: SQL text and parameters (SQL becomes "operation + tables"; if the text cannot be read reliably, only the
operation), connection strings, URL query strings, request/response bodies, header values
(including `user_agent.original` and any `http.*.header.*` tags), client addresses, `enduser.*` tags and method
arguments. Sent: exception messages and stack traces (Development only). If your own instrumentation adds other tags
to request activities (for example through an enrichment callback), those tags are sent too.

## If the collector is not running

Nothing happens to your app: spans go to a bounded background queue and are dropped when it is full.

## Known behavior in Development

The framework's developer exception page handles exceptions inside the pipeline, so a failing request's server span is
an error with status 500, and the exception itself is recorded on the endpoint, action or handler span.

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
// The type-only import lets TypeScript accept ix.axiosConfig() as an Axios config.
import type {} from '@codeatlas/web/axios'
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
