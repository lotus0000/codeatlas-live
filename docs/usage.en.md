# CodeAtlas Live usage guide

[繁體中文版本](usage.zh-TW.md)

## Private distribution and package locations

Source code is in the private repository `<owner>/codeatlas-live`. NuGet packages
and the npm tarball are attached to its
[v0.1.0 Release](https://github.com/<owner>/codeatlas-live/releases/tag/v0.1.0).
Sign in to a GitHub account with read access and expand **Assets**:

| Type | Release asset |
| --- | --- |
| NuGet backend integration | `CodeAtlas.AspNetCore.0.1.0.nupkg` |
| NuGet shared transport | `CodeAtlas.Otlp.0.1.0.nupkg` |
| NuGet collector/tool | `CodeAtlas.Collector.0.1.0.nupkg` |
| npm browser integration (`@codeatlas/web`) | `codeatlas-web-0.1.0.tgz` |

These are private Release downloads, not registry publications. They are not
published on nuget.org or npmjs.com, and no private package registry is configured.
Do not redistribute packages or traces publicly. Never put GitHub tokens in
project files, prompts, or documentation.

Install GitHub CLI, run `gh auth login`, and confirm repository access. Then:

```bash
gh repo clone <owner>/codeatlas-live
cd codeatlas-live
gh release download v0.1.0 --repo <owner>/codeatlas-live --pattern '*.nupkg' --dir artifacts/nuget
gh release download v0.1.0 --repo <owner>/codeatlas-live --pattern '*.tgz' --dir artifacts/npm
```

Packages are now in `artifacts/nuget/` and `artifacts/npm/` inside your checkout.
The project requires .NET 10 and Node 24. The demo additionally requires Docker.
Installing packages does not start any services automatically.

## Run the demo first

From the CodeAtlas repository:

```bash
scripts/demo.sh
```

Open the application and viewer URLs printed in the terminal. Click **Create
Order**, then inspect the interaction, HTTP request, endpoint/handler, and database
operations in the viewer. Select the handler to inspect its source; **Open in
code-server** opens the file. Try the out-of-stock button to inspect a failed
request. Press Ctrl+C to stop the demo.

For remote code-server access:

```bash
CODEATLAS_PUBLIC_BASE=https://code.example.com/absproxy scripts/demo.sh
```

Replace the example hostname with your code-server address. Keep authentication
enabled; do not expose the collector publicly.

## Integrate into your application

Follow [integration.md](integration.md) for complete configuration examples:

1. Add the absolute path of `artifacts/nuget` to your existing `nuget.config`.
   Preserve existing package sources.
2. Reference `CodeAtlas.AspNetCore` version `0.1.0` only in Debug and define the
   `CODEATLAS` compilation symbol.
3. Import its namespace and call `builder.AddCodeAtlas()` inside `#if CODEATLAS`.
4. Optionally enable Minimal API handler spans. Preserve permissions, validation,
   and existing tracing behavior.
5. Install the browser tarball from your application's frontend directory:

   ```bash
   npm install /absolute/path/to/codeatlas-live/artifacts/npm/codeatlas-web-0.1.0.tgz
   ```

   Initialize it only in development. Proxy only the base-aware
   `codeatlas/v1/traces` path, never collector management APIs.
6. Start the collector. Set `--source-root` to the observed application's source,
   not the CodeAtlas repository.

From the CodeAtlas repository, install and start the packaged tool:

```bash
dotnet tool install CodeAtlas.Collector --version 0.1.0 --tool-path ./artifacts/tool --add-source ./artifacts/nuget
./artifacts/tool/codeatlas --source-root /absolute/path/to/your-app
```

The default viewer address is `http://127.0.0.1:4318/`. If the tool is already
installed, use it or run `dotnet tool update` when needed instead of reinstalling.

Alternatively, run the collector from source:

```bash
dotnet run --project src/CodeAtlas.Collector -- --source-root /absolute/path/to/your-app
```

## Existing example-app integration

example-app uses a local ProjectReference; do not replace it with package
references unnecessarily. First run `scripts/pack-local.sh` in CodeAtlas to build
the required assets, then run this command from the example-app repository:

```bash
CODEATLAS_ROOT=/absolute/path/to/codeatlas-live ./script/dev-codeatlas.sh
```

The launcher manages the collector, backend, and frontend. Use the printed URLs.
To disable integration, unset `CODEATLAS_ROOT` and restart the normal development
workflow. The existing Dashboard development configuration opts in explicitly;
check that configuration too if you start the project through Dashboard.

## Interpret results and verify integration

Only recorded spans are execution evidence. Routing metadata, timing-based
correlation, and stack traces are not a complete method execution tree. Mark
unrecorded steps as unknown. This version does not automatically record every
service or repository method called inside a handler.

SQL defaults to operation and table names, but exception messages, stack traces,
and custom tags can still contain sensitive information.

- Run the application's tests and a Release build; verify production excludes
  the CodeAtlas runtime.
- Click the actual UI and verify new traces, success/failure status, source-file
  opening, and code-server paths.
- Run `scripts/test-all.sh` in CodeAtlas for automated checks. See
  [manual-checks.md](manual-checks.md) for manual acceptance.
- If no traces appear, check collector startup, Debug/Development settings,
  proxy/base paths, and the application's OpenTelemetry sampling before assuming
  the code did not execute.

## Maintain private releases

Run `scripts/pack-local.sh`, then upload the `.nupkg` and `.tgz` files to a Release
in the private repository. For a new version, synchronize package versions,
documentation, and verification before creating a new tag. Do not overwrite
published versions.

Do not use `dotnet nuget push` or `npm publish` for this distribution workflow.
Copy-ready AI instructions are in [ai-prompts.en.md](ai-prompts.en.md).
