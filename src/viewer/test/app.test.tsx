import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { App } from '../src/App'
import { failure, FakeLive, heuristic, stubCollector, success, summaries } from './support'

const activity = () => screen.getByRole('complementary', { name: 'Recent activity' })
const row = (text: string) => within(activity()).getAllByRole('button').find(b => b.textContent?.includes(text))!
const header = () => screen.getByRole('main', { name: 'Trace' })

describe('app', () => {
  it('lists recent activity and follows the newest trace that reached the backend', async () => {
    stubCollector()
    render(<App live={new FakeLive()} />)

    await waitFor(() => expect(within(activity()).getByText(`${summaries.length} traces`)).toBeInTheDocument())
    await waitFor(() => expect(within(header()).getByText('Refresh')).toBeInTheDocument())
    expect(screen.getByText('CodeAtlas Live')).toBeInTheDocument()
    expect(await screen.findByText('v0.1.0')).toBeInTheDocument()
    expect(row('Refresh')).toHaveAttribute('aria-current', 'true')
  })

  it('choosing a trace pins it until Follow latest is turned back on', async () => {
    stubCollector()
    const live = new FakeLive()
    render(<App live={live} />)
    await waitFor(() => expect(within(header()).getByText('Refresh')).toBeInTheDocument())

    await userEvent.click(row('500'))
    await waitFor(() => expect(within(header()).getByText('500')).toBeInTheDocument())
    expect(screen.getByRole('button', { name: /Follow latest/ })).toHaveAttribute('aria-pressed', 'false')

    act(() => live.update([{ ...heuristic.summary, spanCount: heuristic.summary.spanCount + 1 }]))
    expect(within(header()).getByText('500')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /Follow latest/ }))
    await waitFor(() => expect(within(header()).getByText('Refresh')).toBeInTheDocument())
  })

  it('a live update of the selected trace refetches it and keeps the selected span', async () => {
    const calls = stubCollector()
    const live = new FakeLive()
    render(<App live={live} />)
    await waitFor(() => expect(row('201')).toBeDefined())
    await userEvent.click(row('201'))
    const tree = await screen.findByRole('tree', { name: 'Execution tree' })
    const database = within(tree).getAllByRole('treeitem').find(i => within(i).queryByText('INSERT orders'))!
    await userEvent.click(database)
    const fetchesBefore = calls.filter(c => c.url.includes(`api/traces/${success.summary.traceId}`)).length

    act(() => live.update([success.summary]))

    await waitFor(() => expect(calls.filter(c => c.url.includes(`api/traces/${success.summary.traceId}`)).length).toBe(fetchesBefore + 1))
    await waitFor(() => {
      const again = within(screen.getByRole('tree', { name: 'Execution tree' })).getAllByRole('treeitem').find(i => within(i).queryByText('INSERT orders'))!
      expect(again).toHaveAttribute('aria-selected', 'true')
    })
  })

  it('reconnecting reloads the list', async () => {
    const calls = stubCollector()
    const live = new FakeLive()
    render(<App live={live} />)
    await waitFor(() => expect(within(activity()).getByText(`${summaries.length} traces`)).toBeInTheDocument())
    const listFetches = () => calls.filter(c => c.url.includes('api/traces?')).length
    const before = listFetches()

    act(() => live.state('offline'))
    expect(screen.getByRole('status', { name: 'Live connection' })).toHaveTextContent('Offline')
    act(() => live.state('live'))

    expect(screen.getByRole('status', { name: 'Live connection' })).toHaveTextContent('Live')
    await waitFor(() => expect(listFetches()).toBe(before + 1))
  })

  it('a late response for a previously selected trace is ignored', async () => {
    let release: () => void = () => {}
    const late = new Promise<void>(resolve => { release = resolve })
    stubCollector({
      [`api/traces/${failure.summary.traceId}`]: async () => {
        await late
        return Response.json(failure)
      },
    })
    render(<App live={new FakeLive()} />)
    await waitFor(() => expect(within(header()).getByText('Refresh')).toBeInTheDocument())

    await userEvent.click(row('500'))
    await userEvent.click(row('201'))
    await waitFor(() => expect(within(header()).getByText('201')).toBeInTheDocument())
    await act(async () => { release(); await late })

    expect(within(header()).getByText('201')).toBeInTheDocument()
    expect(within(header()).queryByText('500')).toBeNull()
  })

  it('an evicted trace says so', async () => {
    stubCollector({ [`api/traces/${heuristic.summary.traceId}`]: () => new Response('', { status: 404 }) })
    render(<App live={new FakeLive()} />)

    expect(await screen.findByText(/no longer in the collector/)).toBeInTheDocument()
  })

  it('filters the list', async () => {
    stubCollector()
    render(<App live={new FakeLive()} />)
    await waitFor(() => expect(within(activity()).getByText(`${summaries.length} traces`)).toBeInTheDocument())

    await userEvent.type(within(activity()).getByRole('textbox', { name: 'Filter traces' }), 'refresh')

    expect(within(activity()).getAllByRole('button')).toHaveLength(1)
    await userEvent.clear(within(activity()).getByRole('textbox', { name: 'Filter traces' }))
    await userEvent.type(within(activity()).getByRole('textbox', { name: 'Filter traces' }), 'zzz')
    expect(within(activity()).getByText('No trace matches this filter.')).toBeInTheDocument()
  })

  it('the theme button cycles system → light → dark', async () => {
    stubCollector()
    render(<App live={new FakeLive()} />)

    await userEvent.click(screen.getByRole('button', { name: 'Theme: system' }))
    await userEvent.click(screen.getByRole('button', { name: 'Theme: light' }))

    expect(screen.getByRole('button', { name: 'Theme: dark' })).toBeInTheDocument()
    expect(document.documentElement).toHaveClass('dark')
  })
})
