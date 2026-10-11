import { expect, test, type Page } from '@playwright/test'
import { urls } from '../env'

async function openHandler(page: Page) {
  const shop = await page.context().newPage()
  await shop.goto(urls.app)
  await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
  await page.goto(urls.viewer)
  await page.getByRole('tree', { name: 'Execution tree' }).getByRole('treeitem').filter({ hasText: 'handler' }).first().click()
  await expect(page.locator('.monaco-editor')).toContainText('CreateOrder')
  await page.waitForTimeout(400)
}

const right = async (page: Page, selector: string) => {
  const box = await page.locator(selector).first().boundingBox()
  return box!.x + box!.width
}

test('the inspector content stays inside its pane', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 })
  await openHandler(page)

  const pane = await right(page, '[aria-label="Inspector"]')
  expect(await right(page, '.monaco-editor')).toBeLessThanOrEqual(pane + 1)
  expect(await right(page, '[aria-label="Inspector"] h2')).toBeLessThanOrEqual(pane + 1)
})

test('panes can be resized by dragging, and the size is remembered', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 })
  await openHandler(page)
  const inspector = page.getByRole('complementary', { name: 'Inspector' })
  const before = (await inspector.boundingBox())!.width

  const handle = page.getByRole('separator', { name: 'Resize inspector' })
  const box = (await handle.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down()
  await page.mouse.move(box.x - 200, box.y + box.height / 2, { steps: 10 })
  await page.mouse.up()

  const after = (await inspector.boundingBox())!.width
  expect(after).toBeGreaterThan(before + 150)
  expect(await right(page, '.monaco-editor')).toBeLessThanOrEqual(await right(page, '[aria-label="Inspector"]') + 1)

  await page.reload()
  await expect(page.getByRole('tree', { name: 'Execution tree' })).toBeVisible()
  expect((await page.getByRole('complementary', { name: 'Inspector' }).boundingBox())!.width).toBeGreaterThan(before + 150)
})
