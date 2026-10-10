# CodeAtlas Live — MVP specification

Status: **draft, awaiting the maintainer approval.** No product code is written until this is approved.
Date: 2026-10-10. Evidence for every "confirmed" claim below: `<local-proof-of-concept>/POC-RESULTS.md`.

## 1. Goal

A developer clicks something in an existing React app and immediately sees what the ASP.NET Core backend actually
executed, then opens the exact source of any executed step.

```
React click → actual runtime trace → execution tree → click node → correct source (Monaco) → open in IDE
```

CodeAtlas Live is a local development tool. It is not an APM, not a trace store, and not a static analyzer.
(The static analyzer proposal in `<local-static-analysis-project>/` is a separate, unrelated project.)

## 2. Principles

1. **Evidence only.** Every node, edge and sentence comes from a captured span. Nothing is inferred into existence.
2. **Four evidence kinds are always visually distinct** (§7.2): executed, routing metadata, heuristic correlation, gap.
3. **Refuse rather than guess.** Source mapping, correlation and editor navigation say why they cannot answer.
4. **Debug-only footprint.** Release builds of the target app contain no CodeAtlas code, packages or bundles.
5. **Never interfere with the app.** No changed business behavior, no changed responses, no blocking on the collector,
   no modification of the app's own OpenTelemetry configuration or data.
6. **Standard wire format.** OTLP/HTTP with the official `opentelemetry-proto` schema; no custom tracing protocol.

## 3. Scope

### In the MVP

| Area | Included |
|---|---|
| Backend capture | ASP.NET Core server span; zero-change endpoint span (Minimal APIs + controllers); zero-change MVC action span; opt-in Minimal API handler span; outgoing `HttpClient`; Npgsql database spans with privacy-safe summaries; exceptions |
| Browser capture | Explicit interaction API (`trackInteraction`) for fetch and Axios; heuristic click/submit correlation; W3C `traceparent` propagation to allow-listed origins |
| Collector | One process: OTLP/HTTP receiver (protobuf + JSON), bounded in-memory store, SignalR live push, PDB + Roslyn source mapping, guarded editor launcher, serves the viewer |
| Viewer | Recent activity, execution tree, span inspector, Monaco source view, Explain Execution, evidence legend |
| Editors | code-server (primary), VS Code CLI and Rider CLI (optional) |
| Samples | Minimal API + React "Create Order" demo; controller-based sample |
| Quality | Automated unit, integration, security, source-mapping, correlation, end-to-end and release-build tests |

### Explicitly excluded

SQLite or any persistence · Jaeger / Aspire / external collectors · DI decorators and DispatchProxy ·
automatic tracing of arbitrary methods · MediatR behaviors · BackgroundService tracing · remote / non-loopback access ·
SQL Server, SQLite and EF Core-specific instrumentation · LLM features · publishing packages to public registries.

## 4. Architecture

```
┌ Target React app (dev server) ┐         ┌ Target ASP.NET Core app (Debug) ┐
│ @codeatlas/web                │ fetch + │ CodeAtlas.AspNetCore            │
│  · interaction spans          │traceparent  · own TracerProvider         │
│  · OTLP/JSON exporter ────────┼────┐    │  · redacting OTLP exporter ─────┼──┐
└───────────────────────────────┘    │    └─────────────────────────────────┘  │
                                     ▼                                         ▼
                 ┌ CodeAtlas Collector (one process, 127.0.0.1:4318) ──────────────────┐
                 │ /v1/traces (OTLP)  → TraceStore (ring buffer) → SignalR /live        │
                 │ /api/source (PDB + Roslyn)   /api/open (editor launcher)   viewer UI │
                 └──────────────────────────────────────────────────────────────────────┘
                                     ▲
                       CodeAtlas Live Viewer (browser window B)
```

### 4.1 Repository layout (`codeatlas-live`)

