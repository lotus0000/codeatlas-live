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
      const status = config.url?.includes('boom') ? 500 : config.url?.includes('missing') ? 404 : 0
      if (status) {
        const response = { data: {}, status, statusText: 'Error', headers: {}, config }
        throw new AxiosError(`Request failed with status code ${status}`, 'ERR_BAD_RESPONSE', config, null, response)
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

  it('4xx responses still reject but are not span errors, as with fetch', async () => {
    current = start()
    const { api } = fakeAxios()

    await expect(trackInteraction('Missing', ix => api.get('/api/missing', ix.axiosConfig()))).rejects.toBeInstanceOf(AxiosError)

    const [client] = named(current.spans(), 'GET /api/missing')
    expect(client.attributes['http.response.status_code']).toBe(404)
    expect(client.status.code).toBe(SpanStatusCode.UNSET)
  })

  it('a baseURL with a path is joined the way Axios joins it', async () => {
    current = start({ propagateTo: ['http://localhost:5000/api'] })
    const { api, seen } = fakeAxios('http://localhost:5000/api')

    await trackInteraction('Joined', async ix => {
      await api.post('/orders', {}, ix.axiosConfig())
      await api.post('lines', {}, ix.axiosConfig())
    })

    expect(seen.map(c => c.headers.get('traceparent') ? 'traced' : 'plain')).toEqual(['traced', 'traced'])
    expect(named(current.spans(), 'POST /api/orders')).toHaveLength(1)
    expect(named(current.spans(), 'POST /api/lines')).toHaveLength(1)
  })

  it('a same-origin relative baseURL keeps its path in the span name', async () => {
    current = start()
    const { api } = fakeAxios('/api')

    await trackInteraction('Same origin', ix => api.get('/orders', ix.axiosConfig()))

    expect(named(current.spans(), 'GET /api/orders')).toHaveLength(1)
  })

  it('plain Axios requests right after a click are linked heuristically', async () => {
    current = start()
    const { api, seen } = fakeAxios()
    document.body.innerHTML = '<button>Refresh</button>'
    document.querySelector('button')!.click()

    await api.get('/api/orders')

    const [interaction] = named(current.spans(), 'interaction: Refresh')
    const [client] = named(current.spans(), 'GET /api/orders')
    expect(interaction.attributes['codeatlas.correlation']).toBe('heuristic')
    expect(parentId(client)).toBe(interaction.spanContext().spanId)
    expect(client.attributes['codeatlas.correlation']).toBe('heuristic')
    expect(seen[0].headers.get('traceparent')).toContain(interaction.spanContext().traceId)
    document.body.innerHTML = ''
  })

  it('Relative_axios_urls_resolve_against_baseURL', async () => {
    current = start()
    const { api, seen } = fakeAxios('https://thirdparty.example/v1/')

    await trackInteraction('Third party', ix => api.get('items', ix.axiosConfig()))

    expect(seen[0].headers.get('traceparent')).toBeFalsy()
    expect(named(current.spans(), 'GET /v1/items')).toHaveLength(0)
  })
})
