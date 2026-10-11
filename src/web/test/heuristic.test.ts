import { afterEach, describe, expect, it } from 'vitest'
import { trackInteraction } from '../src/index'
import { named, parentId, sleep, start, traceparent } from './support'

function button(html: string): HTMLElement {
  document.body.innerHTML = html
  return document.body.querySelector('button, a, [role=button], input')!
}

describe('heuristic correlation', () => {
  let current: ReturnType<typeof start> | undefined
  afterEach(async () => {
    await current?.stop()
    current = undefined
    document.body.innerHTML = ''
  })

  it('links a plain fetch after a click and marks it heuristic', async () => {
    current = start()
    button('<button data-codeatlas="Quick order">Order</button>').click()

    await fetch('/api/orders', { method: 'POST' })

    const [interaction] = named(current.spans(), 'interaction: Quick order')
    const [client] = named(current.spans(), 'POST /api/orders')
    expect(interaction.attributes['codeatlas.correlation']).toBe('heuristic')
    expect(interaction.attributes['codeatlas.interaction.label_source']).toBe('data-codeatlas')
    expect(interaction.attributes['codeatlas.interaction.event']).toBe('click')
    expect(parentId(client)).toBe(interaction.spanContext().spanId)
    expect(client.attributes['codeatlas.correlation']).toBe('heuristic')
    expect(traceparent(current.calls[0])).toContain(interaction.spanContext().traceId)
  })

  it('does nothing for requests after the window', async () => {
    current = start({ windowMs: 30 })
    button('<button>Order</button>').click()
    await sleep(60)

    await fetch('/api/orders')

    expect(current.spans()).toHaveLength(0)
    expect(traceparent(current.calls[0])).toBeNull()
  })

  it('a click without a request creates no span', async () => {
    current = start({ windowMs: 30 })
    button('<button>Order</button>').click()
    await sleep(60)

    expect(current.spans()).toHaveLength(0)
  })

  it('an explicit interaction in the same click cancels the heuristic one', async () => {
    current = start()
    const b = button('<button>Order</button>')
    let done: Promise<unknown> = Promise.resolve()
    b.addEventListener('click', () => {
      done = Promise.all([trackInteraction('Explicit order', ix => ix.fetch('/api/orders')), fetch('/api/side-refresh')])
    })

    b.click()
    await done

    expect(current.spans().filter(s => s.name.startsWith('interaction: ')).map(s => s.name)).toEqual(['interaction: Explicit order'])
  })

  it('requests that already carry a traceparent are left alone', async () => {
    current = start()
    const existing = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01'
    button('<button>Order</button>').click()

    await fetch('/api/orders', { headers: { traceparent: existing } })

    expect(traceparent(current.calls[0])).toBe(existing)
    expect(current.spans()).toHaveLength(0)
  })

  it('cross-origin requests get no header unless allow-listed', async () => {
    current = start({ propagateTo: [/^https:\/\/api\.example\//] })
    button('<button>Order</button>').click()

    await fetch('https://thirdparty.example/x')
    await fetch('https://api.example/v1/orders')

    expect(traceparent(current.calls[0])).toBeNull()
    expect(traceparent(current.calls[1])).not.toBeNull()
    expect(named(current.spans(), 'GET /x')).toHaveLength(0)
  })

  it('requests to the collector are never traced, and the original fetch is exposed', async () => {
    current = start()
    button('<button>Order</button>').click()

    await fetch('http://localhost:4318/v1/traces', { method: 'POST' })

    expect(traceparent(current.calls[0])).toBeNull()
    expect(current.spans()).toHaveLength(0)
    expect((window.fetch as typeof fetch & { __original?: unknown }).__original).toBe(current.fake)
  })

  it('Clicks_on_nested_elements_use_the_enclosing_button', async () => {
    current = start()
    document.body.innerHTML = '<button aria-label="Delete order"><svg><path id="icon"></path></svg></button>'
    document.getElementById('icon')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))

    await fetch('/api/orders/1', { method: 'DELETE' })

    const [interaction] = named(current.spans(), 'interaction: Delete order')
    expect(interaction.attributes['codeatlas.interaction.label_source']).toBe('aria-label')
  })

  it('labels come from markup in priority order', async () => {
    current = start()
    const cases: Array<[string, string, string]> = [
      ['<button aria-label="Close dialog">×</button>', 'Close dialog', 'aria-label'],
      ['<button>  Save\n   changes  </button>', 'Save changes', 'text'],
      ['<a href="#" role="button">Open report</a>', 'Open report', 'text'],
      ['<input type="submit" value="Send">', 'Send', 'text'],
      [`<button>${'x'.repeat(60)}</button>`, 'x'.repeat(40), 'text'],
    ]
    for (const [html, label, source] of cases) {
      button(html).dispatchEvent(new MouseEvent('click', { bubbles: true }))
      await fetch('/api/x')
      const [interaction] = named(current.spans(), `interaction: ${label}`)
      expect(interaction.attributes['codeatlas.interaction.label_source']).toBe(source)
    }
  })

  it('textLabels: false falls back to the tag name', async () => {
    current = start({ textLabels: false })
    button('<button>Secret customer name</button>').click()

    await fetch('/api/x')

    expect(named(current.spans(), 'interaction: button')).toHaveLength(1)
  })

  it('form submits use the submitter text, or the form name without one', async () => {
    current = start()
    document.body.innerHTML = '<form name="login"><input name="email"><button type="submit">Sign in</button></form>'
    const form = document.querySelector('form')!
    form.addEventListener('submit', e => e.preventDefault())

    form.requestSubmit(document.querySelector('button')!)
    await fetch('/api/login', { method: 'POST' })
    await sleep(0)
    form.requestSubmit()
    await fetch('/api/login', { method: 'POST' })

    const sources = current.spans().filter(s => s.name.startsWith('interaction: ')).map(s => [s.name, s.attributes['codeatlas.interaction.event']])
    expect(sources).toEqual([['interaction: Sign in', 'submit'], ['interaction: form login', 'submit']])
  })
})
