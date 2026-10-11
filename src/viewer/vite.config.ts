/// <reference types="vitest/config" />
import { fileURLToPath } from 'node:url'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  // Relative asset URLs: the collector may be served under a path such as code-server's /absproxy/4318/.
  base: './',
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: { outDir: '../CodeAtlas.Collector/wwwroot', emptyOutDir: true, chunkSizeWarningLimit: 6000 },
  // `npm run dev` against a collector on the default port.
  server: { proxy: { '/api': 'http://127.0.0.1:4318', '/live': { target: 'http://127.0.0.1:4318', ws: true } } },
  test: { environment: 'jsdom', setupFiles: ['./test/setup.ts'], css: false, env: { TZ: 'UTC' } },
})
