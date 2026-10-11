// Development only. Load it with `if (import.meta.env.DEV) void import('@codeatlas/web/runtime').then(...)`
// so production builds never contain it.
import { ROOT_CONTEXT, SpanKind, SpanStatusCode, trace, type Span } from '@opentelemetry/api'
import { W3CTraceContextPropagator } from '@opentelemetry/core'
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http'
import { resourceFromAttributes } from '@opentelemetry/resources'
import { BatchSpanProcessor, SimpleSpanProcessor, type SpanExporter } from '@opentelemetry/sdk-trace-base'
import { WebTracerProvider } from '@opentelemetry/sdk-trace-web'
import { labelOf } from './labels'
import { setRuntime, type AxiosLikeConfig, type InteractionHandle, type Runtime } from './registry'

const axiosSpan = Symbol('codeatlas.axiosSpan')

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
    // Checked here too, so an untraceable request (collector, cross-origin) never creates the interaction span.
    if (!recent || carriesTrace || !traceable(resolve(input))) return original(input, init)
    return tracedFetch(heuristicSpan(recent), 'heuristic', input, init)
  }

  const onClick = (event: Event) => onInteraction(event, 'click')
  const onSubmit = (event: Event) => onInteraction(event, 'submit')

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
      pending = null
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
      // Axios rejects every non-2xx; like fetch, only network errors and 5xx count as span errors.
      endClient((config as (AxiosLikeConfig & { [axiosSpan]?: Span }) | undefined)?.[axiosSpan], status, status ? undefined : error)
    },
  }

  // The patch keeps OpenTelemetry's `__original` convention so the OTLP exporter bypasses it.
  const patched = Object.assign(((input: RequestInfo | URL, init?: RequestInit) => heuristicFetch(input, init)) as Fetch, { __original: original })
  document.addEventListener('click', onClick, { capture: true, passive: true })
  document.addEventListener('submit', onSubmit, { capture: true, passive: true })
  window.fetch = patched
  setRuntime(runtime)

  const handle: CodeAtlasHandle = {
    collectorUrl,
    async shutdown() {
      document.removeEventListener('click', onClick, { capture: true })
      document.removeEventListener('submit', onSubmit, { capture: true })
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
