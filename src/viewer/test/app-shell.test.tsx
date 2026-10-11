import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { App } from '../src/App'
import { FakeLive, stubCollector } from './support'

describe('app shell', () => {
  it('shows the collector it is connected to', async () => {
    stubCollector()
    render(<App live={new FakeLive()} />)
    expect(await screen.findByText(/v0\.1\.0/)).toBeInTheDocument()
    expect(screen.getByText('CodeAtlas Live')).toBeInTheDocument()
  })
})
