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
