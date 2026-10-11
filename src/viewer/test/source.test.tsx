import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { resetSessionToken } from '../src/api/client'
import type { NodeView } from '../src/api/types'
import { SourcePanel } from '../src/components/SourcePanel'
import { info, sourceFound, sourceRefused, stubApi, success } from './support'

const all = (nodes: NodeView[]): NodeView[] => nodes.flatMap(n => [n, ...all(n.children)])
const handler = all(success.roots).find(n => n.kind === 'handler')!.code!
const endpoint = all(success.roots).find(n => n.kind === 'endpoint')!.code!
const location = sourceFound.location

describe('source panel', () => {
  beforeEach(() => resetSessionToken())

  it('shows the verified declaration at the navigation line', async () => {
    stubApi({ 'api/source?': sourceFound })
    render(<SourcePanel code={handler} info={info} dark={false} />)

    const editor = await screen.findByTestId('monaco')
    expect(editor).toHaveAttribute('data-line', String(location.navigationLine))
    expect(editor).toHaveAttribute('data-range', `${location.displayStart}-${location.displayEnd}`)
    expect(editor.textContent).toBe(sourceFound.text)
    expect(screen.getByText(location.relativePath ?? location.file!)).toBeInTheDocument()
    expect(screen.getByText(`:${location.navigationLine}`)).toBeInTheDocument()
    expect(screen.getByText(/^Executed method · mapped by PDB/)).toBeInTheDocument()
  })

  it('routing evidence says the route points here without claiming it ran', async () => {
    stubApi({ 'api/source?': sourceFound })
    render(<SourcePanel code={endpoint} info={info} dark={false} />)
    expect(await screen.findByText(/^Routing metadata: the route points here; whether it ran is not recorded/)).toBeInTheDocument()
  })

  it('Open sends the identity and reports where it opened', async () => {
    const calls = stubApi({
      'api/source?': sourceFound,
      'api/session': { token: 't' },
      'api/open': { message: 'Opened', file: location.file, line: location.navigationLine },
    })
    render(<SourcePanel code={handler} info={info} dark={false} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Open in code-server' }))

    expect(await screen.findByRole('status')).toHaveTextContent(`Opened ${location.relativePath ?? location.file}:${location.navigationLine} in code-server`)
    const body = JSON.parse(String(calls.find(c => c.url.endsWith('api/open'))!.init!.body))
    expect(Object.keys(body).sort()).toEqual(['assembly', 'mvid', 'token'])
  })

  it('an editor failure is shown, not swallowed', async () => {
    stubApi({
      'api/source?': sourceFound,
      'api/session': { token: 't' },
      'api/open': () => Response.json({ detail: 'code-server exited with code 1.' }, { status: 502 }),
    })
    render(<SourcePanel code={handler} info={info} dark={false} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Open in code-server' }))

    expect(await screen.findByRole('status')).toHaveTextContent('code-server exited with code 1.')
  })

  it('without an editor, Open is disabled and copying is offered', async () => {
    stubApi({ 'api/source?': sourceFound })
    // user-event replaces navigator.clipboard with its own stub during setup; spy on that one.
    const user = userEvent.setup()
    const writeText = vi.spyOn(navigator.clipboard, 'writeText')
    render(<SourcePanel code={handler} info={{ ...info, editor: { kind: 'rider', available: false } }} dark={false} />)

    expect(await screen.findByRole('button', { name: 'Open in Rider' })).toBeDisabled()
    expect(screen.getByText(/Rider was not found on this machine/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Copy path:line' }))

    expect(writeText).toHaveBeenCalledWith(`${location.file}:${location.navigationLine}`)
    expect(screen.getByRole('status')).toHaveTextContent(`Copied ${location.file}:${location.navigationLine}`)
  })

  it('a refused mapping shows the reason and no editor', async () => {
    stubApi({ 'api/source?': sourceRefused })
    render(<SourcePanel code={handler} info={info} dark={false} />)

    expect(await screen.findByText(`Source not shown: ${sourceRefused.location.reason}`)).toBeInTheDocument()
    expect(screen.queryByTestId('monaco')).toBeNull()
    expect(screen.queryByRole('button', { name: /Open in/ })).toBeNull()
  })

  it('a failed request is reported', async () => {
    stubApi({ 'api/source?': () => Response.json({ detail: 'boom' }, { status: 500 }) })
    render(<SourcePanel code={handler} info={info} dark={false} />)
    expect(await screen.findByText('Could not load source: boom')).toBeInTheDocument()
  })
})
