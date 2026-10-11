# Known limitations

Backend integration (`CodeAtlas.AspNetCore`, Plan 1) and collector (`CodeAtlas.Collector`, Plan 2). Plan 5 extends
this file with browser and viewer limitations.

## By design

| Limitation | Why |
|---|---|
| Only endpoint, action and handler methods, plus ASP.NET Core, HttpClient and Npgsql operations, appear as spans. Services and repositories called inside a handler do not. | Automatic method tracing is out of the MVP (spec §3). |
| Minimal APIs need `.WithCodeAtlasHandlerSpan()` per endpoint or group for a handler-only span. | ASP.NET Core has no global hook between binding and the handler. |
| If the app has its own OpenTelemetry tracing, CodeAtlas shows only the requests the app's sampler records, and has no endpoint/action/handler spans unless opted in. | CodeAtlas must not change the app's tracing (spec §5.4, option B). |
| Tracing tools that are not an OpenTelemetry `TracerProvider` in DI (e.g. Application Insights classic, a raw `ActivityListener`) are not detected. Wrapper spans stay on, and those tools may see database/HTTP spans whose parent they never receive. | Detection is defined as "a `TracerProvider` is registered". |
| Exception messages and stack traces are exported. | Needed for debugging; Development only. |
| Tags that the app's own instrumentation adds (other than headers, client address and `enduser.*`) are exported. | CodeAtlas cannot know which custom tags are sensitive. |
| Database tracing is Npgsql only. | MVP scope. |
| In Development, an exception handled by the developer exception page gives a server span with status 500 but no "unhandled" flag; the exception is on the endpoint/action/handler span. | The page handles it inside the pipeline. |

## Deferred minor issues

Each was found in the Plan 1 final review. "Safe to defer" means no business behavior, privacy or Release build is
affected.

