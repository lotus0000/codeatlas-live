import { ChevronRight, TriangleAlert } from 'lucide-react'
import { useState, type KeyboardEvent } from 'react'
import type { TraceView } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { ScrollArea } from '@/components/ui/scroll-area'
import { cn } from '@/lib/utils'
import { formatMs } from '@/model/time'
import { rows, totalMs, traceOrigin, type Row } from '@/model/tree'
import { CorrelationBadge, GapNote, KindTag, RoutingChip } from './badges'

const MAX_INDENT_LEVELS = 24
const columns = 'grid grid-cols-[minmax(0,1fr)_minmax(140px,36%)_72px] gap-x-3'

export function ExecutionTree({ view, selected, onSelect, slowMs }: {
  view: TraceView
  selected: string | null
  onSelect: (spanId: string) => void
  slowMs: number
}) {
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set())
  const total = Math.max(totalMs(view.roots, traceOrigin(view.roots)), 0.001)
  const list = rows(view.roots, collapsed, slowMs)
  const toggle = (spanId: string) =>
    setCollapsed(current => {
      const next = new Set(current)
      if (next.has(spanId)) next.delete(spanId)
      else next.add(spanId)
      return next
    })

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <Legend />
      <div className={cn(columns, 'border-b px-4 py-1.5 text-xs text-muted-foreground')}>
        <span>Span</span>
        <span className="flex justify-between font-mono">
          {[0, 0.25, 0.5, 0.75, 1].map(f => (
            <span key={f}>{f === 1 ? `${Math.round(total)} ms` : Math.round(total * f)}</span>
          ))}
        </span>
        <span className="text-right">Duration</span>
      </div>
      <ScrollArea className="min-h-0 flex-1">
        <div role="tree" aria-label="Execution tree" className="py-1">
          {list.map(row => (
            <TreeRow
              key={row.node.span.spanId}
              row={row}
              total={total}
              selected={row.node.span.spanId === selected}
              collapsed={collapsed.has(row.node.span.spanId)}
              onToggle={toggle}
              onSelect={onSelect}
            />
          ))}
        </div>
      </ScrollArea>
    </div>
  )
}

function Legend() {
  return (
    <div aria-label="Evidence legend" className="flex flex-wrap items-center gap-x-4 gap-y-1 border-b px-4 py-2 text-xs text-muted-foreground">
      <span className="flex items-center gap-1.5">
        <span className="h-2 w-4 rounded-sm bg-bar" aria-hidden />
        Executed
      </span>
      <span className="flex items-center gap-1.5">
        <Badge variant="outline" className="border-routing/40 bg-routing-soft text-routing">route</Badge>
        Routing metadata
      </span>
      <span className="flex items-center gap-1.5">
        <CorrelationBadge kind="heuristic" />
        Linked by timing
      </span>
      <span className="flex items-center gap-1.5 text-gap">
        <TriangleAlert className="size-3" aria-hidden />
        Not visible
      </span>
    </div>
  )
}

function TreeRow({ row, total, selected, collapsed, onToggle, onSelect }: {
  row: Row
  total: number
  selected: boolean
  collapsed: boolean
  onToggle: (spanId: string) => void
  onSelect: (spanId: string) => void
}) {
  const { node } = row
  const spanId = node.span.spanId
  const heuristic = node.correlation === 'heuristic'
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      onSelect(spanId)
    }
  }

  return (
    <div
      role="treeitem"
      tabIndex={0}
      data-span-id={spanId}
      aria-level={row.depth + 1}
      aria-selected={selected}
      aria-expanded={row.hasChildren ? !collapsed : undefined}
      onClick={() => onSelect(spanId)}
      onKeyDown={onKeyDown}
      className={cn(columns, 'cursor-pointer items-start px-4 py-1 text-sm outline-none hover:bg-accent/60 focus-visible:bg-accent', selected && 'bg-accent')}
    >
      <div className="min-w-0" style={{ paddingLeft: Math.min(row.depth, MAX_INDENT_LEVELS) * 16 }}>
        <div className={cn('flex min-w-0 items-center gap-1.5', heuristic && 'border-l border-dashed border-heuristic pl-1.5')}>
          {row.hasChildren ? (
            <button
              type="button"
              aria-label={collapsed ? 'Expand' : 'Collapse'}
              onClick={event => {
                event.stopPropagation()
                onToggle(spanId)
              }}
              className="text-muted-foreground hover:text-foreground"
            >
              <ChevronRight className={cn('size-3.5 transition-transform', !collapsed && 'rotate-90')} />
            </button>
          ) : (
            <span className="w-3.5 shrink-0" />
          )}
          <KindTag kind={node.kind} />
          <span className={cn('truncate', row.error && 'text-error')} title={node.span.name}>
            {node.span.name}
          </span>
          {node.code?.evidence === 'routing' && <RoutingChip fn={node.code.function} />}
          {heuristic && <CorrelationBadge kind="heuristic" />}
          {row.error && (
            <Badge variant="outline" className="border-error/40 text-error">
              error
            </Badge>
          )}
        </div>
        {node.gaps.map(gap => (
          <div key={gap.kind} className="pl-5">
            <GapNote gap={gap} />
          </div>
        ))}
      </div>
      <div className="relative mt-1.5 h-2.5" aria-hidden>
        <div
          className={cn('absolute h-full rounded-sm', row.error ? 'bg-error' : row.slow ? 'bg-slow' : 'bg-bar', heuristic && 'outline-1 outline-dashed outline-heuristic')}
          style={{ left: `${(row.offsetMs / total) * 100}%`, width: `max(2px, ${(row.durationMs / total) * 100}%)` }}
        />
      </div>
      <span className={cn('text-right font-mono text-xs', row.slow ? 'text-slow' : 'text-muted-foreground')}>
        {formatMs(row.durationMs)}
        {row.slow && <span className="sr-only"> (slow)</span>}
      </span>
    </div>
  )
}
