import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { App } from '../src/App'
import { ExplainView } from '../src/components/ExplainView'
import { failure, FakeLive, heuristic, stubCollector, success } from './support'

describe('explain', () => {
  it('shows every line the collector wrote, grouped, with its spans', () => {
    render(<ExplainView view={success} onSelectSpan={() => {}} />)

    for (const line of success.explanation) expect(screen.getByText(line.text)).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Entry point' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Database and HTTP' })).toBeInTheDocument()
    expect(screen.getByText(/nothing unrecorded is stated as fact/)).toBeInTheDocument()
  })

  it('labels routing metadata, gaps and exceptions distinctly', () => {
    const { unmount } = render(<ExplainView view={heuristic} onSelectSpan={() => {}} />)
    if (heuristic.explanation.some(l => l.kind === 'routing')) expect(screen.getAllByText('routing metadata').length).toBeGreaterThan(0)
    expect(screen.getAllByText('not visible').length).toBe(heuristic.explanation.filter(l => l.kind === 'visibility').length)
    unmount()

    render(<ExplainView view={failure} onSelectSpan={() => {}} />)
    const errors = screen.getByRole('region', { name: 'Errors' })
    expect(within(errors).getByText(/System\.InvalidOperationException: Out of stock: SKU-42/)).toBeInTheDocument()
  })

  it('keeps lines of kinds it does not know', () => {
    render(<ExplainView view={{ ...success, explanation: [{ kind: 'future-kind', text: 'Something new.', spanIds: [] }] }} onSelectSpan={() => {}} />)
    expect(within(screen.getByRole('region', { name: 'Other' })).getByText('Something new.')).toBeInTheDocument()
  })

  it('a span link selects that span', async () => {
    const onSelectSpan = vi.fn()
    render(<ExplainView view={success} onSelectSpan={onSelectSpan} />)
    const database = success.explanation.find(l => l.kind === 'database')!

    await userEvent.click(within(screen.getByRole('region', { name: 'Database and HTTP' })).getAllByRole('button', { name: 'INSERT orders' })[0])

    expect(onSelectSpan).toHaveBeenCalledWith(database.spanIds[1])
  })

  it('in the app, following a span link switches to the execution tab with it selected', async () => {
    stubCollector()
    render(<App live={new FakeLive()} />)
    await userEvent.click(await screen.findByRole('tab', { name: 'Explain' }))

    const section = await screen.findByRole('region', { name: 'Database and HTTP' })
    await userEvent.click(within(section).getAllByRole('button', { name: 'SELECT products' })[0])

    expect(screen.getByRole('tab', { name: 'Execution' })).toHaveAttribute('aria-selected', 'true')
    await waitFor(() => {
      const item = within(screen.getByRole('tree')).getAllByRole('treeitem').find(i => within(i).queryByText('SELECT products'))!
      expect(item).toHaveAttribute('aria-selected', 'true')
    })
  })
})