| Issue | Impact | Safe to defer? | When |
|---|---|---|---|
| Several web hosts in one process (e.g. an app's `WebApplicationFactory` tests in a Debug build) each export every span; copies made by a host that did not serve the request lack route and status. | Duplicate or less detailed spans in the viewer while such tests run with a collector open. The app and its tests are unaffected. | Yes, if the collector keeps the most detailed copy of a span. | Plan 2 (collector merge rule) |
| `CodeAtlas.AspNetCore` requires `OpenTelemetry >= 1.19.1`. An app on an older OpenTelemetry gets it upgraded in Debug builds only; an app that pins a lower version directly gets build error NU1605 in Debug. | Debug and Release may run different OpenTelemetry versions; some apps cannot build Debug with CodeAtlas. Release unaffected. | Yes for apps without OpenTelemetry or on ≥ 1.19.1 (e.g. example-app). **Not** for apps pinned below 1.19.1. | Before integrating such an app: lower the floor (CodeAtlas now only needs `BaseProcessor` and instrumentation suppression) and prove it with an older-version release fixture |
| An exception thrown after the response has started is recorded as status 500 on the server span, though the client already received another status. | Misleading status on rare streaming responses. | Yes | Later |
| The export processor's two cancellation token sources are not disposed. | None in practice (no timers attached). | Yes | Later |
| An app with logging fully disabled and no tracing: CodeAtlas's listener makes HttpClient send a `traceparent` header where none was sent before. | Downstream services see a trace header they did not see before; the sampled flag is unchanged. Apps with any logging provider are unaffected. | Yes (Development only, rare setup) | Later |

Fixed in Plan 1's final pass (no longer limitations): SQL with dollar-quoted or `E''` strings now yields the operation
only; `CodeAtlas:Enabled=false` works in Development; invalid settings no longer stop the app; repeated
`AddCodeAtlas()` calls are ignored; header values, client address and `enduser.*` tags are dropped; lazy tag values
are never enumerated; CodeAtlas's export traffic stays out of the app's traces and metrics; the downstream sampled flag
is unchanged; the host stop flushes queued spans.

## Collector: deferred minor issues (Plan 2 final review)

| Issue | Impact | Safe to defer? |
|---|---|---|
| `api/source` re-reads the file for display instead of returning the bytes it verified. | If the file is saved during a lookup, the text shown may not match the verified lines. | Yes (rare timing) |
| The PDB path recorded in an assembly is not checked against the source roots before it is read. | A crafted DLL already inside a root could make the collector read (not return) a PDB elsewhere, or hang on a FIFO. | Yes (needs a hostile file inside your own source root) |
| Source-root matching is case-sensitive. | On Windows/macOS a casing or drive-letter difference between the PDB path and `--source-root` gives a false "outside the roots" refusal. | Yes on Linux; **fix before Windows use** |
| Roslyn parses the file on every lookup (no per-hash cache). | Slower source panel on large files. | Yes |
| Deterministic builds (PathMap `/_/…`) are refused as "outside the source roots" without a hint. | Confusing message. | Yes |
| Port already in use (4318 is the standard OTLP port, e.g. Jaeger) ends with an exception dump instead of a hint to use `--urls`. | Confusing start-up failure. | Yes |
| After a non-zero editor exit, stderr is read without a timeout. | A grandchild process holding the pipe could hang that one request. | Yes |
| The store caps span count and string length, not span names, event counts or array lengths; limits ≤ 0 are not validated. | Memory use from unusual but local input. | Yes |
| The session token comparison is not constant-time. | Negligible on loopback. | Yes |
| On Windows, `code.cmd` / `rider.cmd` run through cmd.exe, which interprets `&` and `%` in arguments. | A source path containing those characters could be misparsed by cmd.exe. | Yes; prefer `.exe` editors on Windows (documented in Plan 5) |

## Browser package: deferred minor issues (Plan 3 final review)

| Issue | Impact | Safe to defer? |
|---|---|---|
| The patched `fetch` can throw synchronously (invalid header names, malformed URL right after a click) where native `fetch` returns a rejected promise. | A `fetch(...).catch(...)` chain sees an uncaught exception instead, only while CodeAtlas runs. | Yes (development only, unusual input) |
| On a form submit with a submitter, the form's own `data-codeatlas` / name / id is ignored. | An icon-only submit button gives the label `button` instead of the form's annotation. | Yes (label only) |
| Clicks on elements that are not buttons/links (e.g. a `<tr onClick>`) neither start nor clear a heuristic interaction. | A request from such a click within `windowMs` of a button click is attributed to that button. | Yes (heuristic, marked as such) |
| Relative URLs resolve against `location.href`, not `document.baseURI`. | With `<base href>`, the recorded path or allow-list decision can differ from the real request. | Yes (rare in SPAs) |
| An old handle's `shutdown()` after a re-init also unregisters the newer runtime; a second `shutdown()` shuts the provider down twice. | Tracing stops after unusual HMR/StrictMode sequences until reload. | Yes |
| `pack-local.sh` does not clean `src/web/dist` before building. | Files from renamed or removed sources could ship in the local tarball. | Yes (local feed only) |
| When the app has wrapped `fetch`, the runtime adds a hidden same-origin `<iframe>` to get a native fetch for OTLP exports. | One extra hidden element under `<html>` in development. | By design |

## Viewer: deferred minor issues (Plan 4 final review)

| Issue | Impact | Safe to defer? |
|---|---|---|
| Monaco's hover is not bundled, so the executable-line dot has no tooltip. | The blue gutter dot is unexplained. | Yes (cosmetic) |
| After a collector restart, old traces stay in the list until reload. | Clicking one says it was evicted. | Yes |
| The error banner is cleared only by a successful list reload; a failed trace load keeps "Loading trace…". | A stale error message can stay visible. | Yes |
| Switching away from an evicted trace briefly shows the "no longer in the collector" text. | A short flash of the wrong message. | Yes |
| "It ran inside …" picks the nearest executed ancestor even across a service boundary. | A downstream service's database span can name the caller's handler. | Yes (multi-service traces only) |
| The tree has no arrow-key navigation; every row is a tab stop. | Keyboard use of large traces is slow. | Yes |
| Selecting a span from Explain or the Parent link does not scroll it into view; switching tabs resets collapsed nodes. | The selection can be off-screen. | Yes |
| The `parent-cycle` gap kind has no short label in the tree. | The chip shows the raw kind (full wording in the tooltip). | Yes |
| A 0 ms heuristic bar renders as a small dashed glyph. | Cosmetic. | Yes |
