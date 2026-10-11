import { vi } from 'vitest'
import type { LiveSource, LiveState } from '../src/api/live'
import type { CollectorInfo, SourceResponse, TraceSummary, TraceView } from '../src/api/types'
import failureJson from './fixtures/failure.json'
import heuristicJson from './fixtures/heuristic.json'
import infoJson from './fixtures/info.json'
import sourceFoundJson from './fixtures/source-found.json'
import sourceRefusedJson from './fixtures/source-refused.json'
import successJson from './fixtures/success.json'
import summariesJson from './fixtures/summaries.json'

// Plain assignments (no casts): if the collector's JSON stops matching src/api/types.ts, tsc fails here.
export const success: TraceView = successJson
export const failure: TraceView = failureJson
export const heuristic: TraceView = heuristicJson
export const summaries: TraceSummary[] = summariesJson
export const info: CollectorInfo = infoJson
export const sourceFound: SourceResponse = sourceFoundJson
export const sourceRefused: SourceResponse = sourceRefusedJson

export type Route = unknown | ((init?: RequestInit) => Response | Promise<Response>)

/** Replaces fetch. A URL containing a key gets that route (longest key wins); anything else is 404. */
export function stubApi(routes: Record<string, Route>) {
  const calls: { url: string; init?: RequestInit }[] = []
  const keys = Object.keys(routes).sort((a, b) => b.length - a.length)
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, init })
    const key = keys.find(k => url.includes(k))
    if (key === undefined) return new Response('', { status: 404 })
    const route = routes[key]
    return typeof route === 'function' ? (route as (init?: RequestInit) => Response | Promise<Response>)(init) : Response.json(route)
  }))
  return calls
}

/** The usual collector: all fixtures at their real paths. */
export function stubCollector(extra: Record<string, Route> = {}) {
  return stubApi({
    'api/info': info,
    'api/traces?': summaries,
    [`api/traces/${success.summary.traceId}`]: success,
    [`api/traces/${failure.summary.traceId}`]: failure,
    [`api/traces/${heuristic.summary.traceId}`]: heuristic,
    'api/source?': sourceFound,
    'api/session': { token: 'session-token' },
    ...extra,
  })
}

export class FakeLive implements LiveSource {
  update: (s: TraceSummary[]) => void = () => {}
  state: (s: LiveState) => void = () => {}
  stopped = false
  start(onUpdate: (s: TraceSummary[]) => void, onState: (s: LiveState) => void) {
    this.update = onUpdate
    this.state = onState
    onState('live')
    return () => { this.stopped = true }
  }
}
