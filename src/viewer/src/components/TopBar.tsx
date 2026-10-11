import { Monitor, Moon, Radio, Sun } from 'lucide-react'
import type { LiveState } from '@/api/live'
import type { CollectorInfo } from '@/api/types'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import type { ThemeMode } from '@/theme'

const liveText: Record<LiveState, string> = { live: 'Live', connecting: 'Connecting…', offline: 'Offline' }

export function TopBar({ info, liveState, follow, onFollow, mode, onCycleMode }: {
  info: CollectorInfo | null
  liveState: LiveState
  follow: boolean
  onFollow: (follow: boolean) => void
  mode: ThemeMode
  onCycleMode: () => void
}) {
  const ThemeIcon = mode === 'dark' ? Moon : mode === 'light' ? Sun : Monitor
  return (
    <header className="flex h-12 shrink-0 items-center gap-3 border-b px-4">
      <span className="font-semibold tracking-tight">CodeAtlas Live</span>
      <span role="status" aria-label="Live connection" className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <span
          aria-hidden
          className={cn('size-2 rounded-full', liveState === 'live' ? 'bg-success' : liveState === 'connecting' ? 'bg-heuristic' : 'bg-error')}
        />
        {liveText[liveState]}
      </span>
      <span className="truncate font-mono text-xs text-muted-foreground">{`${location.host}${location.pathname}`.replace(/\/$/, '')}</span>
      {info && <span className="font-mono text-xs text-muted-foreground">v{info.version}</span>}
      <div className="ml-auto flex items-center gap-2">
        <Button variant={follow ? 'secondary' : 'outline'} size="sm" aria-pressed={follow} onClick={() => onFollow(!follow)}>
          <Radio />
          Follow latest
        </Button>
        <Button variant="ghost" size="icon" aria-label={`Theme: ${mode}`} title={`Theme: ${mode}`} onClick={onCycleMode}>
          <ThemeIcon />
        </Button>
      </div>
    </header>
  )
}
