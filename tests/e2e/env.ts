import { fileURLToPath } from 'node:url'

export const repo = fileURLToPath(new URL('../..', import.meta.url)).replace(/\/$/, '')
// Different from the demo's ports, so a running demo does not get in the way.
export const ports = { collector: 14318, api: 15180, web: 15181, controller: 15182, db: 55434 }
export const urls = {
  viewer: `http://127.0.0.1:${ports.collector}/`,
  app: `http://127.0.0.1:${ports.web}/`,
  controller: `http://127.0.0.1:${ports.controller}/`,
}
export const connectionString = `Host=127.0.0.1;Port=${ports.db};Database=codeatlas_e2e;Username=codeatlas;Password=codeatlas-e2e`
export const recorderFile = `${repo}/tests/e2e/.recorder/editor-args.txt`