```
src/
  CodeAtlas.AspNetCore/      NuGet, referenced by target apps in Debug only
  CodeAtlas.Otlp/            Generated opentelemetry-proto classes + OTLP JSON reader (shared by the two below)
  CodeAtlas.Collector/       The collector process; packed as the dotnet tool `codeatlas`
    Ingest/  Store/  Live/  Source/  Editor/  Explain/  Api/  wwwroot/ (built viewer)
  viewer/                    React + TS + Vite + Tailwind + shadcn/ui + Monaco
  web/                       npm package `@codeatlas/web` (browser integration)
samples/
  MinimalApiSample/  + MinimalApiSample.Web/   (the "Create Order" demo)
  ControllerSample/
tests/
  CodeAtlas.AspNetCore.Tests/   CodeAtlas.Collector.Tests/   CodeAtlas.SourceMap.Fixtures/
  e2e/                          (Playwright)
scripts/
  verify-release.sh   pack-local.sh   demo.sh
docs/
  integration.md  limitations.md  superpowers/specs/  superpowers/plans/
```

Each `src/` unit has one job and is testable alone. The collector does not reference `CodeAtlas.AspNetCore`.

### 4.2 Distribution

- `scripts/pack-local.sh` builds `CodeAtlas.AspNetCore` and the `codeatlas` tool into `./artifacts/nuget/`,
  and `@codeatlas/web` into `./artifacts/npm/*.tgz`. Target apps point a `nuget.config` source and a `file:` npm
  dependency at these. Nothing is published.
- The collector runs as `codeatlas` (dotnet tool) or `dotnet run --project src/CodeAtlas.Collector`.

## 5. Backend integration — `CodeAtlas.AspNetCore`

### 5.1 Required changes in a target app

```xml
<!-- App.csproj -->
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="CodeAtlas.AspNetCore" Version="0.1.*" />
</ItemGroup>
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <DefineConstants>$(DefineConstants);CODEATLAS</DefineConstants>
</PropertyGroup>
```

```csharp
// Program.cs, after the app's own service registrations
#if CODEATLAS
builder.AddCodeAtlas();
#endif
```

Runtime guard as well: `AddCodeAtlas` is a no-op unless the environment is Development or `CodeAtlas:Enabled=true`.

### 5.2 Spans and their evidence kind

| Span | Mechanism | Code change | Evidence kind | Covers |
|---|---|---|---|---|
| `POST /api/orders` server span | ASP.NET Core's own `Microsoft.AspNetCore` activity | none | executed | whole request incl. middleware |
| `endpoint <display name>` | `MatcherPolicy` swaps the matched endpoint for a wrapped copy | none | executed (the endpoint delegate ran) | binding + filters + handler + result |
| — its handler method identity | endpoint metadata | none | **routing metadata** (the handler may not have run, e.g. validation 400) | — |
| `action Controller.Method` | global MVC `IAsyncActionFilter`, `Order = int.MaxValue` | none | executed | the action method |
| `handler Type.Method` | endpoint filter `.WithCodeAtlasHandlerSpan()` | one line per endpoint or group | executed | after `WithValidation`: handler only; on a group: filters + handler |
| `SELECT products` | Npgsql's own `ActivitySource` | none | executed | one command; name = operation + tables |
| `GET https://…` | `System.Net.Http` activity | none | executed | outgoing call |

Confirmed in the PoC: all rows on example-app (Minimal API) and the controller sample; validation failure yields an
endpoint span with no handler span; example-app's 419 tests pass with this active.

`.WithCodeAtlasHandlerSpan()` is the only optional code change and must be wrapped in `#if CODEATLAS`.

### 5.3 Method identity

Every CodeAtlas span that refers to a method carries:

```
codeatlas.code.function        Namespace.Type.Method          (display only)
codeatlas.code.mvid            module version id              (build identity)
codeatlas.code.token           metadata token                 (method identity)
codeatlas.code.assembly_path   loaded assembly location
codeatlas.code.evidence        executed | routing
```

