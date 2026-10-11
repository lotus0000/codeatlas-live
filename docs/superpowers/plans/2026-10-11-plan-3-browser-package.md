# Plan 3 — Browser package (`@codeatlas/web`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `@codeatlas/web`: a tiny pass-through API for app code (`trackInteraction`, `instrumentAxios`) and a development-only runtime (`@codeatlas/web/runtime`) that creates interaction and request spans with the official OpenTelemetry Web SDK, propagates W3C trace context to allowed origins only, and exports OTLP/JSON to the collector — never reading input values, bodies or query strings, and never reaching a production bundle.

**Architecture:** App code imports only `@codeatlas/web` (no OpenTelemetry). It looks up a runtime registered on `globalThis` under `Symbol.for('codeatlas.web.runtime')`; without one it behaves exactly like plain `fetch`/Axios. The app loads `@codeatlas/web/runtime` with a dynamic `import()` inside `if (import.meta.env.DEV)`, so production builds drop it. The runtime owns a `WebTracerProvider` (tracer name `codeatlas-web`, which the collector uses to recognise browser spans), patches `window.fetch` for heuristic correlation while exposing `fetch.__original` (the OpenTelemetry exporter's own convention for bypassing instrumentation), and never traces requests to the collector URL.

**Tech Stack:** TypeScript ~6.0.2, @opentelemetry/api 1.9.1, @opentelemetry/sdk-trace-web 2.12.0, @opentelemetry/sdk-trace-base 2.12.0, @opentelemetry/exporter-trace-otlp-http 0.223.0, @opentelemetry/resources 2.12.0, @opentelemetry/core 2.12.0; dev: vitest ^5.0.1, jsdom ^30.1.1, vite ^8.3.0, axios ^1.20.0, @opentelemetry/otlp-transformer 0.223.0.

**Spec:** `docs/superpowers/specs/2026-10-10-codeatlas-live-mvp-design.md` (§6, §10, §11)

## Global Constraints

- Package `@codeatlas/web` 0.1.0 at `src/web/`, ESM only. Entry points: `.` (pass-through API, **no OpenTelemetry imports**), `./runtime` (`initCodeAtlas`), `./axios` (types-only Axios config augmentation).
- Tracer name `codeatlas-web`. Span names: interaction `interaction: <label>`, request `<METHOD> <path>`.
- Attributes (exact): `codeatlas.interaction.name`, `codeatlas.interaction.label_source` (`data-codeatlas|aria-label|text|form-name|tag`), `codeatlas.interaction.event` (`click|submit`), `codeatlas.correlation` (`explicit|heuristic`), `http.request.method`, `url.path`, `http.response.status_code`.
- Never read or record: input/textarea/select values, `FormData`, request or response bodies, query strings or fragments, cookies, storage. Text labels come from button/link text or `input[type=submit|button]` values only, trimmed to 40 characters; `textLabels: false` turns them off.
- `traceparent` only for same-origin URLs and `propagateTo` entries (string prefix or RegExp). Requests that already carry `traceparent`, and requests to the collector URL, are left untouched.
- `collectorUrl` may be relative; the runtime resolves it against `location.href` (the OTLP exporter requires an absolute URL).
- Heuristic window default 1,000 ms. An explicit interaction started during the same event cancels the heuristic one. A heuristic interaction becomes a span only when a request follows it.
- The patched `window.fetch` exposes `__original` (the unpatched fetch).
- `initCodeAtlas` is idempotent; `shutdown()` restores `window.fetch`, removes listeners and unregisters the runtime.

## Review Focus

- Called from a click handler that throws before any request — the explicit interaction span must still end with an error status and the error must propagate unchanged → Task 2 test `An_error_thrown_by_the_callback_ends_the_interaction_and_propagates`.
- `fetch(new Request(url, { method: 'POST', body }))` (a `Request` object, not a URL) — method and headers must be taken from the request, and the body must still be sent → Task 2 test `Request_objects_keep_their_method_headers_and_body`.
- Axios instances with a `baseURL` and relative URLs — the allow-list check must use the resolved URL → Task 3 test `Relative_axios_urls_resolve_against_baseURL`.
- A click on an element inside a button (an icon `<svg>` in a `<button>`) — the label must come from the button → Task 4 test `Clicks_on_nested_elements_use_the_enclosing_button`.
- Calling `initCodeAtlas` after app code already wrapped `fetch` — CodeAtlas must call that wrapper (not bypass the app's own logic) → Task 2 test `An_app_fetch_wrapper_installed_earlier_is_still_called`.

## File Structure

```
src/web/
  package.json  tsconfig.json  tsconfig.build.json  vitest.config.ts
  src/registry.ts      shared runtime slot on globalThis + types (no OpenTelemetry)
  src/index.ts         trackInteraction, instrumentAxios — pass-through without a runtime
  src/axios.ts         `declare module 'axios'` augmentation (types only)
  src/labels.ts        label from markup, never from values
  src/runtime.ts       initCodeAtlas: provider, exporter, fetch patch, correlation, heuristic
  test/support.ts      start(), fake fetch, helpers
  test/passthrough.test.ts  test/explicit.test.ts  test/axios.test.ts
  test/heuristic.test.ts  test/privacy.test.ts
  test/bundle.test.ts  test/fixtures/app/{index.html,main.ts,control.html,control.ts}
  test/otlp-fixture.test.ts
scripts/pack-local.sh   (+ npm pack)
```

---

### Task 1: Package skeleton and the pass-through API

**Files:**
- Create: `src/web/package.json`, `src/web/tsconfig.json`, `src/web/tsconfig.build.json`, `src/web/vitest.config.ts`, `src/web/src/registry.ts`, `src/web/src/index.ts`, `src/web/src/axios.ts`
- Test: `src/web/test/passthrough.test.ts`

**Interfaces:**
- Produces:
  - `registry.ts`: `export interface InteractionHandle { readonly name: string }`; `export type AxiosLikeConfig = { url?: string; baseURL?: string; method?: string; headers?: unknown; codeatlas?: InteractionHandle; [key: string]: unknown }`; `export interface Runtime { startInteraction(name: string): InteractionHandle; endInteraction(handle: InteractionHandle, error?: unknown): void; fetch(handle: InteractionHandle, input: RequestInfo | URL, init?: RequestInit): Promise<Response>; beforeAxiosRequest<C extends AxiosLikeConfig>(config: C): C; afterAxiosResponse(config: AxiosLikeConfig | undefined, status: number, error?: unknown): void }`; `export function currentRuntime(): Runtime | undefined`; `export function setRuntime(runtime: Runtime | undefined): void`
  - `index.ts`: `export interface Interaction { readonly name: string; fetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response>; axiosConfig(): { codeatlas?: InteractionHandle } }`; `export function trackInteraction<T>(name: string, fn: (ix: Interaction) => Promise<T>): Promise<T>`; `export interface AxiosLikeInstance`; `export function instrumentAxios(instance: AxiosLikeInstance): void`

- [ ] **Step 1: Create the package files**

`src/web/package.json`:
```json
{
  "name": "@codeatlas/web",
  "version": "0.1.0",
  "description": "CodeAtlas Live browser integration (development only).",
  "type": "module",
  "sideEffects": false,
  "files": ["dist"],
  "exports": {
    ".": { "types": "./dist/index.d.ts", "import": "./dist/index.js" },
    "./runtime": { "types": "./dist/runtime.d.ts", "import": "./dist/runtime.js" },
    "./axios": { "types": "./dist/axios.d.ts", "import": "./dist/axios.js" }
  },
  "scripts": {
    "build": "tsc -p tsconfig.build.json",
    "test": "vitest run",
    "typecheck": "tsc -p tsconfig.json --noEmit"
  },
  "dependencies": {
    "@opentelemetry/api": "1.9.1",
    "@opentelemetry/core": "2.12.0",
    "@opentelemetry/exporter-trace-otlp-http": "0.223.0",
    "@opentelemetry/resources": "2.12.0",
    "@opentelemetry/sdk-trace-base": "2.12.0",
    "@opentelemetry/sdk-trace-web": "2.12.0"
  },
  "peerDependencies": { "axios": ">=1" },
  "peerDependenciesMeta": { "axios": { "optional": true } },
  "devDependencies": {
    "@opentelemetry/otlp-transformer": "0.223.0",
    "axios": "^1.20.0",
    "jsdom": "^30.1.1",
    "typescript": "~6.0.2",
    "vite": "^8.3.0",
    "vitest": "^5.0.1"
  }
}
```

`src/web/tsconfig.json`:
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "skipLibCheck": true,
    "types": ["vite/client"],
    "paths": {
      "@codeatlas/web": ["./src/index.ts"],
      "@codeatlas/web/runtime": ["./src/runtime.ts"]
    }
  },
  "include": ["src", "test"]
}
```

`src/web/tsconfig.build.json`:
```json
{
  "extends": "./tsconfig.json",
  "compilerOptions": { "rootDir": "src", "outDir": "dist", "declaration": true, "types": [] },
  "include": ["src"]
}
```

`src/web/vitest.config.ts`:
```ts
import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: { environment: 'jsdom', environmentOptions: { jsdom: { url: 'http://localhost:3000/' } } },
})
```

Run:
```bash
cd src/web && npm install && cd ../..
```
Expected: a `package-lock.json` and `node_modules` (git-ignored) in `src/web`.

- [ ] **Step 2: Write the failing pass-through tests**

`src/web/test/passthrough.test.ts`:
```ts
import { afterEach, describe, expect, it, vi } from 'vitest'
import { instrumentAxios, trackInteraction } from '../src/index'
import { currentRuntime } from '../src/registry'

