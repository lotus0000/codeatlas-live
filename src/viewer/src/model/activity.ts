import type { TraceSummary } from '@/api/types'

export const MAX_TRACES = 500

function newestFirst(a: TraceSummary, b: TraceSummary): number {
  const difference = BigInt(b.startUnixNano) - BigInt(a.startUnixNano)
  return difference > 0n ? 1 : difference < 0n ? -1 : a.traceId.localeCompare(b.traceId)
}

/** Newest first. A summary for a known trace replaces the older copy: spans of a trace keep arriving. */
export function mergeSummaries(current: TraceSummary[], incoming: TraceSummary[]): TraceSummary[] {
  const byId = new Map(current.map(s => [s.traceId, s]))
  for (const summary of incoming) byId.set(summary.traceId, summary)
  return [...byId.values()].sort(newestFirst).slice(0, MAX_TRACES)
}

/** The collector fills method, route and status from the trace's server span. */
export const hasServer = (s: TraceSummary) => s.method !== null || s.route !== null || s.statusCode !== null

/** Follow mode shows the newest trace that reached the backend (spec §8). */
export function followTarget(list: TraceSummary[]): string | null {
  return list.find(hasServer)?.traceId ?? null
}

export function matches(s: TraceSummary, filter: string): boolean {
  const term = filter.trim().toLowerCase()
  if (!term) return true
  return [s.interaction, s.method, s.route, s.statusCode?.toString(), s.traceId].some(value => value?.toLowerCase().includes(term))
}

export function statusTone(s: TraceSummary): 'ok' | 'warn' | 'error' | 'unknown' {
  if (s.hasError || (s.statusCode ?? 0) >= 500) return 'error'
  if (s.statusCode === null) return 'unknown'
  return s.statusCode >= 400 ? 'warn' : 'ok'
}
