import { TriangleAlert } from 'lucide-react'
import type { Gap, TraceSummary } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { statusTone } from '@/model/activity'
import { cn } from '@/lib/utils'

/** "Shop.OrderEndpoints.Create" → "OrderEndpoints.Create". */
export const shortFunction = (fn: string) => fn.split('.').slice(-2).join('.')

export function CorrelationBadge({ kind }: { kind: string }) {
  return kind === 'heuristic' ? (
    <Badge variant="outline" title="Linked by timing, not proven" className="border-dashed border-heuristic text-heuristic">
      heuristic
    </Badge>
  ) : (
    <Badge variant="outline" className="text-muted-foreground">
      {kind}
    </Badge>
  )
}

export function RoutingChip({ fn }: { fn: string }) {
  return (
    <Badge
      variant="outline"
      title="Routing metadata: the route points here; whether it ran is not recorded"
      className="border-routing/40 bg-routing-soft font-mono text-routing"
    >
      route → {shortFunction(fn)}
    </Badge>
  )
}

export function KindTag({ kind }: { kind: string }) {
  return <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{kind}</span>
}

export function StatusText({ summary }: { summary: TraceSummary }) {
  const tone = statusTone(summary)
  return (
    <span
      className={cn(
        'font-mono text-xs',
        tone === 'error' && 'text-error',
        tone === 'warn' && 'text-heuristic',
        tone === 'ok' && 'text-success',
        tone === 'unknown' && 'text-muted-foreground',
      )}
    >
      {summary.statusCode ?? (summary.hasError ? 'error' : '—')}
    </span>
  )
}

export function GapNote({ gap }: { gap: Gap }) {
  return (
    <p className="flex gap-1.5 text-xs text-gap">
      <TriangleAlert className="mt-0.5 size-3 shrink-0" aria-hidden />
      <span>
        <span className="sr-only">Not visible: </span>
        {gap.message}
      </span>
    </p>
  )
}