describe('without the runtime (production)', () => {
  const realFetch = window.fetch
  afterEach(() => { window.fetch = realFetch })

  it('ix.fetch is plain fetch: same arguments, no trace header', async () => {
    const fake = vi.fn(async () => new Response('ok'))
    window.fetch = fake as typeof fetch
    const init = { method: 'POST', headers: { 'x-app': '1' } }

    const result = await trackInteraction('Save', async ix => (await ix.fetch('/api/save', init)).text())

    expect(result).toBe('ok')
    expect(fake).toHaveBeenCalledWith('/api/save', init)
    expect(currentRuntime()).toBeUndefined()
  })

  it('axiosConfig adds nothing', async () => {
    await trackInteraction('Save', async ix => { expect(ix.axiosConfig()).toEqual({}) })
  })

  it('errors from the callback propagate unchanged', async () => {
    const error = new Error('boom')
    await expect(trackInteraction('Save', async () => { throw error })).rejects.toBe(error)
  })

  it('instrumentAxios leaves configs and responses untouched', async () => {
    const request: Array<(c: unknown) => unknown> = []
    const response: Array<(r: unknown) => unknown> = []
    instrumentAxios({
      interceptors: {
        request: { use: (f: (c: unknown) => unknown) => request.push(f) },
        response: { use: (f: (r: unknown) => unknown) => response.push(f) },
      },
    })
    const config = { url: '/api/x', headers: { a: '1' } }
    const reply = { status: 200, config }

    expect(request[0](config)).toBe(config)
    expect(config).toEqual({ url: '/api/x', headers: { a: '1' } })
    expect(response[0](reply)).toBe(reply)
  })
})
```

- [ ] **Step 3: Run to verify failure**

Run: `cd src/web && npx vitest run test/passthrough.test.ts`
Expected: FAIL — `Failed to resolve import "../src/index"`.

- [ ] **Step 4: Implement registry, index and the Axios augmentation**

`src/web/src/registry.ts`:
```ts
// Shared between the pass-through API and the runtime. Nothing here imports OpenTelemetry.

export interface InteractionHandle {
  readonly name: string
}

export type AxiosLikeConfig = {
  url?: string
  baseURL?: string
  method?: string
  headers?: unknown
  codeatlas?: InteractionHandle
  [key: string]: unknown
}

export interface Runtime {
  startInteraction(name: string): InteractionHandle
  endInteraction(handle: InteractionHandle, error?: unknown): void
  fetch(handle: InteractionHandle, input: RequestInfo | URL, init?: RequestInit): Promise<Response>
  beforeAxiosRequest<C extends AxiosLikeConfig>(config: C): C
  afterAxiosResponse(config: AxiosLikeConfig | undefined, status: number, error?: unknown): void
}

const slot = Symbol.for('codeatlas.web.runtime')
type Slot = { [slot]?: Runtime }

export function currentRuntime(): Runtime | undefined {
  return (globalThis as Slot)[slot]
}

export function setRuntime(runtime: Runtime | undefined): void {
  ;(globalThis as Slot)[slot] = runtime
}
```

`src/web/src/index.ts`:
```ts
// The part of CodeAtlas that app code imports. Without the development runtime it is plain fetch / plain Axios,
// so it is safe (and tiny) in production bundles.
import { currentRuntime, type AxiosLikeConfig, type InteractionHandle } from './registry'

export type { InteractionHandle } from './registry'

export interface Interaction {
  readonly name: string
  /** fetch that carries this interaction's trace context. Without the runtime it is plain fetch. */
  fetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response>
  /** Pass as (or spread into) the config of a request made with an instance given to instrumentAxios. */
  axiosConfig(): { codeatlas?: InteractionHandle }
}

/**
 * Links every request made through `ix` to an interaction named `name`, across any number of awaits.
 * Errors thrown by `fn` propagate unchanged.
 */
export async function trackInteraction<T>(name: string, fn: (ix: Interaction) => Promise<T>): Promise<T> {
  const runtime = currentRuntime()
  if (!runtime) return fn({ name, fetch: (input, init) => fetch(input, init), axiosConfig: () => ({}) })

  const handle = runtime.startInteraction(name)
  try {
    const result = await fn({ name, fetch: (input, init) => runtime.fetch(handle, input, init), axiosConfig: () => ({ codeatlas: handle }) })
    runtime.endInteraction(handle)
    return result
  } catch (error) {
    runtime.endInteraction(handle, error)
    throw error
  }
}

type Interceptors = {
  request: { use(onFulfilled: (config: never) => unknown): unknown }
  response: { use(onFulfilled: (response: never) => unknown, onRejected?: (error: never) => unknown): unknown }
}

