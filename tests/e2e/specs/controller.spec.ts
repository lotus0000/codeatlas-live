import { expect, test } from '@playwright/test'
import { urls } from '../env'

test('controller sample: action span with source, and a service exception as a stack frame', async ({ page, request }) => {
  await page.goto(urls.viewer)
  await expect(page.getByRole('status', { name: 'Live connection' })).toHaveText(/Live/)
  const trace = page.getByRole('main', { name: 'Trace' })
  const rows = page.getByRole('tree', { name: 'Execution tree' }).getByRole('treeitem')
  const inspector = page.getByRole('complementary', { name: 'Inspector' })

  expect((await request.post(`${urls.controller}api/orders`, { data: { sku: 'KB-01', quantity: 2 } })).status()).toBe(201)
  await expect(trace.getByText('201')).toBeVisible()
  await expect(trace.getByText('No interaction')).toBeVisible()
  await rows.filter({ hasText: 'action' }).first().click()
  await expect(inspector.getByRole('heading', { name: 'ControllerSample.Controllers.OrdersController.Create' })).toBeVisible()
  await expect(inspector.getByText(/ControllerSample\/Controllers\/OrdersController\.cs/)).toBeVisible()
  await expect(inspector.locator('.monaco-editor')).toContainText('Create(CreateOrder request)')

  expect((await request.post(`${urls.controller}api/orders`, { data: { sku: 'KB-01', quantity: 0 } })).status()).toBe(500)
  await expect(trace.getByText('500')).toBeVisible()
  await rows.filter({ hasText: 'action' }).first().click()
  const exception = inspector.getByRole('region', { name: 'Exception' })
  await expect(exception).toContainText('System.ArgumentOutOfRangeException')
  await expect(exception).toContainText('OrderService.Create')
})