Names are never used to look up source; MVID + token are.

### 5.4 Coexistence with the app's own OpenTelemetry (not verified by the PoC — plan task 1)

The PoC added CodeAtlas to the app's tracing pipeline and stripped SQL by mutating `Activity` tags. That would change
what an app's own exporter sends. The MVP design avoids this:

- CodeAtlas builds **its own `TracerProvider`** (`Sdk.CreateTracerProviderBuilder()`), owned by a hosted service.
  It never calls `AddOpenTelemetry()` on the app's builder, never configures the app's instrumentation options,
  processors, exporters or sampler.
- CodeAtlas **never mutates an `Activity`.** Redaction, SQL summaries and renaming happen while serializing into OTLP
  protobuf inside `CodeAtlasExporter : BaseExporter<Activity>` (official proto classes from `CodeAtlas.Otlp`).
- CodeAtlas's sampler returns **record-only** (`AllData`, not `Recorded`). When the app has its own sampler, the
  app's decision still controls the `sampled` flag and its exporters (which only export `Recorded` activities);
  when it has none, CodeAtlas still sees every request.

- **Parent links (found while planning).** CodeAtlas's own spans (endpoint, action, handler) become the parent of
  the database and HTTP spans created inside them. An app that has its own tracing would then export those child
  spans with a parent its backend never receives (broken trees in the app's own tool). Therefore:
  **when the app has its own tracing** (a `TracerProvider` is registered before `AddCodeAtlas`), CodeAtlas does not
  create endpoint/action/handler spans; it shows the server, database and HTTP spans only, logs a warning once,
  and the viewer shows a banner explaining why. `WrapperSpansBesideAppTracing = true` opts back in and accepts the
  broken parent links in the app's own tool (Development only). Apps without their own tracing (e.g. example-app)
  get the full tree.
- **HTTP facts without mutation.** CodeAtlas does not add ASP.NET Core instrumentation to any provider (that would
  attach exception events and enrichment to shared activities). A first-in-pipeline middleware records method, route
  and status code in a side table keyed by the request's `Activity` object (`ConditionalWeakTable`), and the
  serializer merges them.

Plan task 4 proves this with a test app that has its own tracing (in-memory exporter, 0 % and 100 % samplers):
the app's exported spans must be identical with and without CodeAtlas (count, names, attributes, sampled flags,
including when the app customizes its own ASP.NET Core instrumentation options),
and CodeAtlas must receive every request. **If any assertion fails, implementation stops and the maintainer decides** between
"separate provider with always-on sampling (documented side effect)" and "CodeAtlas refuses to run beside existing tracing".

### 5.5 Privacy (backend)

Applied in `CodeAtlasExporter` serialization, on by default:

| Data | Treatment |
|---|---|
| `db.statement`, `db.query.text` | replaced by `db.query.summary` = operation + table names (e.g. `INSERT audit_logs products`); span renamed to it |
| `db.npgsql.data_source`, `db.connection_string` | dropped |
| `url.query`, `url.full` query part | dropped |
| request / response bodies, headers | never captured |
| method arguments | never captured |
| `exception.message`, `exception.stacktrace` | **kept** (needed for debugging; Development only) — documented |

`KeepDbStatements = true` keeps SQL text (opt-in, never parameters).

### 5.6 Failure isolation

- Export is batched on a background thread with a bounded queue (2,048 spans); overflow drops the oldest.
- HTTP timeout 2 s; failures are counted and logged at Debug level once per minute, never thrown.
- No CodeAtlas code runs on the request path except span start/stop and the endpoint wrapper.
- Confirmed in the PoC: collector offline → requests unchanged (41 ms vs 41 ms), 419/419 tests pass.

## 6. Browser integration — `@codeatlas/web`

### 6.1 Two entry points

