import { expect, test } from '@playwright/test'
import { urls } from '../env'

for (const width of [1280, 1440]) {
  for (const colorScheme of ['light', 'dark'] as const) {
    test(`viewer at ${width}px, ${colorScheme}: no horizontal scroll`, async ({ browser }, testInfo) => {
      const context = await browser.newContext({ viewport: { width, height: 800 }, colorScheme })
      const shop = await context.newPage()
      await shop.goto(urls.app)
      await shop.getByRole('button', { name: 'Create Order', exact: true }).click()
      const page = await context.newPage()
      await page.goto(urls.viewer)
      await expect(page.getByRole('tree', { name: 'Execution tree' })).toBeVisible()
      await page.getByRole('tree', { name: 'Execution tree' }).getByRole('treeitem').filter({ hasText: 'handler' }).first().click()
      await expect(page.locator('.monaco-editor')).toBeVisible()

      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      await testInfo.attach(`viewer-${width}-${colorScheme}`, { body: await page.screenshot(), contentType: 'image/png' })
      await context.close()
    })
  }
}
