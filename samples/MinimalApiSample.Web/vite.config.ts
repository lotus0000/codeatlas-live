/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

export default defineConfig(({ command, mode }) => {
  // Behind a path proxy (code-server's /absproxy/5181/), DEV_PUBLIC_URL is the address the browser uses.
  const env = command === 'serve' ? loadEnv(mode, import.meta.dirname, '') : {}
  const publicUrl = env.DEV_PUBLIC_URL ? new URL(env.DEV_PUBLIC_URL) : undefined
  const base = publicUrl ? publicUrl.pathname.replace(/\/?$/, '/') : '/'
  const api = env.SAMPLE_API || 'http://127.0.0.1:5180'
  const collector = env.CODEATLAS_COLLECTOR || 'http://127.0.0.1:4318'

  return {
    base,
    plugins: [react()],
    server: {
      allowedHosts: publicUrl ? [publicUrl.hostname] : [],
      hmr: publicUrl ? { clientPort: Number(publicUrl.port) || (publicUrl.protocol === 'https:' ? 443 : 80) } : undefined,
      // Everything is same-origin for the browser: API calls carry their traceparent without CORS, and the
      // browser's spans reach the collector the same way (also through code-server's /absproxy/).
      proxy: {
        [`${base}api`]: { target: api, rewrite: (url: string) => url.slice(base.length - 1) },
        [`${base}codeatlas`]: { target: collector, rewrite: (url: string) => url.slice(`${base}codeatlas`.length) },
      },
    },
    test: { environment: 'jsdom' },
  }
})
