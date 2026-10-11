import { readFileSync, rmSync } from 'node:fs'
import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { recorderFile, repo, urls } from '../env'

async function openBoth(context: BrowserContext) {
  const shop = await context.newPage()
  await shop.goto(urls.app)
  await expect(shop.getByRole('cell', { name: 'KB-01' })).toBeVisible()
  const atlas = await context.newPage()
  await atlas.goto(urls.viewer)
  await expect(atlas.getByRole('status', { name: 'Live connection' })).toHaveText(/Live/)
  return { shop, atlas }
}

const trace = (atlas: Page) => atlas.getByRole('main', { name: 'Trace' })
const rows = (atlas: Page) => atlas.getByRole('tree', { name: 'Execution tree' }).getByRole('treeitem')
const rowOf = (atlas: Page, kind: string) => rows(atlas).filter({ hasText: kind }).first()
const inspector = (atlas: Page) => atlas.getByRole('complementary', { name: 'Inspector' })

test('Create Order: click → tree → handler source → database span without SQL', async ({ context }) => {
  const { shop, atlas } = await openBoth(context)

  await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
  await expect(shop.getByRole('status')).toHaveText('Order created (HTTP 201)')

  await expect(trace(atlas).getByText('Create order', { exact: true })).toBeVisible()
  await expect(trace(atlas).getByText('explicit')).toBeVisible()
  await expect(trace(atlas).getByText('201')).toBeVisible()
  for (const kind of ['interaction', 'browser-request', 'server', 'endpoint', 'handler'])
    await expect(rows(atlas).filter({ hasText: kind }).first()).toBeVisible()
  await expect(rows(atlas).filter({ hasText: 'SELECT products' })).toHaveCount(1)
  await expect(rows(atlas).filter({ hasText: 'INSERT orders' })).toHaveCount(1)
  await expect(rows(atlas).filter({ hasText: 'route → OrderEndpoints.CreateOrder' })).toHaveCount(1)

  await rowOf(atlas, 'handler').click()
  await expect(inspector(atlas).getByRole('heading', { name: 'MinimalApiSample.Orders.OrderEndpoints.CreateOrder' })).toBeVisible()
  await expect(inspector(atlas).getByText(/MinimalApiSample\/Orders\/OrderEndpoints\.cs/)).toBeVisible()
  await expect(inspector(atlas).locator('.monaco-editor')).toContainText('CreateOrder')

  await rows(atlas).filter({ hasText: 'INSERT orders' }).click()
  await expect(inspector(atlas).getByText('SQL text, parameters and connection details are never exported.')).toBeVisible()
  await expect(inspector(atlas)).not.toContainText('VALUES')
})

test('the page-load request is a trace without an interaction', async ({ context }) => {
  const { atlas } = await openBoth(context)

  const list = atlas.getByRole('complementary', { name: 'Recent activity' })
  const pageLoad = list.getByRole('button').filter({ hasText: 'GET /api/products' }).first()
  await expect(pageLoad).toContainText('No interaction')
})

test('Out of stock: 500, the exception on the handler, and Explain names it', async ({ context }) => {
  const { shop, atlas } = await openBoth(context)

  await shop.getByRole('button', { name: 'Create Order (out of stock)' }).click()
  await expect(shop.getByRole('status')).toHaveText('Order failed (HTTP 500)')

  await expect(trace(atlas).getByText('Create order (out of stock)', { exact: true })).toBeVisible()
  await expect(trace(atlas).getByText('500')).toBeVisible()
  const handler = rowOf(atlas, 'handler')
  await expect(handler).toContainText('error')
  await handler.click()
  const exception = inspector(atlas).getByRole('region', { name: 'Exception' })
  await expect(exception).toContainText('System.InvalidOperationException')
  await expect(exception).toContainText('Out of stock: MON-27')
  await expect(exception).toContainText('OrderService.CreateAsync')

  await atlas.getByRole('tab', { name: 'Explain' }).click()
  await expect(atlas.getByRole('region', { name: 'Errors' })).toContainText('Out of stock: MON-27')
})

test('Refresh products is linked by timing; Axios with ix.axiosConfig() is linked explicitly', async ({ context }) => {
  const { shop, atlas } = await openBoth(context)

  await shop.getByRole('button', { name: 'Refresh products' }).click()
  await expect(trace(atlas).getByText('Refresh products', { exact: true })).toBeVisible()
  await expect(trace(atlas).getByText('heuristic').first()).toBeVisible()

  await shop.getByRole('button', { name: 'Create Order (Axios)' }).click()
  await expect(shop.getByRole('status')).toHaveText('Order created with Axios (HTTP 201)')
  await expect(trace(atlas).getByText('Create order (Axios)', { exact: true })).toBeVisible()
  await expect(trace(atlas).getByText('explicit')).toBeVisible()
  await expect(rows(atlas).filter({ hasText: 'handler' })).toHaveCount(1)
})

test('Open in code-server hands the editor the declaration line, as one argument', async ({ context }) => {
  rmSync(recorderFile, { force: true })
  const { shop, atlas } = await openBoth(context)
  await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
  await expect(trace(atlas).getByText('Create order', { exact: true })).toBeVisible()
  await rowOf(atlas, 'handler').click()

  await inspector(atlas).getByRole('button', { name: 'Open in code-server' }).click()

  await expect(inspector(atlas).getByRole('status')).toHaveText(/^Opened .*OrderEndpoints\.cs:\d+ in code-server$/)
  const file = `${repo}/samples/MinimalApiSample/Orders/OrderEndpoints.cs`
  const line = readFileSync(file, 'utf8').split('\n').findIndex(l => l.includes('public static async Task<IResult> CreateOrder(')) + 1
  expect(readFileSync(recorderFile, 'utf8').trim().split('\n')).toEqual(['-r', '-g', `${file}:${line}`])
})

test('Monaco and source work with every outside request blocked', async ({ context }) => {
  const blocked: string[] = []
  await context.route('**/*', route => {
    const host = new URL(route.request().url()).hostname
    if (host === '127.0.0.1' || host === 'localhost') return route.continue()
    blocked.push(route.request().url())
    return route.abort()
  })
  const { shop, atlas } = await openBoth(context)
  await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
  await expect(trace(atlas).getByText('Create order', { exact: true })).toBeVisible()

  await rowOf(atlas, 'handler').click()

  await expect(inspector(atlas).locator('.monaco-editor')).toContainText('CreateOrder')
  expect(blocked).toEqual([])
})
