# CodeAtlas Live — implementation plans

Spec: `../specs/2026-10-10-codeatlas-live-mvp-design.md`. The MVP is split into five plans; each one ends with
software that builds and passes its own tests. A plan is written in full only when the previous one is done, so that
what the earlier plan taught us (especially the Plan 1 gate) shapes the next.

| # | Plan | Delivers | Depends on | Status |
|---|---|---|---|---|
| 1 | `2026-10-10-plan-1-backend-integration.md` | Repo skeleton, `CodeAtlas.Otlp`, `CodeAtlas.AspNetCore` (own tracer provider, redacting exporter, endpoint/action/handler spans, Npgsql/HttpClient privacy), coexistence gate, backend release verification | — | done |
| 2 | `2026-10-11-plan-2-collector.md` | `CodeAtlas.Collector`: OTLP ingest (proto + JSON), bounded store, SignalR, PDB + Roslyn source mapping with fixtures and regression, guarded editor launcher, host filtering, session token, Explain generator, `codeatlas` dotnet tool | 1 (`CodeAtlas.Otlp`) | done |
| 3 | `2026-10-11-plan-3-browser-package.md` | `@codeatlas/web` (pass-through API + `runtime` entry), explicit/heuristic correlation, Axios, label privacy, Vitest suite, production-bundle check | collector URL contract from 2 | done |
| 4 | `2026-10-11-plan-4-viewer.md` | React + shadcn/ui + Monaco viewer: activity list, execution tree with evidence kinds and gaps, inspector, source panel, open in IDE, Explain tab, live follow; served by the collector | 2 | implemented (branch `plan-4-viewer`) |
| 5 | Samples, demo, E2E, release | `MinimalApiSample` (+ web) "Create Order" demo, `ControllerSample`, `scripts/demo.sh`, Playwright E2E (demo, error, offline Monaco, open via recorder editor), full `verify-release.sh`, `test-all.sh`, `docs/integration.md`, `docs/limitations.md`, `docs/manual-checks.md` | 1–4 | outline |

## Outlines for plans 2–5 (expanded into full plans later)

**Plan 2 — Collector.** Tasks: (1) project + `Ingest` (`POST /v1/traces`, gzip, both encodings, response encoding,
400 on malformed) with real payload fixtures captured from the .NET exporter and the JS exporter; (2) `TraceStore`
(upsert, 500 traces / 2,000 spans / 4 KB attributes, eviction, string timestamps) + SignalR `/live`;
(3) `Source/PdbSourceMap` + Roslyn declaration widening, navigation line = declaration start, with
`CodeAtlas.SourceMap.Fixtures` compiled in-test (ordinary, async, iterator, overloads, generic, lambda, local function,
primary constructor, top-level, generated) and refusal cases (stale source, rebuilt assembly, wrong PDB, outside roots,
symlink escape); (4) `Editor/EditorLauncher` (code-server, VS Code, Rider; argument list; recorder-editor tests) and
`/api/open` (session token, Origin, host filtering, JSON only, identity only); (5) `Explain` generator + evidence
classification on fixture traces; (6) loopback-only start-up, configuration, `codeatlas` tool packaging.

**Plan 3 — Browser package.** Tasks: (1) package skeleton with two entry points and the pass-through implementation;
(2) runtime: provider, OTLP/JSON exporter, `initCodeAtlas`; (3) explicit correlation (fetch, Axios) with interleaved
concurrency tests; (4) heuristic listener, labels, duplicate suppression, propagation allow-list; (5) privacy tests and a
production-bundle test (Vite build of a fixture app contains no OpenTelemetry and no collector URL).

**Plan 4 — Viewer.** Tasks: (1) app shell (Vite, Tailwind, shadcn/ui), SignalR client, span merge; (2) tree model
(rebuild, gaps, evidence kinds) with Vitest; (3) activity list + tree UI; (4) inspector + Monaco source panel + open in
IDE + copy path; (5) Explain tab; (6) build into the collector's `wwwroot`.

**Plan 5 — Samples, demo, E2E, release.** Tasks: (1) `MinimalApiSample` + web app with Create Order and an
out-of-stock failure; (2) `ControllerSample`; (3) `demo.sh`; (4) Playwright E2E suite; (5) `verify-release.sh` covering
both samples' backend and frontend, plus a sample with its own OpenTelemetry; (6) docs and manual checklist.
