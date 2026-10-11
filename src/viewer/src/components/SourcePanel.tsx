import { Copy, ExternalLink } from 'lucide-react'
import { lazy, Suspense, useEffect, useState } from 'react'
import { getSource, openInEditor } from '@/api/client'
import type { CodeRef, CollectorInfo, SourceLocation, SourceResponse } from '@/api/types'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

// Monaco is large: it loads only when a source panel is shown.
const MonacoView = lazy(() => import('./MonacoView'))

export function editorName(kind?: string): string {
  return kind === 'vscode' ? 'VS Code' : kind === 'rider' ? 'Rider' : (kind ?? 'editor')
}

export function describeSource(code: CodeRef, location: SourceLocation): string {
  const evidence = code.evidence === 'routing'
    ? 'Routing metadata: the route points here; whether it ran is not recorded'
    : 'Executed method'
  const mapping = location.mapping === 'pdb only' ? 'mapped by PDB only (declaration not found)' : 'mapped by PDB + Roslyn'
  return [evidence, mapping, location.kind, location.stateMachine ? 'async or iterator' : null].filter(Boolean).join(' · ')
}

type State = { status: 'loading' } | { status: 'loaded'; source: SourceResponse } | { status: 'failed'; message: string }
type Notice = { tone: 'ok' | 'error'; text: string }

export function SourcePanel({ code, info, dark }: { code: CodeRef; info: CollectorInfo | null; dark: boolean }) {
  const [state, setState] = useState<State>({ status: 'loading' })
  const [notice, setNotice] = useState<Notice | null>(null)
  const identity = `${code.assembly}|${code.mvid}|${code.token}`

  useEffect(() => {
    let current = true
    setState({ status: 'loading' })
    setNotice(null)
    getSource(code).then(
      source => current && setState({ status: 'loaded', source }),
      (e: Error) => current && setState({ status: 'failed', message: e.message }),
    )
    return () => {
      current = false
    }
    // Refetch only when the identity changes, not when an equal object arrives with a refreshed trace.
  }, [identity]) // eslint-disable-line react-hooks/exhaustive-deps

  if (state.status === 'loading') return <p className="text-xs text-muted-foreground">Loading source…</p>
  if (state.status === 'failed') return <p className="text-xs text-error">Could not load source: {state.message}</p>

  const { location, text } = state.source
  if (!location.found || text === null) {
    return <p className="text-xs text-gap">Source not shown: {location.reason ?? 'the collector gave no reason.'}</p>
  }

  const shownPath = location.relativePath ?? location.file!
  const target = `${location.file}:${location.navigationLine}`
  const editor = editorName(info?.editor.kind)
  const available = info?.editor.available ?? false

  const open = async () => {
    setNotice(null)
    try {
      const result = await openInEditor(code)
      setNotice({ tone: 'ok', text: `Opened ${shownPath}:${result.line} in ${editor}` })
    } catch (e) {
      setNotice({ tone: 'error', text: (e as Error).message })
    }
  }
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(target)
      setNotice({ tone: 'ok', text: `Copied ${target}` })
    } catch {
      setNotice({ tone: 'error', text: `Copy failed. Path: ${target}` })
    }
  }

  return (
    <div className="space-y-2">
      <div className="space-y-0.5">
        <p className="break-all font-mono text-xs">
          <span>{shownPath}</span>
          <span className="text-muted-foreground">:{location.navigationLine}</span>
        </p>
        <p className="text-xs text-muted-foreground">{describeSource(code, location)}</p>
      </div>
      <div className="h-80 overflow-hidden rounded-md border">
        <Suspense fallback={<p className="p-3 text-xs text-muted-foreground">Loading editor…</p>}>
          <MonacoView
            text={text}
            displayStart={location.displayStart}
            displayEnd={location.displayEnd}
            navigationLine={location.navigationLine}
            executableLines={location.executableLines}
            dark={dark}
          />
        </Suspense>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button size="sm" onClick={open} disabled={!available} title={available ? undefined : `${editor} was not found; copy the path instead.`}>
          <ExternalLink />
          Open in {editor}
        </Button>
        <Button size="sm" variant="outline" onClick={copy}>
          <Copy />
          Copy path:line
        </Button>
      </div>
      {info && !available && <p className="text-xs text-muted-foreground">{editor} was not found on this machine; use “Copy path:line”.</p>}
      {notice && (
        <p role="status" className={cn('text-xs', notice.tone === 'error' ? 'text-error' : 'text-success')}>
          {notice.text}
        </p>
      )}
    </div>
  )
}
