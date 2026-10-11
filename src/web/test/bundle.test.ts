// @vitest-environment node
/// <reference types="node" />
import { mkdtempSync, readdirSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'
import { describe, expect, it } from 'vitest'

const root = fileURLToPath(new URL('./fixtures/app', import.meta.url))
const src = fileURLToPath(new URL('../src', import.meta.url))

async function bundle(entry: 'index.html' | 'control.html'): Promise<string> {
  const outDir = mkdtempSync(join(tmpdir(), 'codeatlas-web-bundle-'))
  // Vitest sets NODE_ENV=test, which would make Vite keep import.meta.env.DEV branches; a real `vite build` has production.
  const nodeEnv = process.env.NODE_ENV
  process.env.NODE_ENV = 'production'
  try {
    await build({
      root,
      mode: 'production',
      logLevel: 'silent',
      resolve: {
        alias: [
          { find: /^@codeatlas\/web\/runtime$/, replacement: join(src, 'runtime.ts') },
          { find: /^@codeatlas\/web$/, replacement: join(src, 'index.ts') },
        ],
      },
      build: { outDir, emptyOutDir: true, rollupOptions: { input: join(root, entry) } },
    })
  } finally {
    process.env.NODE_ENV = nodeEnv
  }
  const assets = join(outDir, 'assets')
  return readdirSync(assets).filter(f => f.endsWith('.js')).map(f => readFileSync(join(assets, f), 'utf8')).join('\n')
}

// Strings that only the runtime contains: OpenTelemetry's header name, our tracer name, the collector port.
const runtimeMarkers = /traceparent|codeatlas-web|4318|interaction: /

describe('production builds', () => {
  it('contain no runtime, no OpenTelemetry and no collector URL', async () => {
    expect(await bundle('index.html')).not.toMatch(runtimeMarkers)
  }, 60_000)

  it('control: a build that references the runtime does contain the markers', async () => {
    expect(await bundle('control.html')).toMatch(runtimeMarkers)
  }, 60_000)
})
