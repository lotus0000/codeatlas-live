import type { NodeView } from '@/api/types'
import { offsetMs } from './time'

export interface Row {
  node: NodeView
  depth: number
  hasChildren: boolean
  offsetMs: number
  durationMs: number
  error: boolean
  slow: boolean
}

function* walk(nodes: NodeView[]): Generator<NodeView> {
  for (const node of nodes) {
    yield node
    yield* walk(node.children)
  }
}

export function traceOrigin(roots: NodeView[]): bigint {
  let origin: bigint | undefined
  for (const node of walk(roots)) {
    const start = BigInt(node.span.startUnixNano)
    if (origin === undefined || start < origin) origin = start
  }
  return origin ?? 0n
}

export function totalMs(roots: NodeView[], origin: bigint): number {
  let end = origin
  for (const node of walk(roots)) {
    const value = BigInt(node.span.endUnixNano)
    if (value > end) end = value
  }
  return Number(end - origin) / 1_000_000
}

export function isError(node: NodeView): boolean {
  const status = Number(node.span.attributes['http.response.status_code'] ?? 0)
  return node.span.status === 'error' || status >= 500 || node.span.events.some(e => e.name === 'exception')
}

const byStart = (a: NodeView, b: NodeView) => {
  const difference = BigInt(a.span.startUnixNano) - BigInt(b.span.startUnixNano)
  return difference < 0n ? -1 : difference > 0n ? 1 : 0
}

/** Pre-order rows, children in start order; collapsed nodes keep their row but hide descendants. */
export function rows(roots: NodeView[], collapsed: ReadonlySet<string>, slowMs: number): Row[] {
  const origin = traceOrigin(roots)
  const result: Row[] = []
  const visit = (nodes: NodeView[], depth: number) => {
    for (const node of [...nodes].sort(byStart)) {
      result.push({
        node,
        depth,
        hasChildren: node.children.length > 0,
        offsetMs: offsetMs(node.span.startUnixNano, origin),
        durationMs: node.span.durationMs,
        error: isError(node),
        slow: node.span.durationMs >= slowMs,
      })
      if (!collapsed.has(node.span.spanId)) visit(node.children, depth + 1)
    }
  }
  visit(roots, 0)
  return result
}

export function findNode(roots: NodeView[], spanId: string): NodeView | undefined {
  for (const node of walk(roots)) if (node.span.spanId === spanId) return node
  return undefined
}

/** The chain from a root down to the node's parent. Empty for roots. */
export function ancestors(roots: NodeView[], spanId: string): NodeView[] {
  const search = (nodes: NodeView[], path: NodeView[]): NodeView[] | undefined => {
    for (const node of nodes) {
      if (node.span.spanId === spanId) return path
      const found = search(node.children, [...path, node])
      if (found) return found
    }
    return undefined
  }
  return search(roots, []) ?? []
}

/** The first executed method if there is one (what the developer usually wants), else the first server span, else the first root. */
export function defaultSelection(roots: NodeView[]): string | undefined {
  const all = [...walk(roots)]
  return (all.find(n => n.code?.evidence === 'executed') ?? all.find(n => n.kind === 'server') ?? all[0])?.span.spanId
}