/** The part of an Axios instance CodeAtlas needs; a real `AxiosInstance` fits. */
export interface AxiosLikeInstance {
  interceptors: Interceptors
}

/** Adds trace context to requests made with `ix.axiosConfig()`. Other requests, and everything without the runtime, are untouched. */
export function instrumentAxios(instance: AxiosLikeInstance): void {
  instance.interceptors.request.use(((config: AxiosLikeConfig) => currentRuntime()?.beforeAxiosRequest(config) ?? config) as (config: never) => unknown)
  instance.interceptors.response.use(
    ((response: { status: number; config?: AxiosLikeConfig }) => {
      currentRuntime()?.afterAxiosResponse(response.config, response.status)
      return response
    }) as (response: never) => unknown,
    ((error: { config?: AxiosLikeConfig; response?: { status: number } }) => {
      currentRuntime()?.afterAxiosResponse(error?.config, error?.response?.status ?? 0, error)
      return Promise.reject(error)
    }) as (error: never) => unknown,
  )
}
```

`src/web/src/axios.ts`:
```ts
// Types only: `import type {} from '@codeatlas/web/axios'` lets `ix.axiosConfig()` be passed as an Axios config.
import type { InteractionHandle } from './registry'

declare module 'axios' {
  interface AxiosRequestConfig {
    codeatlas?: InteractionHandle
  }
}

export {}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd src/web && npx vitest run test/passthrough.test.ts && npx tsc -p tsconfig.json --noEmit`
Expected: `4 passed`; no type errors.

- [ ] **Step 6: Commit**

```bash
git add src/web
git commit -m "feat(web): @codeatlas/web package with a pass-through API"
```

---

### Task 2: Runtime and explicit fetch correlation

**Files:**
- Create: `src/web/src/runtime.ts` (provider, exporter, fetch correlation, explicit interactions; heuristic arrives in Task 4), `src/web/test/support.ts`, `src/web/test/explicit.test.ts`

**Interfaces:**
- Consumes: `registry.ts`, `index.ts` (Task 1).
- Produces:
  - `export interface CodeAtlasOptions { collectorUrl: string; serviceName: string; windowMs?: number; propagateTo?: Array<string | RegExp>; textLabels?: boolean; exporter?: SpanExporter }`
  - `export interface CodeAtlasHandle { readonly collectorUrl: string; shutdown(): Promise<void> }`
  - `export function initCodeAtlas(options: CodeAtlasOptions): CodeAtlasHandle`
  - test helper `start(options?)` → `{ calls, fake, spans(), handle, stop() }`, `traceparent(call)`, `sleep(ms)`

- [ ] **Step 1: Write the test support**

`src/web/test/support.ts`:
```ts
import { InMemorySpanExporter, type ReadableSpan } from '@opentelemetry/sdk-trace-base'
import { vi } from 'vitest'
import { initCodeAtlas, type CodeAtlasOptions } from '../src/runtime'

export type Call = { url: string; method: string; headers: Headers; body: unknown }

/** Installs a fake network as window.fetch, then the runtime with an in-memory exporter. */
export function start(options: Partial<CodeAtlasOptions> = {}) {
  const calls: Call[] = []
  const fake = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : undefined
    const url = new URL(request ? request.url : String(input), location.href).href
    calls.push({
      url,
      method: (init?.method ?? request?.method ?? 'GET').toUpperCase(),
      headers: new Headers(init?.headers ?? request?.headers),
      body: init?.body ?? (request ? await request.clone().text() : undefined),
    })
    if (url.includes('/network-down')) throw new TypeError('Failed to fetch')
    return new Response('{}', { status: url.includes('/boom') ? 500 : 201 })
  })
  window.fetch = fake as typeof fetch
  const exporter = new InMemorySpanExporter()
  const handle = initCodeAtlas({ collectorUrl: 'http://localhost:4318/v1/traces', serviceName: 'test-web', exporter, ...options })
  return {
    calls,
    fake,
    handle,
    spans: (): ReadableSpan[] => exporter.getFinishedSpans(),
    stop: () => handle.shutdown(),
  }
}

export const traceparent = (call: Call) => call.headers.get('traceparent')
export const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms))
export const named = (spans: ReadableSpan[], name: string) => spans.filter(s => s.name === name)
export const parentId = (span: ReadableSpan) => span.parentSpanContext?.spanId
```

- [ ] **Step 2: Write the failing explicit-correlation tests**

`src/web/test/explicit.test.ts`:
```ts
import { SpanKind, SpanStatusCode } from '@opentelemetry/api'
import { afterEach, describe, expect, it } from 'vitest'
import { trackInteraction } from '../src/index'
import { initCodeAtlas } from '../src/runtime'
import { currentRuntime } from '../src/registry'
import { named, parentId, sleep, start, traceparent } from './support'

