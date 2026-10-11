import type { TraceView } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { ScrollArea } from '@/components/ui/scroll-area'
import { cn } from '@/lib/utils'
import { shortId } from '@/model/time'
import { findNode } from '@/model/tree'

// The order of spec §8: entry point → executed code → database and HTTP → slowest → exceptions → visibility gaps.
const sections = [
  { title: 'Entry point', kinds: ['entry', 'request'] },
  { title: 'Executed code', kinds: ['executed'] },
  { title: 'Routing metadata', kinds: ['routing'] },
  { title: 'Database and HTTP', kinds: ['database', 'http'] },
  { title: 'Timing', kinds: ['slowest'] },
  { title: 'Errors', kinds: ['exception'] },
  { title: 'Visibility', kinds: ['visibility', 'sampling'] },
]

const tags: Record<string, { label: string; className?: string }> = {
  entry: { label: 'entry' },
  request: { label: 'request' },
  executed: { label: 'executed', className: 'border-executed/40 text-executed' },
  routing: { label: 'routing metadata', className: 'border-routing/40 bg-routing-soft text-routing' },
  database: { label: 'database' },
  http: { label: 'http' },
  slowest: { label: 'slowest' },
  exception: { label: 'exception', className: 'border-error/40 text-error' },
  visibility: { label: 'not visible', className: 'border-gap/40 text-gap' },
  sampling: { label: 'sampling', className: 'border-gap/40 text-gap' },
}

export function ExplainView({ view, onSelectSpan }: { view: TraceView; onSelectSpan: (spanId: string) => void }) {
  const known = new Set(sections.flatMap(s => s.kinds))
  const groups = [...sections, { title: 'Other', kinds: view.explanation.map(l => l.kind).filter(k => !known.has(k)) }]
    .map(section => ({ title: section.title, lines: view.explanation.filter(l => section.kinds.includes(l.kind)) }))
    .filter(group => group.lines.length > 0)

  return (
    <ScrollArea className="min-h-0 flex-1">
      <div className="max-w-3xl space-y-6 p-5">
        <p className="text-sm text-muted-foreground">
          Built only from recorded spans. Every sentence links to the spans it is based on; nothing unrecorded is stated as fact.
        </p>
        {groups.map(group => (
          <section key={group.title} aria-label={group.title} className="space-y-2">
            <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{group.title}</h3>
            <ul className="space-y-2.5">
              {group.lines.map((line, i) => (
                <li key={i} className="flex gap-2 text-sm">
                  <Badge variant="outline" className={cn('h-fit shrink-0 font-mono', tags[line.kind]?.className)}>
                    {tags[line.kind]?.label ?? line.kind}
                  </Badge>
                  <div className="min-w-0 space-y-1">
                    <p>{line.text}</p>
                    {line.spanIds.length > 0 && (
                      <div className="flex flex-wrap gap-1">
                        {line.spanIds.map(id => (
                          <button
                            key={id}
                            type="button"
                            onClick={() => onSelectSpan(id)}
                            className="rounded border px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground hover:bg-accent hover:text-foreground"
                          >
                            {findNode(view.roots, id)?.span.name ?? shortId(id)}
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </ScrollArea>
  )
}
