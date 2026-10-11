import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import './styles.css'

// CodeAtlas runtime: development only. Production builds contain none of it (test/release.test.ts, verify-release.sh).
if (import.meta.env.DEV)
  void import('@codeatlas/web/runtime').then(m =>
    m.initCodeAtlas({ collectorUrl: `${import.meta.env.BASE_URL}codeatlas/v1/traces`, serviceName: 'MinimalApiSample.Web' }))

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
