import { useCallback, useEffect, useState } from 'react'

export type ThemeMode = 'system' | 'light' | 'dark'

const key = 'codeatlas.theme'
const darkQuery = '(prefers-color-scheme: dark)'

export function readMode(): ThemeMode {
  try {
    const value = localStorage.getItem(key)
    return value === 'light' || value === 'dark' ? value : 'system'
  } catch {
    return 'system'
  }
}

export function saveMode(mode: ThemeMode) {
  try {
    if (mode === 'system') localStorage.removeItem(key)
    else localStorage.setItem(key, mode)
  } catch {
    // Storage blocked (private window): the choice lasts until reload.
  }
}

export const nextMode = (mode: ThemeMode): ThemeMode => (mode === 'system' ? 'light' : mode === 'light' ? 'dark' : 'system')

export function useTheme() {
  const [mode, setMode] = useState<ThemeMode>(readMode)
  const [systemDark, setSystemDark] = useState(() => window.matchMedia(darkQuery).matches)

  useEffect(() => {
    const query = window.matchMedia(darkQuery)
    const onChange = (event: MediaQueryListEvent) => setSystemDark(event.matches)
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])

  const dark = mode === 'dark' || (mode === 'system' && systemDark)
  useEffect(() => {
    document.documentElement.classList.toggle('dark', dark)
  }, [dark])

  const cycle = useCallback(() => {
    setMode(current => {
      const next = nextMode(current)
      saveMode(next)
      return next
    })
  }, [])

  return { mode, dark, cycle }
}