| Import | Contents | In production bundle? |
|---|---|---|
| `@codeatlas/web` | `trackInteraction`, `instrumentAxios`, types. No OpenTelemetry imports. Without the runtime it is a pass-through: `ix.fetch === window.fetch`, `ix.axiosConfig()` returns `{}`. | only if the app calls the explicit API (< 1 KB, no network) |
| `@codeatlas/web/runtime` | OpenTelemetry Web SDK, OTLP/JSON exporter, heuristic listeners, `initCodeAtlas` | **never** |

Required change in a target app:

```ts
// main.tsx
if (import.meta.env.DEV)
  void import('@codeatlas/web/runtime').then(m => m.initCodeAtlas({ collectorUrl: 'http://127.0.0.1:4318/v1/traces', serviceName: 'my-web' }))
```

The OpenTelemetry packages are `dependencies` of `@codeatlas/web` and are reached only through the dynamic import.
Confirmed in the PoC: a static import leaked into the production bundle; the dynamic import removed it completely.

### 6.2 Correlation modes

| Mode | How | Label | Guarantee |
|---|---|---|---|
| explicit | `trackInteraction('Create order', async ix => ix.fetch(...))`; Axios via `api.post(url, body, ix.axiosConfig())` | `explicit` | exact: the interaction travels with the call, across any number of awaits and concurrent interactions (confirmed) |
| heuristic | capture-phase `click`/`submit` listener; a request started within `windowMs` (default 1000) becomes its child | `heuristic` | none. Follow-up requests (e.g. a list reload after Save) are also attached; shown as heuristic, never as confirmed |

- An explicit interaction started during the same click cancels the heuristic one (no duplicates; confirmed).
- A heuristic interaction becomes a span only if a request follows it.
- Requests that already carry `traceparent` are left alone.

### 6.3 Labels and privacy (browser)

- Label order: `data-codeatlas` → `aria-label` → submit button text (`SubmitEvent.submitter`) → button/link text
  (trimmed, 40 chars) → form `name`/`id`. Text labels can be switched off (`textLabels: false`).
- Never read: input values, `FormData`, request/response bodies, query strings, cookies, storage.
- `traceparent` is added only to same-origin URLs and `propagateTo` patterns, so third-party APIs see no new header and
  no new CORS preflight.

## 7. Collector

### 7.1 Ingestion and store

- `POST /v1/traces`: `application/x-protobuf` (official proto) and `application/json` (OTLP JSON: hex ids converted
  before `JsonParser`), optional gzip. Returns `ExportTraceServiceResponse` in the request's encoding. Malformed → 400.
- `TraceStore`: upsert by span id; ring buffer of 500 traces, 2,000 spans per trace, attribute values cut at 4 KB.
  Out-of-order and late spans are normal; nothing assumes the parent arrives first.
- Timestamps leave the collector as **strings** (nanoseconds exceed JS number precision).
- SignalR hub `/live` pushes span batches; the viewer merges by span id and rebuilds trees.

### 7.2 Evidence classification (shared by tree, inspector and Explain)

| Kind | Rule | Display |
|---|---|---|
| executed | a span exists for it | solid node |
| routing metadata | `codeatlas.code.evidence = routing` (endpoint span's handler identity) | "routed to" chip on the node, never a node of its own |
| heuristic | `codeatlas.correlation = heuristic` on browser spans | dashed edge + "heuristic" badge |
| gap | computed (below) | amber note, worded as unknown, never as "nothing ran" |

Gap rules (from the PoC, wording fixed):
- uncovered time ≥ 1 ms in a span with children: "N ms not covered by any child span — unexplained by telemetry, not
  evidence that nothing ran";
- HTTP client span (has `http.request.method`) with no server child after 5 s: "No server span received — the backend may
  not be instrumented, the span may be sampled out, delayed or lost in export";
- span whose parent is absent: "Parent span not received yet";
- endpoint span with a routing handler but no handler/action child: "Handler execution not recorded (no handler span;
  the handler may not have run, or `WithCodeAtlasHandlerSpan` is not enabled)".

### 7.3 Source mapping (`/api/source`)