describe('explicit interactions', () => {
  let current: ReturnType<typeof start> | undefined
  afterEach(async () => { await current?.stop(); current = undefined })

  it('links the request to the interaction and sends its traceparent', async () => {
    current = start()

    await trackInteraction('Create order', ix => ix.fetch('/api/orders', { method: 'POST' }))

    const [interaction] = named(current.spans(), 'interaction: Create order')
    const [client] = named(current.spans(), 'POST /api/orders')
    expect(interaction.attributes['codeatlas.interaction.name']).toBe('Create order')
    expect(interaction.attributes['codeatlas.correlation']).toBe('explicit')
    expect(client.kind).toBe(SpanKind.CLIENT)
    expect(parentId(client)).toBe(interaction.spanContext().spanId)
    expect(client.attributes['codeatlas.correlation']).toBe('explicit')
    expect(client.attributes['http.request.method']).toBe('POST')
    expect(client.attributes['url.path']).toBe('/api/orders')
    expect(client.attributes['http.response.status_code']).toBe(201)
    expect(client.instrumentationScope.name).toBe('codeatlas-web')
    expect(traceparent(current.calls[0])).toBe(`00-${client.spanContext().traceId}-${client.spanContext().spanId}-01`)
  })

  it('keeps each interaction separate across awaits when they interleave', async () => {
    current = start()

    await Promise.all([
      trackInteraction('A', async ix => { await sleep(40); await ix.fetch('/api/a') }),
      trackInteraction('B', async ix => { await sleep(5); await ix.fetch('/api/b') }),
    ])

    const spans = current.spans()
    const [a] = named(spans, 'interaction: A')
    const [b] = named(spans, 'interaction: B')
    const callA = current.calls.find(c => c.url.endsWith('/api/a'))!
    const callB = current.calls.find(c => c.url.endsWith('/api/b'))!
    expect(traceparent(callA)).toContain(a.spanContext().traceId)
    expect(traceparent(callB)).toContain(b.spanContext().traceId)
    expect(a.spanContext().traceId).not.toBe(b.spanContext().traceId)
  })

  it('marks network errors and 5xx responses, and rethrows network errors', async () => {
    current = start()

    await expect(trackInteraction('Down', ix => ix.fetch('/api/network-down'))).rejects.toThrow('Failed to fetch')
    await trackInteraction('Boom', ix => ix.fetch('/api/boom'))

    expect(named(current.spans(), 'GET /api/network-down')[0].status.code).toBe(SpanStatusCode.ERROR)
    expect(named(current.spans(), 'interaction: Down')[0].status.code).toBe(SpanStatusCode.ERROR)
    expect(named(current.spans(), 'GET /api/boom')[0].status.code).toBe(SpanStatusCode.ERROR)
    expect(named(current.spans(), 'interaction: Boom')[0].status.code).toBe(SpanStatusCode.UNSET)
  })

  it('An_error_thrown_by_the_callback_ends_the_interaction_and_propagates', async () => {
    current = start()
    const error = new Error('validation failed')

    await expect(trackInteraction('Save', async () => { throw error })).rejects.toBe(error)

    const [interaction] = named(current.spans(), 'interaction: Save')
    expect(interaction.ended).toBe(true)
    expect(interaction.status.code).toBe(SpanStatusCode.ERROR)
  })

  it('Request_objects_keep_their_method_headers_and_body', async () => {
    current = start()

    await trackInteraction('Save', ix => ix.fetch(new Request('http://localhost:3000/api/save', {
      method: 'PUT', headers: { 'x-app': '1' }, body: 'payload',
    })))

    const [call] = current.calls
    expect(call.method).toBe('PUT')
    expect(call.headers.get('x-app')).toBe('1')
    expect(traceparent(call)).not.toBeNull()
    expect(call.body).toBe('payload')
    expect(named(current.spans(), 'PUT /api/save')).toHaveLength(1)
  })

  it('An_app_fetch_wrapper_installed_earlier_is_still_called', async () => {
    const appWrapperCalls: string[] = []
    current = start()
    await current.stop()
    current = undefined
    const network = window.fetch
    window.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      appWrapperCalls.push(String(input))
      return network(input, init)
    }) as typeof fetch
    const exporter = (await import('@opentelemetry/sdk-trace-base')).InMemorySpanExporter
    const handle = initCodeAtlas({ collectorUrl: 'http://localhost:4318/v1/traces', serviceName: 't', exporter: new exporter() })

    await trackInteraction('Save', ix => ix.fetch('/api/save'))

    expect(appWrapperCalls).toEqual(['/api/save'])
    await handle.shutdown()
  })

  it('resolves a relative collector URL against the page', async () => {
    current = start({ collectorUrl: '/codeatlas/v1/traces' })

    expect(current.handle.collectorUrl).toBe('http://localhost:3000/codeatlas/v1/traces')
  })

  it('is idempotent and shuts down cleanly', async () => {
    current = start()
    const fake = current.fake

    expect(initCodeAtlas({ collectorUrl: 'http://x/v1/traces', serviceName: 'again' })).toBe(current.handle)
    expect((window.fetch as typeof fetch & { __original?: unknown }).__original).toBe(fake)

    await current.stop()
    current = undefined

    expect(window.fetch).toBe(fake)
    expect(currentRuntime()).toBeUndefined()
  })
})
```

- [ ] **Step 3: Run to verify failure**

Run: `cd src/web && npx vitest run test/explicit.test.ts`
Expected: FAIL — `Failed to resolve import "../src/runtime"`.

- [ ] **Step 4: Implement the runtime (explicit part)**

`src/web/src/runtime.ts`:
```ts
// Development only. Load it with `if (import.meta.env.DEV) void import('@codeatlas/web/runtime').then(...)`
// so production builds never contain it.
import { ROOT_CONTEXT, SpanKind, SpanStatusCode, trace, type Span } from '@opentelemetry/api'
import { W3CTraceContextPropagator } from '@opentelemetry/core'
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http'
import { resourceFromAttributes } from '@opentelemetry/resources'
import { BatchSpanProcessor, SimpleSpanProcessor, type SpanExporter } from '@opentelemetry/sdk-trace-base'
import { WebTracerProvider } from '@opentelemetry/sdk-trace-web'
import { setRuntime, type AxiosLikeConfig, type InteractionHandle, type Runtime } from './registry'

export interface CodeAtlasOptions {
  /** OTLP/HTTP traces URL of the collector; may be relative to the page (e.g. a dev-server proxy path). */
  collectorUrl: string
  serviceName: string
  /** Requests started this long after a click/submit are linked to it heuristically. Default 1000 ms. */
  windowMs?: number
  /** Cross-origin URLs (prefix or RegExp) that may receive a traceparent header. Same-origin always may. */
  propagateTo?: Array<string | RegExp>
  /** Use button/link text as interaction labels. Default true. */
  textLabels?: boolean
  /** Test hook: export synchronously to this exporter instead of OTLP. */
  exporter?: SpanExporter
}

export interface CodeAtlasHandle {
  readonly collectorUrl: string
  shutdown(): Promise<void>
}

type Fetch = typeof fetch
interface Handle extends InteractionHandle {
  readonly span: Span
}

let active: CodeAtlasHandle | undefined

export function initCodeAtlas(options: CodeAtlasOptions): CodeAtlasHandle {
  if (active) return active

  const collectorUrl = new URL(options.collectorUrl, location.href).href
  const propagateTo = options.propagateTo ?? []
  const processor = options.exporter
    ? new SimpleSpanProcessor(options.exporter)
    : new BatchSpanProcessor(new OTLPTraceExporter({ url: collectorUrl }), { scheduledDelayMillis: 300 })
  const provider = new WebTracerProvider({
    resource: resourceFromAttributes({ 'service.name': options.serviceName }),
    spanProcessors: [processor],
  })
  const tracer = provider.getTracer('codeatlas-web')
  const propagator = new W3CTraceContextPropagator()
  // Whatever fetch the page has now (possibly the app's own wrapper) is what CodeAtlas calls.
  const original: Fetch = window.fetch

  const resolve = (input: RequestInfo | URL) => new URL(input instanceof Request ? input.url : String(input), location.href)
  const traceable = (url: URL) =>
    url.href !== collectorUrl &&
    (url.origin === location.origin || propagateTo.some(p => (typeof p === 'string' ? url.href.startsWith(p) : p.test(url.href))))

  function startClient(parent: Span, correlation: string, method: string, url: URL): Span {
    return tracer.startSpan(`${method} ${url.pathname}`, {
      kind: SpanKind.CLIENT,
      // Path only: query strings and fragments may hold sensitive values.
      attributes: { 'http.request.method': method, 'url.path': url.pathname, 'codeatlas.correlation': correlation },
    }, trace.setSpan(ROOT_CONTEXT, parent))
  }

  function endClient(span: Span | undefined, status: number, error?: unknown) {
    if (!span) return
    if (status) span.setAttribute('http.response.status_code', status)
    if (error || status >= 500) span.setStatus({ code: SpanStatusCode.ERROR, message: error ? String(error) : `HTTP ${status}` })
    span.end()
  }

  async function tracedFetch(parent: Span, correlation: string, input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
    const url = resolve(input)
    if (!traceable(url)) return original(input, init)
    const request = input instanceof Request ? input : undefined
    const method = (init?.method ?? request?.method ?? 'GET').toUpperCase()
    const span = startClient(parent, correlation, method, url)
    const headers = new Headers(init?.headers ?? request?.headers)
    propagator.inject(trace.setSpan(ROOT_CONTEXT, span), headers, { set: (carrier, key, value) => carrier.set(key, value) })
    try {
      const response = await original(input, { ...init, headers })
      endClient(span, response.status)
      return response
    } catch (error) {
      endClient(span, 0, error)
      throw error
    }
  }

  const runtime: Runtime = {
    startInteraction(name) {
      const span = tracer.startSpan(`interaction: ${name}`, {
        attributes: { 'codeatlas.interaction.name': name, 'codeatlas.correlation': 'explicit' },
      }, ROOT_CONTEXT)
      return { name, span } satisfies Handle
    },
    endInteraction(handle, error) {
      const span = (handle as Handle).span
      if (error !== undefined) span.setStatus({ code: SpanStatusCode.ERROR, message: String(error) })
      span.end()
    },
    fetch: (handle, input, init) => tracedFetch((handle as Handle).span, 'explicit', input, init),
    beforeAxiosRequest: config => config,
    afterAxiosResponse: () => {},
  }

  // Heuristic correlation and the fetch patch are added in Task 4; the patch keeps OpenTelemetry's
  // `__original` convention so the OTLP exporter bypasses it.
  const patched = Object.assign(((input: RequestInfo | URL, init?: RequestInit) => original(input, init)) as Fetch, { __original: original })
  window.fetch = patched
  setRuntime(runtime)

  const handle: CodeAtlasHandle = {
    collectorUrl,
    async shutdown() {
      if (window.fetch === patched) window.fetch = original
      setRuntime(undefined)
      active = undefined
      await provider.shutdown()
    },
  }
  active = handle
  return handle
}

