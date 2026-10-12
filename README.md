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
the endpoint, the handler, and its three database operations (`SELECT`, `UPDATE`, `INSERT`). Select the handler to see its source; **Open in
code-server** jumps to it. **Create Order (out of stock)** shows a failing request with its exception.

Working in code-server from another machine? Start it from a code-server terminal with your code-server address:

```bash
CODEATLAS_PUBLIC_BASE=https://code.example.com/absproxy scripts/demo.sh
```

## Use it in your own app

Private distribution: see [使用教學](docs/usage.zh-TW.md) for installation and
[AI prompts](docs/ai-prompts.zh-TW.md) for integration and evidence-based debugging.
Source and packaged binaries are distributed through a private GitHub repository and its Releases;
no packages are published to public NuGet or npm registries.

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
