import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { App } from '../src/App'

const products = [{ sku: 'KB-01', name: 'Mechanical keyboard', stock: 5 }, { sku: 'MON-27', name: '27-inch monitor', stock: 0 }]

function stubBackend() {
  const calls: { url: string; init?: RequestInit }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, init })
    if (url.endsWith('api/products')) return Response.json(products)
    const body = JSON.parse(String(init?.body))
    return body.sku === 'MON-27' ? Response.json({ title: 'Out of stock' }, { status: 500 }) : Response.json({ id: 7 }, { status: 201 })
  }))
  return calls
}

describe('shop', () => {
  afterEach(() => { cleanup(); vi.unstubAllGlobals() })

  it('lists products on load', async () => {
    stubBackend()
    render(<App />)
    expect(await screen.findByRole('cell', { name: 'KB-01' })).toBeTruthy()
  })

  it('Create Order posts one KB-01 and reports the status', async () => {
    const calls = stubBackend()
    render(<App />)
    await userEvent.click(await screen.findByRole('button', { name: 'Create Order' }))

    expect((await screen.findByRole('status')).textContent).toBe('Order created (HTTP 201)')
    const post = calls.find(c => c.init?.method === 'POST')!
    expect(post.url).toBe('/api/orders')
    expect(JSON.parse(String(post.init!.body))).toEqual({ sku: 'KB-01', quantity: 1 })
  })

  it('the out-of-stock button reports the failure', async () => {
    stubBackend()
    render(<App />)
    await userEvent.click(await screen.findByRole('button', { name: 'Create Order (out of stock)' }))

    expect((await screen.findByRole('status')).textContent).toBe('Order failed (HTTP 500)')
  })
})