export type { AxiosLikeConfig }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd src/web && npx vitest run && npx tsc -p tsconfig.json --noEmit`
Expected: all tests pass; no type errors.

- [ ] **Step 6: Commit**

```bash
git add src/web
git commit -m "feat(web): runtime with explicit fetch correlation across awaits"
```

---

### Task 3: Axios correlation

**Files:**
- Modify: `src/web/src/runtime.ts` (`beforeAxiosRequest`, `afterAxiosResponse`)
- Test: `src/web/test/axios.test.ts`

**Interfaces:**
- Consumes: `instrumentAxios` (Task 1), runtime (Task 2), `@codeatlas/web/axios` types.
- Produces: Axios requests made with `ix.axiosConfig()` get a client span (`codeatlas.correlation = explicit`) and a `traceparent` header; others are untouched.

- [ ] **Step 1: Write the failing tests**

`src/web/test/axios.test.ts`:
```ts
import type {} from '../src/axios'
import { SpanStatusCode } from '@opentelemetry/api'
import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios'
import { afterEach, describe, expect, it } from 'vitest'
import { instrumentAxios, trackInteraction } from '../src/index'
import { named, parentId, start } from './support'

function fakeAxios(baseURL?: string) {
  const seen: InternalAxiosRequestConfig[] = []
  const api = axios.create({
    baseURL,
    adapter: async config => {
      seen.push(config)
      if (config.url?.includes('boom')) {
        const response = { data: {}, status: 500, statusText: 'Error', headers: {}, config }
        throw new AxiosError('Request failed with status code 500', 'ERR_BAD_RESPONSE', config, null, response)
      }
      return { data: {}, status: 201, statusText: 'Created', headers: {}, config }
    },
  })
  instrumentAxios(api)
  return { api, seen }
}

describe('Axios', () => {
  let current: ReturnType<typeof start> | undefined
  afterEach(async () => { await current?.stop(); current = undefined })

  it('requests made with ix.axiosConfig() are linked explicitly', async () => {
    current = start()
    const { api, seen } = fakeAxios()

    await trackInteraction('Axios order', ix => api.post('/api/orders', { a: 1 }, ix.axiosConfig()))

    const [interaction] = named(current.spans(), 'interaction: Axios order')
    const [client] = named(current.spans(), 'POST /api/orders')
    expect(parentId(client)).toBe(interaction.spanContext().spanId)
    expect(client.attributes['codeatlas.correlation']).toBe('explicit')
    expect(client.attributes['http.response.status_code']).toBe(201)
    expect(seen[0].headers.get('traceparent')).toBe(`00-${client.spanContext().traceId}-${client.spanContext().spanId}-01`)
  })

  it('other requests are untouched', async () => {
    current = start()
    const { api, seen } = fakeAxios()

    await api.get('/api/plain')

    expect(seen[0].headers.get('traceparent')).toBeFalsy()
    expect(current.spans()).toHaveLength(0)
  })

  it('5xx responses mark the span as an error and still reject', async () => {
    current = start()
    const { api } = fakeAxios()

    await expect(trackInteraction('Boom', ix => api.get('/api/boom', ix.axiosConfig()))).rejects.toBeInstanceOf(AxiosError)

    expect(named(current.spans(), 'GET /api/boom')[0].status.code).toBe(SpanStatusCode.ERROR)
  })

  it('Relative_axios_urls_resolve_against_baseURL', async () => {
    current = start()
    const { api, seen } = fakeAxios('https://thirdparty.example/v1/')

    await trackInteraction('Third party', ix => api.get('items', ix.axiosConfig()))

    expect(seen[0].headers.get('traceparent')).toBeFalsy()
    expect(named(current.spans(), 'GET /v1/items')).toHaveLength(0)
  })
})
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src/web && npx vitest run test/axios.test.ts`
Expected: `requests made with ix.axiosConfig() are linked explicitly` and `5xx responses…` FAIL (no client span); the other two pass because the runtime still passes Axios configs through unchanged.

- [ ] **Step 3: Implement Axios support in the runtime**

In `src/web/src/runtime.ts`, replace the two Axios members of `runtime` with:
```ts
    beforeAxiosRequest(config) {
      const handle = config.codeatlas as Handle | undefined
      if (!handle) return config
      const base = config.baseURL ? new URL(config.baseURL, location.href) : new URL(location.href)
      const url = new URL(config.url ?? '', base)
      if (!traceable(url)) return config
      const span = startClient(handle.span, 'explicit', (config.method ?? 'get').toUpperCase(), url)
      const carrier: Record<string, string> = {}
      propagator.inject(trace.setSpan(ROOT_CONTEXT, span), carrier, { set: (c, key, value) => { c[key] = value } })
      const headers = config.headers as { set?: (key: string, value: string) => void } | Record<string, string> | undefined
      if (headers && typeof (headers as { set?: unknown }).set === 'function')
        for (const [key, value] of Object.entries(carrier)) (headers as { set: (k: string, v: string) => void }).set(key, value)
      else config.headers = { ...(headers as Record<string, string> | undefined), ...carrier }
      ;(config as AxiosLikeConfig & { [axiosSpan]?: Span })[axiosSpan] = span
      return config
    },
    afterAxiosResponse(config, status, error) {
      endClient((config as (AxiosLikeConfig & { [axiosSpan]?: Span }) | undefined)?.[axiosSpan], status, error)
    },
