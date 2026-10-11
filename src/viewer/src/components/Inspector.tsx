import { Fragment, type ReactNode } from 'react'
import type { CollectorInfo, NodeView, SpanEvent, TraceView } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { ScrollArea } from '@/components/ui/scroll-area'
import { cn } from '@/lib/utils'
import { clock, formatMs } from '@/model/time'
import { ancestors, findNode, isError } from '@/model/tree'
import { CorrelationBadge, GapNote, KindTag, RoutingChip } from './badges'
import { SourcePanel } from './SourcePanel'

export function Inspector({ view, spanId, info, dark, onSelect }: {
  view: TraceView | null
  spanId: string | null
  info: CollectorInfo | null
  dark: boolean
  onSelect: (spanId: string) => void
}) {
  const node = view && spanId ? findNode(view.roots, spanId) : undefined
  if (!view || !node) {
    return (
      <aside aria-label="Inspector" className="p-6 text-sm text-muted-foreground">
        Select a span to see what it recorded.
      </aside>
    )
  }
  const span = node.span
  const parent = span.parentSpanId ? findNode(view.roots, span.parentSpanId) : undefined
  // Only a method that is recorded as executed is named as where this span ran.
  const owner = ancestors(view.roots, span.spanId).reverse().find(a => a.code?.evidence === 'executed')

  return (
    <aside aria-label="Inspector" className="flex min-h-0 flex-col">
      <ScrollArea className="min-h-0 flex-1">
        <div className="space-y-5 p-4">
          <div className="space-y-1.5">
            <div className="flex flex-wrap items-center gap-1.5">
              <KindTag kind={node.kind} />
              <Badge variant="outline" className="border-executed/40 text-executed">executed</Badge>
              {node.code?.evidence === 'routing' && <RoutingChip fn={node.code.function} />}
              {node.correlation && <CorrelationBadge kind={node.correlation} />}
              {isError(node) && <Badge variant="outline" className="border-error/40 text-error">error</Badge>}
            </div>
            <h2 className="break-words font-mono text-sm font-medium">{node.code?.function ?? span.name}</h2>
          </div>

          <dl className="grid grid-cols-[96px_minmax(0,1fr)] gap-x-3 gap-y-1.5 text-xs">
            <Fact label="Duration">{formatMs(span.durationMs)}</Fact>
            <Fact label="Start → end">{clock(span.startUnixNano, true)} → {clock(span.endUnixNano, true)}</Fact>
            <Fact label="Trace" mono>{span.traceId}</Fact>
            <Fact label="Span" mono>{span.spanId}</Fact>
            <Fact label="Parent" mono>
              {!span.parentSpanId ? (
                'root'
              ) : parent ? (
                <button type="button" className="underline underline-offset-2" onClick={() => onSelect(parent.span.spanId)}>
                  {span.parentSpanId}
                </button>
              ) : (
                `${span.parentSpanId} (not received)`
              )}
            </Fact>
            <Fact label="Service">{`${span.service} · ${span.scope}`}</Fact>
            <Fact label="Status">{span.statusMessage ? `${span.status}: ${span.statusMessage}` : span.status}</Fact>
          </dl>

          {node.gaps.length > 0 && (
            <section aria-label="Visibility" className="space-y-1">
              {node.gaps.map(gap => <GapNote key={gap.kind} gap={gap} />)}
            </section>
          )}

          {span.events.filter(e => e.name === 'exception').map((event, i) => <ExceptionBlock key={i} event={event} />)}

          <section aria-label="Source">
            {node.code ? <SourcePanel code={node.code} info={info} dark={dark} /> : <NoSource node={node} owner={owner} onSelect={onSelect} />}
          </section>

          <Attributes attributes={span.attributes} />
        </div>
      </ScrollArea>
    </aside>
  )
}

function Fact({ label, mono, children }: { label: string; mono?: boolean; children: ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className={cn('break-all', mono && 'font-mono')}>{children}</dd>
    </>
  )
}


function NoSource({ node, owner, onSelect }: { node: NodeView; owner: NodeView | undefined; onSelect: (spanId: string) => void }) {
  return (
    <div className="space-y-2 text-xs text-muted-foreground">
      <p>
        {node.kind === 'database'
          ? 'A database operation has no method identity, so there is no source to show.'
          : 'This span has no method identity, so there is no source to show.'}
        {owner && (
          <>
            {' It ran inside '}
            <button type="button" className="font-mono text-foreground underline underline-offset-2" onClick={() => onSelect(owner.span.spanId)}>
              {owner.code!.function}
            </button>
            ; select it to see its code.
          </>
        )}
      </p>
      {node.kind === 'database' && <p>SQL text, parameters and connection details are never exported.</p>}
    </div>
  )
}

function ExceptionBlock({ event }: { event: SpanEvent }) {
  const a = event.attributes
  const stack = a['exception.stacktrace']
  return (
    <section aria-label="Exception" className="space-y-1.5 rounded-md border border-error/40 bg-error-soft p-3">
      <p className="font-mono text-xs font-medium text-error">{String(a['exception.type'] ?? 'Exception')}</p>
      {a['exception.message'] != null && <p className="text-sm">{String(a['exception.message'])}</p>}
      {stack != null && (
        <>
          <pre className="max-h-48 overflow-auto whitespace-pre-wrap font-mono text-[11px] text-muted-foreground">{String(stack)}</pre>
          <p className="text-xs text-muted-foreground">Methods without a span of their own appear only as frames in this recorded stack trace.</p>
        </>
      )}
    </section>
  )
}

function Attributes({ attributes }: { attributes: Record<string, unknown> }) {
  const entries = Object.entries(attributes).sort(([a], [b]) => a.localeCompare(b))
  if (entries.length === 0) return null
  return (
    <section aria-label="Attributes">
      <h3 className="mb-1.5 text-xs font-medium text-muted-foreground">Attributes</h3>
      <dl className="grid grid-cols-[minmax(0,42%)_minmax(0,1fr)] gap-x-3 gap-y-1 font-mono text-[11px]">
        {entries.map(([key, value]) => (
          <Fragment key={key}>
            <dt className="truncate text-muted-foreground" title={key}>{key}</dt>
            <dd className="break-all">{typeof value === 'string' ? value : JSON.stringify(value)}</dd>
          </Fragment>
        ))}
      </dl>
    </section>
  )
}
