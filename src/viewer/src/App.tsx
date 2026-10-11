import { ArrowRight } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { getInfo, getTrace, getTraces } from '@/api/client'
import { signalRLive, type LiveSource, type LiveState } from '@/api/live'
import type { CollectorInfo, TraceSummary, TraceView } from '@/api/types'
import { ActivityList } from '@/components/ActivityList'
import { CorrelationBadge, StatusText } from '@/components/badges'
import { ExecutionTree } from '@/components/ExecutionTree'
import { Inspector } from '@/components/Inspector'
import { TopBar } from '@/components/TopBar'
import { followTarget, hasServer, mergeSummaries } from '@/model/activity'
import { formatMs, shortId } from '@/model/time'
import { defaultSelection, findNode } from '@/model/tree'
import { useTheme } from '@/theme'

export const DEFAULT_SLOW_MS = 200

function slowFromUrl(): number {
  const value = Number(new URLSearchParams(location.search).get('slow'))
  return Number.isFinite(value) && value > 0 ? value : DEFAULT_SLOW_MS
}

export function App({ live = signalRLive }: { live?: LiveSource }) {
  const theme = useTheme()
  const [slowMs] = useState(slowFromUrl)
  const [info, setInfo] = useState<CollectorInfo | null>(null)
  const [summaries, setSummaries] = useState<TraceSummary[]>([])
  const [liveState, setLiveState] = useState<LiveState>('connecting')
  const [follow, setFollow] = useState(true)
  const [filter, setFilter] = useState('')
  const [traceId, setTraceId] = useState<string | null>(null)
  const [view, setView] = useState<TraceView | null>(null)
  const [missing, setMissing] = useState(false)
  const [spanId, setSpanId] = useState<string | null>(null)
  const [refresh, setRefresh] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const selected = useRef<string | null>(null)
  selected.current = traceId

  const reload = useCallback(() => {
    getTraces().then(
      list => {
        setError(null)
        setSummaries(current => mergeSummaries(current, list))
      },
      (e: Error) => setError(`Could not load traces: ${e.message}`),
    )
  }, [])

  useEffect(() => {
    getInfo().then(setInfo, () => setInfo(null))
    reload()
  }, [reload])

  useEffect(
    () =>
      live.start(
        updates => {
          setSummaries(current => mergeSummaries(current, updates))
          if (updates.some(u => u.traceId === selected.current)) setRefresh(n => n + 1)
        },
        state => {
          setLiveState(state)
          // Updates may have been missed while disconnected.
          if (state === 'live') reload()
        },
      ),
    [live, reload],
  )

  useEffect(() => {
    if (!follow) return
    const target = followTarget(summaries)
    if (target && target !== selected.current) {
      setTraceId(target)
      setSpanId(null)
    }
  }, [follow, summaries])

  useEffect(() => {
    if (!traceId) return
    let current = true
    getTrace(traceId).then(
      next => {
        if (!current) return
        setView(next)
        setMissing(next === null)
        setSpanId(previous => (next && previous && findNode(next.roots, previous) ? previous : next ? defaultSelection(next.roots) ?? null : null))
      },
      (e: Error) => current && setError(`Could not load trace: ${e.message}`),
    )
    return () => {
      current = false
    }
  }, [traceId, refresh])

  const choose = (id: string) => {
    setFollow(false)
    setTraceId(id)
    setSpanId(null)
  }

  const shown = view && view.summary.traceId === traceId ? view : null

  return (
    <div className="flex h-screen flex-col bg-background text-foreground">
      <TopBar info={info} liveState={liveState} follow={follow} onFollow={setFollow} mode={theme.mode} onCycleMode={theme.cycle} />
      {error && (
        <p role="alert" className="border-b bg-error-soft px-4 py-1.5 text-xs text-error">
          {error}
        </p>
      )}
      <div className="grid min-h-0 flex-1 grid-cols-[300px_minmax(0,1fr)_420px]">
        <ActivityList summaries={summaries} selected={traceId} filter={filter} onFilter={setFilter} onSelect={choose} />
        <main aria-label="Trace" className="flex min-h-0 flex-col border-x">
          {shown ? (
            <>
              <TraceHeader summary={shown.summary} truncated={shown.truncated} />
              <ExecutionTree key={shown.summary.traceId} view={shown} selected={spanId} onSelect={setSpanId} slowMs={slowMs} />
            </>
          ) : (
            <p className="m-auto max-w-sm p-6 text-center text-sm text-muted-foreground">
              {missing
                ? 'This trace is no longer in the collector (older traces are evicted).'
                : traceId
                  ? 'Loading trace…'
                  : 'No trace selected. Traces that reach your backend are followed automatically.'}
            </p>
          )}
        </main>
        <Inspector view={shown} spanId={spanId} info={info} dark={theme.dark} onSelect={setSpanId} />
      </div>
    </div>
  )
}

function TraceHeader({ summary, truncated }: { summary: TraceSummary; truncated: boolean }) {
  return (
    <div className="border-b">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-3">
        <span className="font-medium">{summary.interaction ?? 'No interaction'}</span>
        {summary.correlation && <CorrelationBadge kind={summary.correlation} />}
        <ArrowRight className="size-4 text-muted-foreground" aria-hidden />
        <span className="font-mono text-sm">{hasServer(summary) ? `${summary.method ?? ''} ${summary.route ?? ''}`.trim() : 'no server span'}</span>
        <StatusText summary={summary} />
        <span className="text-sm text-muted-foreground">
          {formatMs(summary.durationMs)} · {summary.spanCount} spans
        </span>
        <span className="ml-auto font-mono text-xs text-muted-foreground" title={summary.traceId}>
          {shortId(summary.traceId)}
        </span>
      </div>
      {truncated && <p className="px-4 pb-2 text-xs text-gap">This trace reached the span limit; later spans were dropped.</p>}
    </div>
  )
}
