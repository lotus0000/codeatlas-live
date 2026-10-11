/** Milliseconds between a span start and the trace origin, computed in BigInt so nanoseconds are not rounded away. */
export function offsetMs(startUnixNano: string, origin: bigint): number {
  return Number(BigInt(startUnixNano) - origin) / 1_000_000
}

export function formatMs(ms: number): string {
  return ms >= 1000 ? `${(ms / 1000).toFixed(2)} s` : `${ms.toFixed(1)} ms`
}

const pad = (n: number, width = 2) => String(n).padStart(width, '0')

/** Local wall-clock time of a nanosecond timestamp. */
export function clock(unixNano: string, withMs = false): string {
  const date = new Date(Number(BigInt(unixNano) / 1_000_000n))
  const base = `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
  return withMs ? `${base}.${pad(date.getMilliseconds(), 3)}` : base
}

export const shortId = (id: string) => (id.length > 16 ? `${id.slice(0, 8)}…${id.slice(-6)}` : id)