Input: `assembly`, `mvid`, `token` only. Steps (all confirmed in the PoC):

1. Assembly path must be inside a configured source root (symlinks resolved).
2. Read MVID from the assembly; mismatch → "rebuilt after this span".
3. Open the Portable PDB (embedded or side-by-side) with PDB-id validation; failure → "no matching PDB".
4. Sequence points of the method; for async/iterator methods, of the `MoveNext` whose kickoff is this method.
5. Document must be in a source root and exist; generated (`/obj/`) → "produced by a source generator".
6. Document checksum (SHA-256/SHA-1) must match the file → otherwise "source changed after build".
7. Roslyn (syntax only, cached per file hash) widens to the innermost function-like node containing every sequence
   point: method, constructor, accessor, local function, lambda, primary constructor, top-level statements.
8. Response: file, declaration range, **navigation line = declaration start line**, executable lines, kind, full text.

PoC accuracy: 651 / 655 user-written methods mapped, 4 correctly refused, 0 wrong; 17 / 17 runtime identities.

### 7.4 Editor launcher (`/api/open`)

- Input: method identity only. File and line come from §7.3.
- Editor from config: `code-server` (`-r -g file:line`, default), `vscode` (`code -r -g file:line`),
  `rider` (`rider --line N file`). Executable resolved once at start-up; `ProcessStartInfo.ArgumentList`, no shell.
- If the editor executable is missing, the viewer shows why and offers "copy path:line".

### 7.5 Security

| Threat | Control |
|---|---|
| Access from the network | binds `127.0.0.1` only; refuses to start on any other address in the MVP |
| DNS rebinding (malicious site resolving to 127.0.0.1) | host filtering: only `localhost`, `127.0.0.1` accepted in `Host` |
| Cross-site requests to `/api/open` | per-process session token (`GET /api/session`, no CORS) in `X-CodeAtlas-Token`; `Origin` must be the viewer origin; JSON only |
| Browser OTLP from any site | CORS on `/v1/traces` allows only configured app origins, `POST`, `content-type` |
| Arbitrary file read | source endpoints accept identities only; files must come from a matching PDB, inside a root, checksum-verified |
| Command injection | fixed executable, argument list, file name passed as one argument |
| Data leaving the machine | no outbound calls; Monaco and all assets bundled |

## 8. Viewer

- Layout: left **Recent activity** (time, interaction + correlation badge, method + route, status, duration, trace id);
  center **Execution tree** (hierarchical timeline, collapse/expand, duration bars, error and slow (≥ 200 ms, configurable)
  highlighting, parallel children shown overlapping); right **Inspector** (timings, ids, parent, service, scope,
  status, attributes, exception events, Source panel with Monaco, "Open in IDE", "copy path:line").
- Live: auto-follows the newest trace that has a server span; a pin keeps the current selection.
- **Explain Execution** tab, deterministic, every sentence linked to its spans:
  entry point (interaction name + correlation kind, HTTP method + route) → executed handlers/actions →
  database operations (count, summaries) → outgoing HTTP calls → slowest 3 spans → exceptions → visibility gaps.
  Routing metadata and heuristic links are phrased as such ("routed to", "likely triggered by").
- Built with Tailwind + shadcn/ui components (the maintainer preference); Monaco imported from the bundle (`loader.config`).

## 9. Demo (real, no simulated data)

`scripts/demo.sh` starts a Postgres container for the sample (`docker run`, port 55433; this is a dependency of the
sample app only, because database tracing is Npgsql-only — CodeAtlas itself needs no Docker), the collector,
`MinimalApiSample` and its React app, then prints two URLs.

1. Window A: sample app. Window B: viewer.
2. Click **Create Order** → tree appears: interaction (explicit) → fetch → server → endpoint → handler →
   `SELECT products`, `INSERT orders` → 201. Code the handler calls (e.g. a service class) has no span of its own;
   its database calls appear under the handler.
