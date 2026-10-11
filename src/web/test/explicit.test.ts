import { SpanKind, SpanStatusCode } from '@opentelemetry/api'
import { afterEach, describe, expect, it, vi } from 'vitest'
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

  it('An_app_fetch_wrapper_that_spreads_record_headers_still_sees_them', async () => {
    current = start()
    await current.stop()
    current = undefined
    const network = window.fetch
    const seen: Headers[] = []
    window.fetch = ((input: RequestInfo | URL, init?: RequestInit) => {
      const merged = { ...(init?.headers as Record<string, string> | undefined), 'x-auth': 't' }
      seen.push(new Headers(merged))
      return network(input, { ...init, headers: merged })
    }) as typeof fetch
    const { InMemorySpanExporter } = await import('@opentelemetry/sdk-trace-base')
    const handle = initCodeAtlas({ collectorUrl: 'http://localhost:4318/v1/traces', serviceName: 't', exporter: new InMemorySpanExporter() })

    await trackInteraction('Save', ix => ix.fetch('/api/x', { method: 'POST', headers: { 'content-type': 'application/json' } }))
    await handle.shutdown()

    expect(seen[0].get('content-type')).toBe('application/json')
    expect(seen[0].get('x-auth')).toBe('t')
    expect(seen[0].get('traceparent')).not.toBeNull()
  })

  it('keeps the header form the caller used', async () => {
    current = start()
    const forms: unknown[] = []
    current.fake.mockImplementation(async (_input: RequestInfo | URL, init?: RequestInit) => {
      forms.push(Array.isArray(init?.headers) ? 'array' : init?.headers instanceof Headers ? 'Headers' : typeof init?.headers)
      return new Response('{}')
    })

    await trackInteraction('Forms', async ix => {
      await ix.fetch('/api/a', { headers: { 'content-type': 'text/plain' } })
      await ix.fetch('/api/b', { headers: [['content-type', 'text/plain']] })
      await ix.fetch('/api/c', { headers: new Headers({ 'content-type': 'text/plain' }) })
    })

    expect(forms).toEqual(['object', 'array', 'Headers'])
  })

  it('The_exporter_bypasses_an_app_fetch_wrapper', async () => {
    const native = vi.fn(async () => new Response('{}'))
    const frame = vi.spyOn(HTMLIFrameElement.prototype, 'contentWindow', 'get').mockReturnValue({ fetch: native } as unknown as Window)
    const wrapper = (async () => new Response('{}')) as typeof fetch
    window.fetch = wrapper
    try {
      const { InMemorySpanExporter } = await import('@opentelemetry/sdk-trace-base')
      const handle = initCodeAtlas({ collectorUrl: 'http://localhost:4318/v1/traces', serviceName: 't', exporter: new InMemorySpanExporter() })
      const exporterFetch = (window.fetch as typeof fetch & { __original?: typeof fetch }).__original!
      await exporterFetch('http://localhost:4318/v1/traces', { method: 'POST' })
      await handle.shutdown()

      expect(native).toHaveBeenCalledTimes(1)
      expect(window.fetch).toBe(wrapper)
      expect(document.querySelector('iframe')).toBeNull()
    } finally {
      frame.mockRestore()
    }
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
