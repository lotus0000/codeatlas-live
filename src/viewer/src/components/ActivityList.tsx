import type { TraceSummary } from '@/api/types'
import { Input } from '@/components/ui/input'
import { ScrollArea } from '@/components/ui/scroll-area'
import { cn } from '@/lib/utils'
import { hasServer, matches } from '@/model/activity'
import { clock, formatMs, shortId } from '@/model/time'
import { CorrelationBadge, StatusText } from './badges'

export function ActivityList({ summaries, selected, filter, onFilter, onSelect }: {
  summaries: TraceSummary[]
  selected: string | null
  filter: string
  onFilter: (filter: string) => void
  onSelect: (traceId: string) => void
}) {
  const visible = summaries.filter(s => matches(s, filter))
  return (
    <aside aria-label="Recent activity" className="flex min-h-0 flex-col">
      <div className="flex items-baseline justify-between px-4 pt-3 pb-2">
        <h2 className="text-sm font-medium">Recent activity</h2>
        <span className="text-xs text-muted-foreground">
          {summaries.length} {summaries.length === 1 ? 'trace' : 'traces'}
        </span>
      </div>
      <div className="px-3 pb-2">
        <Input aria-label="Filter traces" placeholder="Filter traces" value={filter} onChange={e => onFilter(e.target.value)} className="h-8" />
      </div>
      <ScrollArea className="min-h-0 flex-1">
        {visible.length === 0 ? (
          <p className="px-4 py-6 text-sm text-muted-foreground">
            {summaries.length === 0
              ? 'Waiting for traces. Use your app; requests sent to this collector appear here.'
              : 'No trace matches this filter.'}
          </p>
        ) : (
          <ul className="space-y-0.5 px-2 pb-2">
            {visible.map(s => (
              <li key={s.traceId}>
                <button
                  type="button"
                  aria-current={s.traceId === selected ? 'true' : undefined}
                  onClick={() => onSelect(s.traceId)}
                  className={cn(
                    'grid w-full grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-x-2 gap-y-0.5 rounded-md px-2 py-2 text-left text-sm hover:bg-accent',
                    s.traceId === selected && 'bg-accent',
                  )}
                >
                  <span className="font-mono text-xs text-muted-foreground">{clock(s.startUnixNano)}</span>
                  <span className="flex min-w-0 items-center gap-1.5">
                    <span className="truncate">{s.interaction ?? 'No interaction'}</span>
                    {s.correlation === 'heuristic' && <CorrelationBadge kind="heuristic" />}
                  </span>
                  <span className="text-right font-mono text-xs text-muted-foreground">{formatMs(s.durationMs)}</span>
                  <span />
                  <span className="truncate font-mono text-xs text-muted-foreground">
                    {hasServer(s) ? `${s.method ?? ''} ${s.route ?? ''}`.trim() : 'no server span'}
                  </span>
                  <span className="text-right">
                    <StatusText summary={s} />
                  </span>
                  <span />
                  <span className="col-span-2 truncate font-mono text-[11px] text-muted-foreground" title={s.traceId}>
                    {shortId(s.traceId)}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </ScrollArea>
    </aside>
  )
}