```
and add near the top of the file, after the imports:
```ts
const axiosSpan = Symbol('codeatlas.axiosSpan')
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/web && npx vitest run && npx tsc -p tsconfig.json --noEmit`
Expected: all pass; no type errors.

- [ ] **Step 5: Commit**

```bash
git add src/web
git commit -m "feat(web): explicit correlation for Axios requests"
```

---

### Task 4: Heuristic correlation, labels, allow-list, collector exclusion

**Files:**
- Create: `src/web/src/labels.ts`
- Modify: `src/web/src/runtime.ts` (capture listeners, pending interaction, patched fetch, shutdown removes listeners)
- Test: `src/web/test/heuristic.test.ts`

**Interfaces:**
- Consumes: runtime internals from Tasks 2–3.
- Produces: `export function labelOf(element: Element, kind: 'click' | 'submit', textLabels: boolean): { label: string; source: string }`

- [ ] **Step 1: Write the failing tests**

`src/web/test/heuristic.test.ts`:
```ts
import { afterEach, describe, expect, it } from 'vitest'
import { trackInteraction } from '../src/index'
import { named, parentId, sleep, start, traceparent } from './support'

function button(html: string): HTMLElement {
  document.body.innerHTML = html
  return document.body.querySelector('button, a, [role=button], input')!
}

describe('heuristic correlation', () => {
  let current: ReturnType<typeof start> | undefined
  afterEach(async () => {
    await current?.stop()
    current = undefined
    document.body.innerHTML = ''
  })

  it('links a plain fetch after a click and marks it heuristic', async () => {
    current = start()
    button('<button data-codeatlas="Quick order">Order</button>').click()

    await fetch('/api/orders', { method: 'POST' })

    const [interaction] = named(current.spans(), 'interaction: Quick order')
    const [client] = named(current.spans(), 'POST /api/orders')
    expect(interaction.attributes['codeatlas.correlation']).toBe('heuristic')
    expect(interaction.attributes['codeatlas.interaction.label_source']).toBe('data-codeatlas')
    expect(interaction.attributes['codeatlas.interaction.event']).toBe('click')
    expect(parentId(client)).toBe(interaction.spanContext().spanId)
    expect(client.attributes['codeatlas.correlation']).toBe('heuristic')
    expect(traceparent(current.calls[0])).toContain(interaction.spanContext().traceId)
  })

  it('does nothing for requests after the window', async () => {
    current = start({ windowMs: 30 })
    button('<button>Order</button>').click()
    await sleep(60)

    await fetch('/api/orders')

    expect(current.spans()).toHaveLength(0)
    expect(traceparent(current.calls[0])).toBeNull()
  })

  it('a click without a request creates no span', async () => {
    current = start({ windowMs: 30 })
    button('<button>Order</button>').click()
    await sleep(60)

    expect(current.spans()).toHaveLength(0)
  })

  it('an explicit interaction in the same click cancels the heuristic one', async () => {
    current = start()
    const b = button('<button>Order</button>')
    let done: Promise<unknown> = Promise.resolve()
    b.addEventListener('click', () => { done = trackInteraction('Explicit order', ix => ix.fetch('/api/orders')) })

    b.click()
    await done

    expect(current.spans().filter(s => s.name.startsWith('interaction: ')).map(s => s.name)).toEqual(['interaction: Explicit order'])
  })

  it('requests that already carry a traceparent are left alone', async () => {
    current = start()
    const existing = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01'
    button('<button>Order</button>').click()

    await fetch('/api/orders', { headers: { traceparent: existing } })

    expect(traceparent(current.calls[0])).toBe(existing)
    expect(current.spans()).toHaveLength(0)
  })

  it('cross-origin requests get no header unless allow-listed', async () => {
    current = start({ propagateTo: [/^https:\/\/api\.example\//] })
    button('<button>Order</button>').click()

    await fetch('https://thirdparty.example/x')
    await fetch('https://api.example/v1/orders')

    expect(traceparent(current.calls[0])).toBeNull()
    expect(traceparent(current.calls[1])).not.toBeNull()
    expect(named(current.spans(), 'GET /x')).toHaveLength(0)
  })

  it('requests to the collector are never traced, and the original fetch is exposed', async () => {
    current = start()
    button('<button>Order</button>').click()

    await fetch('http://localhost:4318/v1/traces', { method: 'POST' })

    expect(traceparent(current.calls[0])).toBeNull()
    expect(current.spans()).toHaveLength(0)
    expect((window.fetch as typeof fetch & { __original?: unknown }).__original).toBe(current.fake)
  })

  it('Clicks_on_nested_elements_use_the_enclosing_button', async () => {
    current = start()
    document.body.innerHTML = '<button aria-label="Delete order"><svg><path id="icon"></path></svg></button>'
    document.getElementById('icon')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))

    await fetch('/api/orders/1', { method: 'DELETE' })

    const [interaction] = named(current.spans(), 'interaction: Delete order')
    expect(interaction.attributes['codeatlas.interaction.label_source']).toBe('aria-label')
  })

  it('labels come from markup in priority order', async () => {
    current = start()
    const cases: Array<[string, string, string]> = [
      ['<button aria-label="Close dialog">×</button>', 'Close dialog', 'aria-label'],
      ['<button>  Save\n   changes  </button>', 'Save changes', 'text'],
      ['<a href="#" role="button">Open report</a>', 'Open report', 'text'],
      ['<input type="submit" value="Send">', 'Send', 'text'],
      [`<button>${'x'.repeat(60)}</button>`, 'x'.repeat(40), 'text'],
    ]
    for (const [html, label, source] of cases) {
      button(html).dispatchEvent(new MouseEvent('click', { bubbles: true }))
      await fetch('/api/x')
      const [interaction] = named(current.spans(), `interaction: ${label}`)
      expect(interaction.attributes['codeatlas.interaction.label_source']).toBe(source)
    }
  })

  it('textLabels: false falls back to the tag name', async () => {
    current = start({ textLabels: false })
    button('<button>Secret customer name</button>').click()

    await fetch('/api/x')

    expect(named(current.spans(), 'interaction: button')).toHaveLength(1)
  })

  it('form submits use the submitter text, or the form name without one', async () => {
    current = start()
    document.body.innerHTML = '<form name="login"><input name="email"><button type="submit">Sign in</button></form>'
    const form = document.querySelector('form')!
    form.addEventListener('submit', e => e.preventDefault())

    form.requestSubmit(document.querySelector('button')!)
    await fetch('/api/login', { method: 'POST' })
    await sleep(0)
    form.requestSubmit()
    await fetch('/api/login', { method: 'POST' })

    const sources = current.spans().filter(s => s.name.startsWith('interaction: ')).map(s => [s.name, s.attributes['codeatlas.interaction.event']])
    expect(sources).toEqual([['interaction: Sign in', 'submit'], ['interaction: form login', 'submit']])
  })
})
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src/web && npx vitest run test/heuristic.test.ts`
Expected: the tests that expect a heuristic span FAIL on their assertions, because the runtime does not listen to clicks yet (e.g. `Cannot read properties of undefined (reading 'attributes')`). Four tests pass already and that is expected, because they assert that nothing happens: `does nothing for requests after the window`, `a click without a request creates no span`, `requests that already carry a traceparent are left alone`, and `cross-origin requests get no header unless allow-listed`.

- [ ] **Step 3: Implement labels and the heuristic**

`src/web/src/labels.ts`:
```ts
/**
 * A label for an interaction, from markup only. Never reads what a user typed: inputs other than submit/button
 * values, textareas and selects are never consulted.
 */
export function labelOf(element: Element, kind: 'click' | 'submit', textLabels: boolean): { label: string; source: string } {
  const explicit = element.getAttribute('data-codeatlas')
  if (explicit) return { label: explicit, source: 'data-codeatlas' }
  const aria = element.getAttribute('aria-label')
  if (aria) return { label: aria, source: 'aria-label' }
  if (kind === 'submit') {
    const name = element.getAttribute('name') || element.id
    return { label: name ? `form ${name}` : 'form', source: 'form-name' }
  }
  if (textLabels) {
    const text = element instanceof HTMLInputElement
      ? (element.type === 'submit' || element.type === 'button' ? element.value : '')
      : element.textContent ?? ''
    const label = text.replace(/\s+/g, ' ').trim().slice(0, 40)
    if (label) return { label, source: 'text' }
  }
  return { label: element.tagName.toLowerCase(), source: 'tag' }
}
```

In `src/web/src/runtime.ts`:

Add the import:
```ts
import { labelOf } from './labels'
```

Add these declarations after `const traceable = …`:
```ts
  const windowMs = options.windowMs ?? 1000
  const textLabels = options.textLabels ?? true

  // A click is turned into a span only if a request follows it, and dropped if the click starts an explicit interaction.
  type Pending = { label: string; source: string; event: string; at: number; wallMs: number; span?: Span }
  let pending: Pending | null = null

  function onInteraction(event: Event, kind: 'click' | 'submit') {
    const target = event.target instanceof Element ? event.target : null
    const element = kind === 'submit' ? target : target?.closest('[data-codeatlas],button,a,[role=button],input[type=submit],input[type=button]')
    if (!element) return
    const submitter = kind === 'submit' ? (event as SubmitEvent).submitter : null
    const { label, source } = submitter ? labelOf(submitter, 'click', textLabels) : labelOf(element, kind, textLabels)
    pending = { label, source, event: kind, at: performance.now(), wallMs: Date.now() }
  }

  function heuristicSpan(p: Pending): Span {
    if (!p.span) {
      p.span = tracer.startSpan(`interaction: ${p.label}`, {
        startTime: p.wallMs,
        attributes: {
          'codeatlas.interaction.name': p.label,
          'codeatlas.interaction.label_source': p.source,
          'codeatlas.interaction.event': p.event,
          'codeatlas.correlation': 'heuristic',
        },
      }, ROOT_CONTEXT)
      p.span.end(p.wallMs)
    }
    return p.span
  }

  function heuristicFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
    const recent = pending && performance.now() - pending.at <= windowMs ? pending : null
    const request = input instanceof Request ? input : undefined
    const carriesTrace = new Headers(init?.headers ?? request?.headers).has('traceparent')
    if (!recent || carriesTrace) return original(input, init)
    return tracedFetch(heuristicSpan(recent), 'heuristic', input, init)
  }

  const onClick = (event: Event) => onInteraction(event, 'click')
  const onSubmit = (event: Event) => onInteraction(event, 'submit')
