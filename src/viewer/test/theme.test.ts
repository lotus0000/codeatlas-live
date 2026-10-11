import { act, renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { nextMode, readMode, saveMode, useTheme } from '../src/theme'

describe('theme', () => {
  it('defaults to the system setting and remembers an explicit choice', () => {
    expect(readMode()).toBe('system')
    saveMode('dark')
    expect(readMode()).toBe('dark')
    saveMode('system')
    expect(localStorage.getItem('codeatlas.theme')).toBeNull()
  })

  it('works when storage is unavailable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    expect(readMode()).toBe('system')
    expect(() => saveMode('dark')).not.toThrow()
  })

  it('cycles system → light → dark → system', () => {
    expect([nextMode('system'), nextMode('light'), nextMode('dark')]).toEqual(['light', 'dark', 'system'])
  })

  it('useTheme applies the dark class and saves each choice', () => {
    const { result } = renderHook(() => useTheme())
    expect(result.current.dark).toBe(false)
    act(() => result.current.cycle()) // light
    act(() => result.current.cycle()) // dark
    expect(result.current.mode).toBe('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
    expect(localStorage.getItem('codeatlas.theme')).toBe('dark')
  })
})
