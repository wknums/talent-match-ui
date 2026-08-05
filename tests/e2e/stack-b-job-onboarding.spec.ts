import { expect, test, type Page } from '@playwright/test'
import { existsSync } from 'node:fs'
import {
  prerequisiteError,
  resolvedAuthStatePath,
  seedMsalSession,
} from './msal-session'

const ORGANIZATION_NAME = 'Western Cape Government'
const SEED_DEPARTMENT_NAME = 'Corporate Services'
const DEPARTMENT_NAME = 'Education'

const SPEC_FILE_ENV = 'E2E_JOB_SPEC_FILE'
const specFile = process.env[SPEC_FILE_ENV]?.trim()

const blockingError =
  prerequisiteError ??
  (!specFile || !existsSync(specFile)
    ? `Job specification fixture unavailable: set ${SPEC_FILE_ENV} to an existing document.`
    : undefined)

// AI extraction of a real job description round-trips through an external model.
const EXTRACTION_TIMEOUT_MS = 180_000

async function gotoShell(page: Page, path: string): Promise<void> {
  await page.goto(path)
  await expect(page.locator('.page')).toBeVisible({ timeout: 90_000 })
}

function organizationSelect(page: Page) {
  return page.locator('#organization-select')
}

test.describe.configure({ mode: 'serial' })

test.describe('Admin creates an organization, department, and job from a specification', () => {
  test.skip(Boolean(blockingError), blockingError ?? '')
  test.use({
    storageState: resolvedAuthStatePath ?? { cookies: [], origins: [] },
    viewport: { width: 1440, height: 900 },
  })

  let page: Page

  test.beforeAll(async ({ browser }) => {
    const context = await browser.newContext({
      storageState: resolvedAuthStatePath,
      viewport: { width: 1440, height: 900 },
    })
    await seedMsalSession(context)
    page = await context.newPage()
  })

  test.afterAll(async () => {
    await page?.context().close()
  })

  test('signs in and lands on the authenticated shell', async () => {
    await gotoShell(page, '/')
    await expect(
      page.getByRole('button', { name: /^(collapse|expand) navigation$/i }),
    ).toBeVisible()
  })

  test(`creates the "${ORGANIZATION_NAME}" organization`, async () => {
    await gotoShell(page, '/organizations')

    const select = organizationSelect(page)
    await expect(select).toBeVisible({ timeout: 60_000 })

    const existing = await select.locator('option', { hasText: ORGANIZATION_NAME }).count()
    if (existing > 0) {
      test.info().annotations.push({
        type: 'note',
        description: `${ORGANIZATION_NAME} already exists; reusing it.`,
      })
      return
    }

    const createSection = page.locator('section', {
      has: page.getByRole('heading', { name: 'Create organization' }),
    })
    await createSection.getByLabel('Organization name').fill(ORGANIZATION_NAME)
    await createSection.getByLabel('First department').fill(SEED_DEPARTMENT_NAME)
    await createSection.getByRole('button', { name: 'Create organization' }).click()

    await expect(select.locator('option', { hasText: ORGANIZATION_NAME })).toHaveCount(1, {
      timeout: 60_000,
    })
  })

  test(`adds the "${DEPARTMENT_NAME}" department`, async () => {
    const select = organizationSelect(page)
    await select.selectOption({ label: ORGANIZATION_NAME })

    const departments = page.locator('.organization-admin__department-list')
    await expect(departments).toBeVisible({ timeout: 60_000 })

    if ((await departments.getByText(DEPARTMENT_NAME, { exact: true }).count()) === 0) {
      const inlineForm = page.locator('.organization-admin__inline-form')
      await inlineForm.getByLabel('New department').fill(DEPARTMENT_NAME)
      await inlineForm.getByRole('button', { name: 'Add department' }).click()
    }

    await expect(departments.getByText(DEPARTMENT_NAME, { exact: true })).toHaveCount(1, {
      timeout: 60_000,
    })
  })

  test('creates a job from the uploaded specification', async () => {
    test.setTimeout(EXTRACTION_TIMEOUT_MS + 120_000)
    await gotoShell(page, '/')

    await page.getByRole('button', { name: /create job/i }).first().click()

    const dialog = page.getByRole('dialog')
    await expect(dialog).toBeVisible()
    await expect(dialog.getByRole('heading', { name: 'Create New Job' })).toBeVisible()

    // The spec upload is the first of the two file inputs; the second is the rubric.
    await dialog.locator('input[type="file"]').first().setInputFiles(specFile!)

    await expect(dialog.getByText(/Extracting job specification with AI/i)).toBeHidden({
      timeout: EXTRACTION_TIMEOUT_MS,
    })
    await expect(dialog.locator('text=❌')).toHaveCount(0)

    const titleInput = dialog.locator('input[placeholder="Job Title"]')
    if (!(await titleInput.inputValue())) {
      await titleInput.fill('Departmental Head: Education')
    }
    await dialog.locator('#create-job-organisation').selectOption({ label: ORGANIZATION_NAME })
    await dialog.locator('#create-job-department').selectOption({ label: DEPARTMENT_NAME })

    await dialog.getByRole('button', { name: 'Create Job', exact: true }).click()

    await expect(dialog.getByText(/job created/i)).toBeVisible({ timeout: 60_000 })
  })
})
