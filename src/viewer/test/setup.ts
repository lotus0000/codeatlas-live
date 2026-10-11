import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  localStorage.clear()
  document.documentElement.className = ''
  document.head.querySelectorAll('base').forEach(b => b.remove())
})

// jsdom lacks these; Radix and the theme code use them.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false, media: query, onchange: null,
    addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false,
  }),
})
globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver
Element.prototype.scrollIntoView ??= function () {}

// Monaco needs a real browser. Tests see a <pre> that records what it was asked to show; the E2E suite (Plan 5) runs the real one.
vi.mock('@/components/MonacoView', async () => {
  const { createElement } = await import('react')
  return {
    default: (props: { text: string; navigationLine: number; displayStart: number; displayEnd: number }) =>
      createElement('pre', {
        'data-testid': 'monaco',
        'data-line': props.navigationLine,
        'data-range': `${props.displayStart}-${props.displayEnd}`,
      }, props.text),
  }
})

// jsdom has no layout: every element is a 0×0 box at 0,0, so react-resizable-panels would treat every pointer event as
// hitting a pane separator (and take focus). Put separators out of the way; real resizing is covered by the E2E suite.
const realRect = Element.prototype.getBoundingClientRect
Element.prototype.getBoundingClientRect = function (this: Element) {
  return this.hasAttribute('data-separator') ? DOMRect.fromRect({ x: -10_000, y: -10_000, width: 1, height: 1 }) : realRect.call(this)
}
