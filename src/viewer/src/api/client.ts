import type { CodeRef, CollectorInfo, OpenResult, SourceResponse, TraceSummary, TraceView } from './types'

/** Every request is relative to the page, so the viewer works under a path base such as /absproxy/4318/. */
export const apiUrl = (path: string) => new URL(path, document.baseURI).href

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message)
  }
}

async function problem(response: Response): Promise<ApiError> {
  let message = `HTTP ${response.status}`
  try {
    const body = (await response.json()) as { detail?: string; title?: string }
    message = body.detail ?? body.title ?? message
  } catch {
    // not JSON: keep the status text
  }
  return new ApiError(message, response.status)
}

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(apiUrl(path), { headers: { accept: 'application/json' } })
  if (!response.ok) throw await problem(response)
  return (await response.json()) as T
}

export const getInfo = () => getJson<CollectorInfo>('api/info')
export const getTraces = (limit = 200) => getJson<TraceSummary[]>(`api/traces?limit=${limit}`)

export async function getTrace(traceId: string): Promise<TraceView | null> {
  try {
    return await getJson<TraceView>(`api/traces/${encodeURIComponent(traceId)}`)
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null
    throw error
  }
}

export function getSource(code: CodeRef) {
  const query = new URLSearchParams({ assembly: code.assembly, mvid: code.mvid, token: String(code.token) })
  return getJson<SourceResponse>(`api/source?${query}`)
}

let session: Promise<string> | undefined

export function resetSessionToken() {
  session = undefined
}

/** Asks the collector to open the method in the configured editor. Only the identity is sent; the collector finds the file. */
export async function openInEditor(code: CodeRef): Promise<OpenResult> {
  for (let attempt = 0; ; attempt++) {
    session ??= getJson<{ token: string }>('api/session').then(s => s.token)
    const response = await fetch(apiUrl('api/open'), {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'X-CodeAtlas-Token': await session },
      body: JSON.stringify({ assembly: code.assembly, mvid: code.mvid, token: code.token }),
    })
    // A restarted collector has a new token: fetch it and try once more.
    if (response.status === 401 && attempt === 0) {
      session = undefined
      continue
    }
    if (!response.ok) throw await problem(response)
    return (await response.json()) as OpenResult
  }
}
