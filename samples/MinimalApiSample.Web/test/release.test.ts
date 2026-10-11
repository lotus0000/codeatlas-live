// @vitest-environment node
import { mkdtempSync, readdirSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'
import { describe, expect, it } from 'vitest'

const root = fileURLToPath(new URL('..', import.meta.url))

describe('production build', () => {
  it('contains the app but no CodeAtlas runtime, OpenTelemetry or collector path', async () => {
    const outDir = mkdtempSync(join(tmpdir(), 'sample-web-'))
    // Vitest sets NODE_ENV=test, which would keep import.meta.env.DEV branches; a real build has production.
    const nodeEnv = process.env.NODE_ENV
    process.env.NODE_ENV = 'production'
    try {
      await build({ root, mode: 'production', logLevel: 'silent', build: { outDir, emptyOutDir: true } })
    } finally {
      process.env.NODE_ENV = nodeEnv
    }
    const js = readdirSync(join(outDir, 'assets')).filter(f => f.endsWith('.js')).map(f => readFileSync(join(outDir, 'assets', f), 'utf8')).join('\n')

    expect(js).toContain('Create Order')
    expect(js).not.toMatch(/traceparent|codeatlas-web|interaction: |codeatlas\/v1/)
  }, 60_000)
})
