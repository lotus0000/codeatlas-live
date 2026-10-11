import { instrumentAxios, trackInteraction } from '@codeatlas/web'

if (import.meta.env.DEV)
  void import('@codeatlas/web/runtime').then(m => m.initCodeAtlas({ collectorUrl: 'http://127.0.0.1:4318/v1/traces', serviceName: 'fixture' }))

document.querySelector('button')!.addEventListener('click', () => {
  void trackInteraction('Save', ix => ix.fetch('/api/save', { method: 'POST' }))
})
instrumentAxios({ interceptors: { request: { use: () => 0 }, response: { use: () => 0 } } })