3. Click the handler node → Monaco at `OrderEndpoints.CreateOrder`; Open in IDE.
4. Click the DB node → summary, duration, no SQL text.
5. Click **Create Order (out of stock)** → the handler throws → HTTP 500, exception recorded on the handler span and
   shown in the inspector and in Explain Execution.

## 10. Testing

| Suite | Tool | Covers |
|---|---|---|
| `CodeAtlas.AspNetCore.Tests` | xUnit + WebApplicationFactory + Testcontainers Postgres + fake collector | span set for Minimal API and controllers; handler span placement; validation 400 → no handler span; method identity attributes; privacy (no SQL text, connection string, query string, bodies); collector offline (no exceptions, latency budget); **coexistence with app tracing** (§5.4); runtime guard outside Development |
| `CodeAtlas.Collector.Tests` | xUnit | OTLP protobuf + JSON + gzip parsing incl. captured real payloads from the .NET and JS exporters; malformed input; store bounds and eviction; out-of-order upsert; SignalR push; Explain generator on fixture traces; evidence classification |
| Source-mapping tests | xUnit + `CodeAtlas.SourceMap.Fixtures` (compiled in the test with a Portable PDB) | ordinary, async, iterator, overloads, generic, lambda, local function, primary constructor, top-level, generated code; stale source, rebuilt assembly, wrong PDB, outside roots, symlink escape; **regression: 100 % of user-written methods in the two samples map, 0 wrong** |
| Security tests | xUnit | `/api/open` token, origin, host header, content type, identity-only input; shell-syntax file name reaches the editor as one argument (recorder editor) and runs nothing; loopback-only start-up |
| `@codeatlas/web` tests | Vitest + jsdom | explicit correlation across awaits with interleaved concurrent interactions; Axios; heuristic window and duplicate suppression; labels never contain input values; no `traceparent` to non-allowed origins; pass-through mode equals plain fetch |
| Viewer tests | Vitest | tree rebuild with out-of-order spans; gap math; evidence rendering; Explain text |
| E2E | Playwright, real processes | demo flow in two windows; error flow; Monaco offline (network blocked); "Open in IDE" via recorder editor |
| Release verification | `scripts/verify-release.sh` (run in CI) | `dotnet publish -c Release` of both samples and of a sample that has its own OpenTelemetry: no `CodeAtlas*`, no CodeAtlas-only packages, app's own OTel kept; `vite build`: no OTel, no collector URL, no runtime code |
| Manual checklist | `docs/manual-checks.md` | code-server visual open (**pending the maintainer**), VS Code desktop and Rider on Windows |

All suites run from one command (`scripts/test-all.sh`).

## 11. Known limitations (documented in `docs/limitations.md`)

- Only methods that CodeAtlas wraps (endpoint, handler, action) or libraries instrument (ASP.NET Core, HttpClient,
  Npgsql) appear. Services and repositories called inside a handler are not separate spans in the MVP.
- Minimal APIs need one line per endpoint/group for a handler-only span.
- Heuristic correlation can over-attach follow-up requests.
- Database tracing is Npgsql-only in the MVP.
- Exception messages are captured and may contain data.
- An app that has its own tracing gets no endpoint/action/handler spans by default (§5.4).
- An app whose own sampler drops a request: see §5.4 outcome.
- Background work outside a request appears as separate root traces.

## 12. Extension points kept for later (not built)

- `ISpanSource` in the collector, so another backend (e.g. an Aspire telemetry API) could feed the viewer.
- Method identity attributes are generic: a future safe Service/Repository instrumentation (source generator or
  interceptors at compile time) only has to emit the same attributes.
- Editor launcher is a table of kinds; new editors are one entry.

## 13. Open items needing the maintainer

1. Approve this specification.
2. code-server visual open from the PoC: pending your manual check.
3. §5.4 default when the app has its own tracing: no wrapper spans (recommended), with an opt-in. Confirm or change.
4. §5.4 outcome, only if plan task 4 fails.
