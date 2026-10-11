// @vitest-environment node
/// <reference types="node" />
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const pkg = fileURLToPath(new URL('..', import.meta.url))

// Vitest, SSR and other Node consumers load the published files with Node's ESM loader, which needs explicit extensions.
describe('published package', () => {
  it('every entry point loads in Node ESM', () => {
    execFileSync('npx', ['tsc', '-p', 'tsconfig.build.json'], { cwd: pkg })
    const script = "for (const e of ['@codeatlas/web', '@codeatlas/web/runtime', '@codeatlas/web/axios']) await import(e); console.log('ok')"
    expect(execFileSync(process.execPath, ['--input-type=module', '-e', script], { cwd: pkg, encoding: 'utf8' }).trim()).toBe('ok')
  }, 60_000)
})
