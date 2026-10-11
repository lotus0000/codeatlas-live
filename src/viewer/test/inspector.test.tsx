import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { NodeView, TraceView } from '../src/api/types'
import { Inspector } from '../src/components/Inspector'
import { failure, heuristic, info, success } from './support'

const all = (nodes: NodeView[]): NodeView[] => nodes.flatMap(n => [n, ...all(n.children)])
const byKind = (view: TraceView, kind: string) => all(view.roots).find(n => n.kind === kind)!

describe('inspector', () => {
  it('asks for a selection when nothing is selected', () => {
    render(<Inspector view={success} spanId={null} info={info} dark={false} onSelect={() => {}} />)
    expect(screen.getByText('Select a span to see what it recorded.')).toBeInTheDocument()
  })

  it('shows the facts and attributes of a span', () => {
    const handler = byKind(success, 'handler')
    render(<Inspector view={success} spanId={handler.span.spanId} info={info} dark={false} onSelect={() => {}} />)

    const panel = screen.getByRole('complementary', { name: 'Inspector' })
    expect(within(panel).getByRole('heading', { name: 'Fixture.Orders.LoadAsync' })).toBeInTheDocument()
    expect(within(panel).getByText('52.0 ms')).toBeInTheDocument()
    expect(within(panel).getByText(handler.span.traceId)).toBeInTheDocument()
    expect(within(panel).getByText(`${handler.span.service} · ${handler.span.scope}`)).toBeInTheDocument()
    expect(within(panel).getByText('codeatlas.code.function')).toBeInTheDocument()
  })

  it('a database span says why there is no source and links to the method it ran in', async () => {
    const onSelect = vi.fn()
    const database = byKind(success, 'database')
    render(<Inspector view={success} spanId={database.span.spanId} info={info} dark={false} onSelect={onSelect} />)

    expect(screen.getByText(/A database operation has no method identity/)).toBeInTheDocument()
    expect(screen.getByText('SQL text, parameters and connection details are never exported.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Fixture.Orders.LoadAsync' }))
    expect(onSelect).toHaveBeenCalledWith(byKind(success, 'handler').span.spanId)
  })

  it('shows recorded exceptions with their stack trace', () => {
    const handler = byKind(failure, 'handler')
    render(<Inspector view={failure} spanId={handler.span.spanId} info={info} dark={false} onSelect={() => {}} />)

    const exception = screen.getByRole('region', { name: 'Exception' })
    expect(within(exception).getByText('System.InvalidOperationException')).toBeInTheDocument()
    expect(within(exception).getByText('Out of stock: SKU-42')).toBeInTheDocument()
    expect(within(exception).getByText(/at Fixture\.Stock\.Reserve/)).toBeInTheDocument()
    expect(within(exception).getByText(/appear only as frames in this recorded stack trace/)).toBeInTheDocument()
  })

  it('routing evidence is labelled as such, and a missing parent is named', () => {
    const endpoint = byKind(success, 'endpoint')
    const { unmount } = render(<Inspector view={success} spanId={endpoint.span.spanId} info={info} dark={false} onSelect={() => {}} />)
    expect(screen.getByText('route → Orders.LoadAsync')).toBeInTheDocument()
    unmount()

    const orphan = heuristic.roots.find(r => r.gaps.some(g => g.kind === 'missing-parent'))!
    render(<Inspector view={heuristic} spanId={orphan.span.spanId} info={info} dark={false} onSelect={() => {}} />)
    expect(screen.getByText(`${orphan.span.parentSpanId} (not received)`)).toBeInTheDocument()
  })

  it('a span with a method identity shows its source panel', async () => {
    const { stubApi, sourceFound } = await import('./support')
    stubApi({ 'api/source?': sourceFound })
    const handler = byKind(success, 'handler')
    render(<Inspector view={success} spanId={handler.span.spanId} info={info} dark={false} onSelect={() => {}} />)
    expect(await screen.findByTestId('monaco')).toBeInTheDocument()
  })
})