```

In `runtime.startInteraction`, cancel the pending heuristic interaction as the first statement:
```ts
      pending = null
```

Replace the placeholder patch with the heuristic one and add the listeners:
```ts
  const patched = Object.assign(((input: RequestInfo | URL, init?: RequestInit) => heuristicFetch(input, init)) as Fetch, { __original: original })
  document.addEventListener('click', onClick, { capture: true, passive: true })
  document.addEventListener('submit', onSubmit, { capture: true, passive: true })
  window.fetch = patched
```

In `shutdown`, remove the listeners first:
```ts
      document.removeEventListener('click', onClick, { capture: true })
      document.removeEventListener('submit', onSubmit, { capture: true })
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/web && npx vitest run && npx tsc -p tsconfig.json --noEmit`
Expected: all pass; no type errors.

If only the submitter half of `form submits use the submitter text…` fails, check whether jsdom delivers `SubmitEvent.submitter` (`console.log(event.constructor.name, (event as SubmitEvent).submitter)` inside `onInteraction`). If jsdom lacks it, that is a test-environment gap, not a product change: record a ruling, keep the production code, change only that assertion to the form-name label, and leave the submitter label to the Plan 5 browser E2E (which runs in Chromium).

- [ ] **Step 5: Commit**

```bash
git add src/web
git commit -m "feat(web): heuristic click/submit correlation with markup labels and allow-list"
```

---

### Task 5: Privacy, production bundle, OTLP fixture, packaging

**Files:**
- Create: `src/web/test/privacy.test.ts`, `src/web/test/bundle.test.ts`, `src/web/test/fixtures/app/index.html`, `src/web/test/fixtures/app/main.ts`, `src/web/test/fixtures/app/control.html`, `src/web/test/fixtures/app/control.ts`, `src/web/test/otlp-fixture.test.ts`
- Modify: `scripts/pack-local.sh` (npm pack)

**Interfaces:**
- Consumes: everything above; `tests/CodeAtlas.Collector.Tests/Fixtures/otlp/js-export.json` (Plan 2).
- Produces: `artifacts/npm/codeatlas-web-0.1.0.tgz`.

- [ ] **Step 1: Write the privacy tests**

`src/web/test/privacy.test.ts`:
```ts
import { afterEach, describe, expect, it } from 'vitest'
import { trackInteraction } from '../src/index'
import { start } from './support'

describe('privacy', () => {
  let current: ReturnType<typeof start> | undefined
  afterEach(async () => {
    await current?.stop()
    current = undefined
    document.body.innerHTML = ''
  })

  const everything = (c: ReturnType<typeof start>) =>
    JSON.stringify(c.spans().map(s => ({ name: s.name, attributes: s.attributes, events: s.events })))

  it('never records typed values, bodies or query strings', async () => {
    current = start()
    document.body.innerHTML = `
      <form name="login">
        <input name="email" value="alice@example.com">
        <input name="password" type="password" value="hunter2-secret">
        <textarea name="note">private note</textarea>
        <button type="submit">Sign in</button>
      </form>`
    const form = document.querySelector('form')!
    form.addEventListener('submit', e => e.preventDefault())

    form.requestSubmit(document.querySelector('button')!)
    await fetch('/api/login?token=SECRET-TOKEN#frag', { method: 'POST', body: JSON.stringify({ password: 'hunter2-secret' }) })
    await trackInteraction('Search', ix => ix.fetch('/api/search?q=SECRET-QUERY'))

    const text = everything(current)
    for (const secret of ['alice@example.com', 'hunter2', 'private note', 'SECRET-TOKEN', 'SECRET-QUERY', 'frag'])
      expect(text).not.toContain(secret)
    expect(text).toContain('"url.path":"/api/login"')
  })
})
```

- [ ] **Step 2: Write the production-bundle tests and fixtures**

`src/web/test/fixtures/app/index.html`:
```html
<!doctype html>
<html><body><button>Save</button><script type="module" src="./main.ts"></script></body></html>
```

`src/web/test/fixtures/app/main.ts` (integrated exactly as the guide says):
```ts
import { instrumentAxios, trackInteraction } from '@codeatlas/web'

