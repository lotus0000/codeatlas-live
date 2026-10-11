import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { NodeView, TraceView } from '../src/api/types'
import { ExecutionTree } from '../src/components/ExecutionTree'
import { failure, heuristic, success } from './support'

const items = () => screen.getAllByRole('treeitem')

describe('execution tree', () => {
  it('shows one row per span, with kinds, and routing as a chip, never as a node', () => {
    render(<ExecutionTree view={success} selected={null} onSelect={() => {}} slowMs={200} />)

    expect(items()).toHaveLength(8)
    const endpoint = items()[3]
    expect(within(endpoint).getByText('endpoint')).toBeInTheDocument()
    expect(within(endpoint).getByText('route → Orders.LoadAsync')).toBeInTheDocument()
    expect(screen.getByLabelText('Evidence legend')).toHaveTextContent(/Executed.*Routing metadata.*Linked by timing.*Not visible/)
  })

  it('collapses and expands', async () => {
    render(<ExecutionTree view={success} selected={null} onSelect={() => {}} slowMs={200} />)
    const handler = items()[4]

    await userEvent.click(within(handler).getByRole('button', { name: 'Collapse' }))
    expect(items()).toHaveLength(5)
    expect(handler).toHaveAttribute('aria-expanded', 'false')

    await userEvent.click(within(items()[4]).getByRole('button', { name: 'Expand' }))
    expect(items()).toHaveLength(8)
  })

  it('selects with click and keyboard', async () => {
    const onSelect = vi.fn()
    render(<ExecutionTree view={success} selected={null} onSelect={onSelect} slowMs={200} />)

    await userEvent.click(items()[5])
    items()[6].focus()
    await userEvent.keyboard('{Enter}')

    expect(onSelect.mock.calls.map(c => c[0])).toEqual([items()[5].dataset.spanId, items()[6].dataset.spanId])
  })

  it('shows heuristic links and every gap exactly as the collector worded it', () => {
    render(<ExecutionTree view={heuristic} selected={null} onSelect={() => {}} slowMs={200} />)

    const all = (nodes: NodeView[]): NodeView[] => nodes.flatMap(n => [n, ...all(n.children)])
    const gaps = all(heuristic.roots).flatMap(n => n.gaps)
    expect(gaps.map(g => g.kind)).toEqual(expect.arrayContaining(['missing-parent', 'no-server-span', 'handler-not-recorded']))
    for (const gap of gaps) expect(screen.getAllByText(gap.message).length).toBeGreaterThan(0)
    expect(screen.getAllByText('heuristic').length).toBeGreaterThanOrEqual(3)
  })

  it('gaps in the tree are short labels that carry the full wording', () => {
    render(<ExecutionTree view={heuristic} selected={null} onSelect={() => {}} slowMs={200} />)

    const all = (nodes: NodeView[]): NodeView[] => nodes.flatMap(n => [n, ...all(n.children)])
    const gap = all(heuristic.roots).flatMap(n => n.gaps).find(g => g.kind === 'handler-not-recorded')!
    const label = screen.getByText('handler not recorded')
    expect(label.closest('[title]')).toHaveAttribute('title', gap.message)
    expect(screen.getAllByText('time not covered').length).toBeGreaterThan(0)
  })

  it('marks errors and slow spans', () => {
    render(<ExecutionTree view={failure} selected={null} onSelect={() => {}} slowMs={20} />)

    const handler = items().find(i => within(i).queryByText('handler'))!
    expect(within(handler).getByText('error')).toBeInTheDocument()
    expect(screen.getAllByText('(slow)').length).toBeGreaterThan(0)
  })

  it('deep chains cap their indentation', () => {
    const base = success.roots[0]
    let node: NodeView = { ...base, children: [] }
    for (let i = 0; i < 300; i++) node = { ...base, span: { ...base.span, spanId: `s${i}` }, children: [node] }
    const deep: TraceView = { ...success, roots: [node] }

    render(<ExecutionTree view={deep} selected={null} onSelect={() => {}} slowMs={200} />)

    const last = items().at(-1)!
    expect(last).toHaveAttribute('aria-level', '301')
    expect((last.firstElementChild as HTMLElement).style.paddingLeft).toBe('384px')
  })
})
