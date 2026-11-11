import { expect, test, type Page } from '@playwright/test'
import { prerequisiteError, resolvedAuthStatePath, seedMsalSession } from './msal-session'

async function gotoShell(page: Page, path: string): Promise<void> {
  await page.goto(path)
  await expect(page.locator('.page')).toBeVisible({ timeout: 90_000 })
}

test.describe.configure({ mode: 'serial' })

test.describe('Stack B rubric editor', () => {
  test.skip(Boolean(prerequisiteError), prerequisiteError ?? '')
  test.use({
    storageState: resolvedAuthStatePath ?? { cookies: [], origins: [] },
    viewport: { width: 1280, height: 900 },
  })

  let page: Page

  test.beforeAll(async ({ browser }) => {
    const context = await browser.newContext({
      storageState: resolvedAuthStatePath,
      viewport: { width: 1280, height: 900 },
    })
    await seedMsalSession(context)
    page = await context.newPage()
  })

  test.afterAll(async () => {
    await page?.context().close()
  })

  test('shows extraction diagnostics and rubric editor affordances', async () => {
    await gotoShell(page, '/')
    const jobLink = page.locator('a,button').filter({ hasText: /job|view/i }).first()
    await expect(jobLink).toBeVisible()
    await jobLink.click()

    await expect(page.getByText(/Extraction diagnostics|Itemized Rubric/i)).toBeVisible({ timeout: 30_000 })
    await expect(page.getByRole('button', { name: /Move up|Move down|Add item/i }).first()).toBeVisible()
  })
})