if (import.meta.env.DEV)
  void import('@codeatlas/web/runtime').then(m => m.initCodeAtlas({ collectorUrl: 'http://127.0.0.1:4318/v1/traces', serviceName: 'fixture' }))

document.querySelector('button')!.addEventListener('click', () => {
  void trackInteraction('Save', ix => ix.fetch('/api/save', { method: 'POST' }))
})
instrumentAxios({ interceptors: { request: { use: () => 0 }, response: { use: () => 0 } } })
```

`src/web/test/fixtures/app/control.html`:
```html
<!doctype html>
<html><body><script type="module" src="./control.ts"></script></body></html>
```

`src/web/test/fixtures/app/control.ts` (positive control: references the runtime unconditionally, so the search terms must be found):
```ts
void import('@codeatlas/web/runtime').then(m => m.initCodeAtlas({ collectorUrl: 'http://127.0.0.1:4318/v1/traces', serviceName: 'control' }))
```

`src/web/test/bundle.test.ts`:
```ts
// @vitest-environment node
import { mkdtempSync, readdirSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'
import { describe, expect, it } from 'vitest'

const root = fileURLToPath(new URL('./fixtures/app', import.meta.url))
const src = fileURLToPath(new URL('../src', import.meta.url))

async function bundle(entry: 'index.html' | 'control.html'): Promise<string> {
  const outDir = mkdtempSync(join(tmpdir(), 'codeatlas-web-bundle-'))
  await build({
    root,
    mode: 'production',
    logLevel: 'silent',
    resolve: {
      alias: [
        { find: /^@codeatlas\/web\/runtime$/, replacement: join(src, 'runtime.ts') },
        { find: /^@codeatlas\/web$/, replacement: join(src, 'index.ts') },
      ],
    },
    build: { outDir, emptyOutDir: true, rollupOptions: { input: join(root, entry) } },
  })
  const assets = join(outDir, 'assets')
  return readdirSync(assets).filter(f => f.endsWith('.js')).map(f => readFileSync(join(assets, f), 'utf8')).join('\n')
}

// Strings that only the runtime contains: OpenTelemetry's header name, our tracer name, the collector port.
const runtimeMarkers = /traceparent|codeatlas-web|4318|interaction: /

describe('production builds', () => {
  it('contain no runtime, no OpenTelemetry and no collector URL', async () => {
    expect(await bundle('index.html')).not.toMatch(runtimeMarkers)
  }, 60_000)

  it('control: a build that references the runtime does contain the markers', async () => {
    expect(await bundle('control.html')).toMatch(runtimeMarkers)
  }, 60_000)
})
```

- [ ] **Step 3: Write the OTLP fixture shape test**

`src/web/test/otlp-fixture.test.ts`:
```ts
// @vitest-environment node
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { SpanKind, ROOT_CONTEXT, trace } from '@opentelemetry/api'
import { JsonTraceSerializer } from '@opentelemetry/otlp-transformer'
import { resourceFromAttributes } from '@opentelemetry/resources'
import { BasicTracerProvider, InMemorySpanExporter, SimpleSpanProcessor } from '@opentelemetry/sdk-trace-base'
import { describe, expect, it } from 'vitest'

// The collector's tests read this hand-written fixture; it must keep the shape the real serializer produces.
const fixturePath = fileURLToPath(new URL('../../../tests/CodeAtlas.Collector.Tests/Fixtures/otlp/js-export.json', import.meta.url))

type Json = Record<string, unknown>
const shape = (value: unknown): string => Array.isArray(value) ? 'array' : value === null ? 'null' : typeof value

describe('OTLP/JSON fixture', () => {
  it('matches the shape of what @opentelemetry/exporter-trace-otlp-http sends', () => {
    const exporter = new InMemorySpanExporter()
    const provider = new BasicTracerProvider({
      resource: resourceFromAttributes({ 'service.name': 'sample-web' }),
      spanProcessors: [new SimpleSpanProcessor(exporter)],
    })
    const tracer = provider.getTracer('codeatlas-web')
    const interaction = tracer.startSpan('interaction: Create order', { attributes: { 'codeatlas.interaction.name': 'Create order', 'codeatlas.correlation': 'explicit' } }, ROOT_CONTEXT)
    const client = tracer.startSpan('POST /api/orders', {
      kind: SpanKind.CLIENT,
      attributes: { 'http.request.method': 'POST', 'url.path': '/api/orders', 'http.response.status_code': 201, 'codeatlas.correlation': 'explicit' },
    }, trace.setSpan(ROOT_CONTEXT, interaction))
    client.end()
    interaction.end()

    const real = JSON.parse(new TextDecoder().decode(JsonTraceSerializer.serializeRequest(exporter.getFinishedSpans())!)) as Json
    const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as Json

    const spans = (doc: Json) => ((doc.resourceSpans as Json[])[0].scopeSpans as Json[])[0].spans as Json[]
    const realClient = spans(real).find(s => s.name === 'POST /api/orders')!
    const fixtureClient = spans(fixture).find(s => s.name === 'POST /api/orders')!
    for (const field of ['traceId', 'spanId', 'parentSpanId', 'kind', 'startTimeUnixNano', 'endTimeUnixNano'])
      expect(`${field}: ${shape(fixtureClient[field])}`).toBe(`${field}: ${shape(realClient[field])}`)
    expect(realClient.traceId).toMatch(/^[0-9a-f]{32}$/)
    expect(fixtureClient.traceId).toMatch(/^[0-9a-f]{32}$/)

    const status = (s: Json) => ((s.attributes as Json[]).find(a => a.key === 'http.response.status_code')!.value as Json).intValue
    expect(`intValue: ${shape(status(fixtureClient))}`).toBe(`intValue: ${shape(status(realClient))}`)
  })
})
```

- [ ] **Step 4: Run all tests**

Run: `cd src/web && npx vitest run`
Expected: all pass.

If `otlp-fixture.test.ts` fails, the hand-written fixture does not match the real serializer: change only the representation in `tests/CodeAtlas.Collector.Tests/Fixtures/otlp/js-export.json` (e.g. `"intValue": 201` ↔ `"intValue": "201"`, or times as numbers ↔ strings) to what the serializer emits, then run `dotnet test --project tests/CodeAtlas.Collector.Tests` to confirm the collector still parses it.

If `bundle.test.ts`'s first test fails, inspect which marker matched before changing anything: a leak means `index.ts` or `registry.ts` gained a runtime import, which is a defect, not a test to loosen.

- [ ] **Step 5: Pack the package**

Append to `scripts/pack-local.sh` before `ls artifacts/nuget`:
```bash
mkdir -p artifacts/npm
(cd src/web && npm ci --silent && npm run build --silent && npm pack --silent --pack-destination ../../artifacts/npm)
ls artifacts/npm
```

Run:
```bash
scripts/pack-local.sh
```
Expected: the listing includes `codeatlas-web-0.1.0.tgz`.

- [ ] **Step 6: Commit**

```bash
git add src/web scripts/pack-local.sh tests/CodeAtlas.Collector.Tests/Fixtures/otlp/js-export.json
git commit -m "test(web): privacy, production-bundle and OTLP fixture checks; npm packaging"
```
