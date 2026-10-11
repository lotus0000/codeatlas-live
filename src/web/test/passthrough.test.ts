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
        request: { use: (f: (c: never) => unknown) => request.push(f as (c: unknown) => unknown) },
        response: { use: (f: (r: never) => unknown) => response.push(f as (r: unknown) => unknown) },
      },
    })
    const config = { url: '/api/x', headers: { a: '1' } }
    const reply = { status: 200, config }

    expect(request[0](config)).toBe(config)
    expect(config).toEqual({ url: '/api/x', headers: { a: '1' } })
    expect(response[0](reply)).toBe(reply)
  })
})
