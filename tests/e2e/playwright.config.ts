import { defineConfig } from '@playwright/test'
import { connectionString, ports, recorderFile, repo, urls } from './env'

const collectorEndpoint = `http://127.0.0.1:${ports.collector}/v1/traces`
const sampleEnv = (port: number) => ({
  ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
  ASPNETCORE_ENVIRONMENT: 'Development',
  CodeAtlas__Endpoint: collectorEndpoint,
})

export default defineConfig({
  testDir: './specs',
  workers: 1,
  timeout: 90_000,
  expect: { timeout: 20_000 },
  reporter: [['list']],
  use: { viewport: { width: 1440, height: 900 }, trace: 'retain-on-failure' },
  webServer: [
    {
      command: `dotnet run --project ${repo}/src/CodeAtlas.Collector -- --urls http://127.0.0.1:${ports.collector} --source-root ${repo}/samples --editor code-server --editor-command ${repo}/tests/e2e/recorder-editor.sh`,
      url: `${urls.viewer}api/info`,
      env: { CODEATLAS_RECORDER_FILE: recorderFile },
      timeout: 240_000,
    },
    {
      command: `dotnet run --project ${repo}/samples/MinimalApiSample --no-launch-profile`,
      url: `http://127.0.0.1:${ports.api}/api/products`,
      env: { ...sampleEnv(ports.api), ConnectionStrings__Shop: connectionString },
      timeout: 240_000,
    },
    {
      command: `dotnet run --project ${repo}/samples/ControllerSample --no-launch-profile`,
      url: `${urls.controller}api/orders`,
      env: sampleEnv(ports.controller),
      timeout: 240_000,
    },
    {
      command: `npx vite --port ${ports.web} --strictPort --host 127.0.0.1`,
      cwd: `${repo}/samples/MinimalApiSample.Web`,
      url: urls.app,
      env: { SAMPLE_API: `http://127.0.0.1:${ports.api}`, CODEATLAS_COLLECTOR: `http://127.0.0.1:${ports.collector}` },
      timeout: 120_000,
    },
  ],
})
