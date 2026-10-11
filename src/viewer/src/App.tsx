import { ArrowRight } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useDefaultLayout, type LayoutStorage } from 'react-resizable-panels'
import { getInfo, getTrace, getTraces } from '@/api/client'
import { signalRLive, type LiveSource, type LiveState } from '@/api/live'
import type { CollectorInfo, TraceSummary, TraceView } from '@/api/types'
import { ActivityList } from '@/components/ActivityList'
import { CorrelationBadge, StatusText } from '@/components/badges'
import { ExecutionTree } from '@/components/ExecutionTree'
import { ExplainView } from '@/components/ExplainView'
import { Inspector } from '@/components/Inspector'
import { TopBar } from '@/components/TopBar'
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from '@/components/ui/resizable'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { followTarget, hasServer, mergeSummaries } from '@/model/activity'
import { formatMs, shortId } from '@/model/time'
import { defaultSelection, findNode } from '@/model/tree'
import { useTheme } from '@/theme'

export const DEFAULT_SLOW_MS = 200

function slowFromUrl(): number {
  const value = Number(new URLSearchParams(location.search).get('slow'))
  return Number.isFinite(value) && value > 0 ? value : DEFAULT_SLOW_MS
}

// Pane widths are a per-viewer convenience: kept in localStorage when it is available, never required.
const paneStorage: LayoutStorage = {
  getItem: key => {
    try {
      return localStorage.getItem(key)
    } catch {
      return null
    }
  },
  setItem: (key, value) => {
    try {
      localStorage.setItem(key, value)
    } catch {
      // Storage blocked: the widths last until reload.
    }
  },
}

export function App({ live = signalRLive }: { live?: LiveSource }) {
  const theme = useTheme()
  const panes = useDefaultLayout({ id: 'codeatlas.panes', storage: paneStorage, onlySaveAfterUserInteractions: true })
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
  const [tab, setTab] = useState<'execution' | 'explain'>('execution')
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

  const showSpan = (id: string) => {
    setTab('execution')
    setSpanId(id)
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
      <ResizablePanelGroup orientation="horizontal" defaultLayout={panes.defaultLayout} onLayoutChanged={panes.onLayoutChanged} className="min-h-0 flex-1">
        <ResizablePanel id="activity" defaultSize="21" minSize={200} maxSize="40" className="flex min-h-0 flex-col">
        <ActivityList summaries={summaries} selected={traceId} filter={filter} onFilter={setFilter} onSelect={choose} />
        </ResizablePanel>
        <ResizableHandle withHandle aria-label="Resize activity list" />
        <ResizablePanel id="trace" minSize={360} className="flex min-h-0 flex-col">
        <main aria-label="Trace" className="flex min-h-0 flex-1 flex-col">
          {shown ? (
            <>
              <TraceHeader summary={shown.summary} truncated={shown.truncated} />
              <Tabs value={tab} onValueChange={value => setTab(value as 'execution' | 'explain')} className="flex min-h-0 flex-1 flex-col gap-0">
                <TabsList className="mx-4 mt-2">
                  <TabsTrigger value="execution">Execution</TabsTrigger>
                  <TabsTrigger value="explain">Explain</TabsTrigger>
                </TabsList>
                <TabsContent value="execution" className="flex min-h-0 flex-1 flex-col">
                  <ExecutionTree key={shown.summary.traceId} view={shown} selected={spanId} onSelect={setSpanId} slowMs={slowMs} />
                </TabsContent>
                <TabsContent value="explain" className="flex min-h-0 flex-1 flex-col">
                  <ExplainView view={shown} onSelectSpan={showSpan} />
                </TabsContent>
              </Tabs>
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
        </ResizablePanel>
        <ResizableHandle withHandle aria-label="Resize inspector" />
        <ResizablePanel id="inspector" defaultSize="30" minSize={300} maxSize="65" className="flex min-h-0 flex-col">
          <Inspector view={shown} spanId={spanId} info={info} dark={theme.dark} onSelect={setSpanId} />
        </ResizablePanel>
      </ResizablePanelGroup>
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
