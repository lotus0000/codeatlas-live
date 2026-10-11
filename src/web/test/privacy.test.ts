import { afterEach, describe, expect, it } from 'vitest'
import { trackInteraction } from '../src/index'
import { start } from './support'

describe('privacy', () => {
  let current: ReturnType<typeof start> | undefined
  afterEach(async () => {
    await current?.stop()
    current = undefined
    document.body.innerHTML = ''
  })

  const everything = (c: ReturnType<typeof start>) =>
    JSON.stringify(c.spans().map(s => ({ name: s.name, attributes: s.attributes, events: s.events })))

  it('never records typed values, bodies or query strings', async () => {
    current = start()
    document.body.innerHTML = `
      <form name="login">
        <input name="email" value="alice@example.com">
        <input name="password" type="password" value="hunter2-secret">
        <textarea name="note">private note</textarea>
        <button type="submit">Sign in</button>
      </form>`
    const form = document.querySelector('form')!
    form.addEventListener('submit', e => e.preventDefault())

    form.requestSubmit(document.querySelector('button')!)
    await fetch('/api/login?token=SECRET-TOKEN#frag', { method: 'POST', body: JSON.stringify({ password: 'hunter2-secret' }) })
    await trackInteraction('Search', ix => ix.fetch('/api/search?q=SECRET-QUERY'))

    const text = everything(current)
    for (const secret of ['alice@example.com', 'hunter2', 'private note', 'SECRET-TOKEN', 'SECRET-QUERY', 'frag'])
      expect(text).not.toContain(secret)
    expect(text).toContain('"url.path":"/api/login"')
  })
})
