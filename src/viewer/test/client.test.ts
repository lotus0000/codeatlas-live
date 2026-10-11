import { beforeEach, describe, expect, it } from 'vitest'
import { apiUrl, getSource, getTrace, openInEditor, resetSessionToken } from '../src/api/client'
import { stubApi, success } from './support'

const handler = success.roots[0].children[0].children[0].children[0].children[0]

describe('API client', () => {
  beforeEach(() => resetSessionToken())

  it('resolves every URL against the page, so a path base works', () => {
    document.head.insertAdjacentHTML('beforeend', '<base href="http://localhost/absproxy/4318/">')
    expect(apiUrl('api/info')).toBe('http://localhost/absproxy/4318/api/info')
  })

  it('getTrace returns null for an evicted trace', async () => {
    stubApi({})
    expect(await getTrace('0'.repeat(32))).toBeNull()
  })

  it('getSource sends the method identity only', async () => {
    const calls = stubApi({ 'api/source?': { location: { found: false }, text: null } })
    await getSource(handler.code!)
    const query = new URL(calls[0].url).searchParams
    expect([...query.keys()].sort()).toEqual(['assembly', 'mvid', 'token'])
    expect(query.get('token')).toBe(String(handler.code!.token))
  })

  it('openInEditor posts the identity as JSON with the session token', async () => {
    const calls = stubApi({ 'api/session': { token: 'abc' }, 'api/open': { message: 'ok', file: '/repo/app/Fixture.cs', line: 13 } })

    const result = await openInEditor(handler.code!)

    const open = calls.find(c => c.url.endsWith('api/open'))!
    expect(open.init?.method).toBe('POST')
    expect(new Headers(open.init?.headers).get('X-CodeAtlas-Token')).toBe('abc')
    expect(new Headers(open.init?.headers).get('content-type')).toBe('application/json')
    expect(JSON.parse(String(open.init?.body))).toEqual({ assembly: handler.code!.assembly, mvid: handler.code!.mvid, token: handler.code!.token })
    expect(result.line).toBe(13)
  })

  it('openInEditor fetches a new token and retries once after a 401', async () => {
    let sessions = 0
    let opens = 0
    stubApi({
      'api/session': () => Response.json({ token: `t${++sessions}` }),
      'api/open': (init?: RequestInit) => {
        opens++
        return new Headers(init?.headers).get('X-CodeAtlas-Token') === 't2'
          ? Response.json({ message: 'ok', file: 'f', line: 1 })
          : Response.json({ detail: 'Missing or wrong session token.' }, { status: 401 })
      },
    })

    await expect(openInEditor(handler.code!)).resolves.toMatchObject({ line: 1 })
    expect([sessions, opens]).toEqual([2, 2])
  })

  it('surfaces the collector\'s problem detail', async () => {
    stubApi({ 'api/session': { token: 'abc' }, 'api/open': () => Response.json({ title: 'Bad Gateway', detail: 'code-server exited with 1' }, { status: 502 }) })
    await expect(openInEditor(handler.code!)).rejects.toThrow('code-server exited with 1')
  })
})
