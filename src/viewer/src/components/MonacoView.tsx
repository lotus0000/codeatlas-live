import 'monaco-editor/languages/definitions/csharp/register'
import * as monaco from 'monaco-editor/editor/editor.api'
import EditorWorker from 'monaco-editor/editor/editor.worker?worker'
import { useEffect, useRef } from 'react'

// Bundled with the viewer and served by the collector: Monaco and its worker are never fetched from a CDN.
;(self as unknown as { MonacoEnvironment: { getWorker: () => Worker } }).MonacoEnvironment = { getWorker: () => new EditorWorker() }
monaco.editor.defineTheme('codeatlas-dark', { base: 'vs-dark', inherit: true, rules: [], colors: { 'editor.background': '#0c0c0f' } })
monaco.editor.defineTheme('codeatlas-light', { base: 'vs', inherit: true, rules: [], colors: { 'editor.background': '#ffffff' } })

export interface MonacoViewProps {
  text: string
  displayStart: number
  displayEnd: number
  navigationLine: number
  executableLines: number[]
  dark: boolean
}

export default function MonacoView({ text, displayStart, displayEnd, navigationLine, executableLines, dark }: MonacoViewProps) {
  const host = useRef<HTMLDivElement>(null)
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null)

  useEffect(() => {
    const instance = monaco.editor.create(host.current!, {
      value: text,
      language: 'csharp',
      readOnly: true,
      domReadOnly: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      automaticLayout: true,
      glyphMargin: true,
      renderLineHighlight: 'none',
      fontFamily: "'Geist Mono Variable', ui-monospace, monospace",
      fontSize: 12,
      lineHeight: 19,
      theme: dark ? 'codeatlas-dark' : 'codeatlas-light',
    })
    editor.current = instance
    return () => {
      instance.getModel()?.dispose()
      instance.dispose()
      editor.current = null
    }
    // The editor is recreated only for new text; theme and highlights update in place below.
  }, [text]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    monaco.editor.setTheme(dark ? 'codeatlas-dark' : 'codeatlas-light')
  }, [dark])

  useEffect(() => {
    const instance = editor.current
    if (!instance) return
    const decorations = instance.createDecorationsCollection([
      { range: new monaco.Range(displayStart, 1, displayEnd, 1), options: { isWholeLine: true, className: 'ca-declaration' } },
      ...executableLines.map(line => ({
        range: new monaco.Range(line, 1, line, 1),
        options: { glyphMarginClassName: 'ca-executable', glyphMarginHoverMessage: { value: 'Executable line (from the PDB)' } },
      })),
    ])
    instance.revealLineNearTop(navigationLine)
    instance.setPosition({ lineNumber: navigationLine, column: 1 })
    return () => decorations.clear()
  }, [text, displayStart, displayEnd, navigationLine, executableLines])

  return <div ref={host} className="h-full w-full" />
}
