import { useEffect, useState } from 'react'
import { getInfo } from '@/api/client'
import { signalRLive, type LiveSource } from '@/api/live'
import type { CollectorInfo } from '@/api/types'
import { useTheme } from '@/theme'

export function App(_props: { live?: LiveSource } = { live: signalRLive }) {
  useTheme()
  const [info, setInfo] = useState<CollectorInfo | null>(null)
  useEffect(() => {
    getInfo().then(setInfo, () => setInfo(null))
  }, [])
  return (
    <div className="flex h-screen items-center justify-center gap-3 bg-background text-foreground">
      <span className="font-semibold">CodeAtlas Live</span>
      <span className="font-mono text-sm text-muted-foreground">{info ? `collector v${info.version}` : 'connecting…'}</span>
    </div>
  )
}
