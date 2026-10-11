import { trackInteraction } from '@codeatlas/web'
import { useCallback, useEffect, useState } from 'react'
import { api, apiPath, type Product } from './api'

export function App() {
  const [products, setProducts] = useState<Product[]>([])
  const [status, setStatus] = useState('')

  // Plain fetch: when it follows a click, CodeAtlas links it to that click by timing ("heuristic").
  const refresh = useCallback(async () => {
    const response = await fetch(apiPath('products'))
    if (response.ok) setProducts(await response.json())
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Explicit: every request made through ix belongs to this interaction, across awaits.
  const createOrder = (label: string, sku: string) =>
    trackInteraction(label, async ix => {
      const response = await ix.fetch(apiPath('orders'), {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ sku, quantity: 1 }),
      })
      // No product refresh here: it would be a newer trace, and the viewer's "Follow latest" would show it instead.
      setStatus(response.ok ? `Order created (HTTP ${response.status})` : `Order failed (HTTP ${response.status})`)
    })

  const createWithAxios = () =>
    trackInteraction('Create order (Axios)', async ix => {
      try {
        const response = await api.post('orders', { sku: 'KB-01', quantity: 1 }, ix.axiosConfig())
        setStatus(`Order created with Axios (HTTP ${response.status})`)
      } catch {
        setStatus('Order failed (Axios)')
      }
    })

  return (
    <main>
      <h1>Shop</h1>
      <p className="hint">A sample app for CodeAtlas Live. Click a button, then look at the viewer.</p>
      <div className="actions">
        <button type="button" onClick={() => void createOrder('Create order', 'KB-01')}>Create Order</button>
        <button type="button" onClick={() => void createOrder('Create order (out of stock)', 'MON-27')}>Create Order (out of stock)</button>
        <button type="button" onClick={() => void createWithAxios()}>Create Order (Axios)</button>
        <button type="button" onClick={() => void refresh()}>Refresh products</button>
      </div>
      <p role="status">{status}</p>
      <table>
        <thead>
          <tr><th>SKU</th><th>Product</th><th>Stock</th></tr>
        </thead>
        <tbody>
          {products.map(p => (
            <tr key={p.sku}><td>{p.sku}</td><td>{p.name}</td><td>{p.stock}</td></tr>
          ))}
        </tbody>
      </table>
    </main>
  )
}
