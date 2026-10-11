import { describe, expect, it } from 'vitest'
import type { NodeView, TraceSummary } from '../src/api/types'
import { followTarget, hasServer, matches, MAX_TRACES, mergeSummaries, statusTone } from '../src/model/activity'
import { clock, formatMs, offsetMs, shortId } from '../src/model/time'
import { ancestors, defaultSelection, findNode, isError, rows, totalMs, traceOrigin } from '../src/model/tree'
import { failure, heuristic, success, summaries } from './support'

const summary = (traceId: string, startUnixNano: string, server = true): TraceSummary => ({
  traceId, startUnixNano, durationMs: 1, interaction: null, correlation: null,
  method: server ? 'GET' : null, route: server ? '/x' : null, statusCode: server ? 200 : null,
  hasError: false, spanCount: 1, sampling: 'codeatlas',
})

describe('time', () => {
  it('offsets keep microsecond precision at real epochs', () => {
    const origin = BigInt('1700000000123456789')
    expect(offsetMs('1700000000123457789', origin)).toBe(0.001)
    expect(offsetMs('1700000000223456789', origin)).toBe(100)
  })

  it('formats durations, clock times and ids', () => {
    expect(formatMs(95.34)).toBe('95.3 ms')
    expect(formatMs(1534)).toBe('1.53 s')
    expect(clock('1700000000066000000')).toBe('22:13:20')
    expect(clock('1700000000066000000', true)).toBe('22:13:20.066')
    expect(shortId('4bf92f3577b34da6a3ce929d0e0e4736')).toBe('4bf92f35…0e4736')
    expect(shortId('00f067aa0ba902b7')).toBe('00f067aa0ba902b7')
  })
})

describe('activity', () => {
  it('merges by trace id, newest first, replacing older copies', () => {
    const merged = mergeSummaries([summary('a', '100'), summary('b', '200')], [{ ...summary('a', '100'), spanCount: 9 }, summary('c', '150')])
    expect(merged.map(s => s.traceId)).toEqual(['b', 'c', 'a'])
    expect(merged.find(s => s.traceId === 'a')!.spanCount).toBe(9)
  })

  it(`keeps at most ${MAX_TRACES} traces`, () => {
    const many = Array.from({ length: MAX_TRACES + 20 }, (_, i) => summary(`t${i}`, String(1000 + i)))
    const merged = mergeSummaries([], many)
    expect(merged).toHaveLength(MAX_TRACES)
    expect(merged[0].traceId).toBe(`t${MAX_TRACES + 19}`)
  })

  it('follows the newest trace that reached the backend', () => {
    expect(followTarget(mergeSummaries([], [summary('old', '1'), summary('browser-only', '3', false), summary('new', '2')]))).toBe('new')
    expect(followTarget([summary('browser-only', '3', false)])).toBeNull()
    expect(hasServer(summaries.find(s => s.traceId === heuristic.summary.traceId)!)).toBe(true)
  })

  it('filters on interaction, request, status and trace id', () => {
    const s = success.summary
    expect(matches(s, 'create')).toBe(true)
    expect(matches(s, '/api/orders')).toBe(true)
    expect(matches(s, '201')).toBe(true)
    expect(matches(s, s.traceId.slice(0, 6))).toBe(true)
    expect(matches(s, 'nothing-like-this')).toBe(false)
    expect(matches(s, '  ')).toBe(true)
  })

  it('status tone reflects errors, client errors and missing status', () => {
    expect(statusTone(success.summary)).toBe('ok')
    expect(statusTone(failure.summary)).toBe('error')
    expect(statusTone({ ...success.summary, statusCode: 404 })).toBe('warn')
    expect(statusTone(summary('x', '1', false))).toBe('unknown')
  })
})

describe('tree', () => {
  it('flattens in time order with depth, offsets and the total', () => {
    const origin = traceOrigin(success.roots)
    const list = rows(success.roots, new Set(), 200)
    expect(list.map(r => [r.node.kind, r.depth])).toEqual([
      ['interaction', 0], ['browser-request', 1], ['server', 2], ['endpoint', 3], ['handler', 4],
      ['database', 5], ['database', 5], ['http-client', 5],
    ])
    expect(list[0].offsetMs).toBe(0)
    expect(list[4].offsetMs).toBe(4)
    expect(totalMs(success.roots, origin)).toBe(62)
  })

  it('collapsing a node hides its descendants only', () => {
    const handler = rows(success.roots, new Set(), 200)[4].node
    const list = rows(success.roots, new Set([handler.span.spanId]), 200)
    expect(list.map(r => r.node.kind)).toEqual(['interaction', 'browser-request', 'server', 'endpoint', 'handler'])
    expect(list[4].hasChildren).toBe(true)
  })

  it('marks errors (status, exception, 5xx) and slow spans', () => {
    const list = rows(failure.roots, new Set(), 20)
    const byKind = (kind: string) => list.find(r => r.node.kind === kind)!
    expect(byKind('handler').error).toBe(true)
    expect(byKind('server').error).toBe(true)
    expect(byKind('database').error).toBe(false)
    expect(byKind('browser-request').slow).toBe(true)
    expect(byKind('database').slow).toBe(false)
    expect(isError(byKind('handler').node)).toBe(true)
  })

  it('finds nodes, their ancestors, and a sensible default selection', () => {
    const handler = rows(success.roots, new Set(), 200)[4].node
    const database = rows(success.roots, new Set(), 200)[5].node
    expect(findNode(success.roots, handler.span.spanId)).toBe(handler)
    expect(ancestors(success.roots, database.span.spanId).map(n => n.kind)).toEqual(['interaction', 'browser-request', 'server', 'endpoint', 'handler'])
    expect(defaultSelection(success.roots)).toBe(handler.span.spanId)
    // No executed method in the heuristic trace: the first server span is selected.
    expect(findNode(heuristic.roots, defaultSelection(heuristic.roots)!)!.kind).toBe('server')
  })

  it('stays fast on a 2,000-deep chain', () => {
    const base = success.roots[0]
    let node: NodeView = { ...base, children: [] }
    for (let i = 0; i < 2000; i++) node = { ...base, span: { ...base.span, spanId: `s${i}` }, children: [node] }
    const roots = [node]

    const started = performance.now()
    const origin = traceOrigin(roots)
    totalMs(roots, origin)
    rows(roots, new Set(), 200)
    findNode(roots, base.span.spanId)
    ancestors(roots, base.span.spanId)
    defaultSelection(roots)
    expect(performance.now() - started).toBeLessThan(60)
  })

  it('handles nodes whose parent never arrived as extra roots', () => {
    expect(heuristic.roots.length).toBeGreaterThan(1)
    const orphan = heuristic.roots.find(r => r.gaps.some(g => g.kind === 'missing-parent'))!
    expect(ancestors(heuristic.roots, orphan.span.spanId)).toEqual([])
  })
})
