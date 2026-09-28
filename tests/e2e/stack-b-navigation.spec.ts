import AxeBuilder from '@axe-core/playwright'
import {
  expect,
  test,
  type APIRequestContext,
  type Browser,
  type BrowserContext,
  type Page,
} from '@playwright/test'
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { sessionStatePathFor } from './auth-state-path'

const QUICKSTART_VIEWPORTS = [
  { name: 'wide desktop', width: 1440, height: 900, compact: false },
  { name: 'desktop', width: 1024, height: 768, compact: false },
  { name: 'compact boundary', width: 991, height: 768, compact: true },
  { name: 'tablet portrait', width: 768, height: 1024, compact: true },
  { name: 'mobile', width: 390, height: 844, compact: true },
  { name: 'small mobile', width: 320, height: 568, compact: true },
] as const

const DYNAMIC_ROUTE_FALLBACK_ID = '00000000-0000-0000-0000-000000000001'
const AUTH_STATE_ENV = 'E2E_STACK_B_AUTH_STATE'
const BASE_URL_ENV = 'E2E_STACK_B_BASE_URL'
const authStatePath = process.env[AUTH_STATE_ENV]?.trim()
const resolvedAuthStatePath = authStatePath ? resolve(authStatePath) : undefined
const prerequisiteError = !process.env[BASE_URL_ENV]?.trim()
  ? `Stack B E2E prerequisite unavailable: set ${BASE_URL_ENV} to the running Stack B URL.`
  : !resolvedAuthStatePath || !existsSync(resolvedAuthStatePath)
    ? `Stack B auth fixture unavailable: set ${AUTH_STATE_ENV} to an existing Playwright storage-state file.`
    : undefined

// storageState only carries cookies and localStorage. Blazor's MSAL cache lives
// in sessionStorage, so the capture helper records it separately.
type CapturedSession = {
  origin: string
  entries: Record<string, string>
  accessToken: string
}

const capturedSession: CapturedSession | undefined = (() => {
  if (!resolvedAuthStatePath) return undefined
  const sessionPath = sessionStatePathFor(resolvedAuthStatePath)
  if (!existsSync(sessionPath)) return undefined
  return JSON.parse(readFileSync(sessionPath, 'utf8')) as CapturedSession
})()

type Actor = {
  role: string
}

type RouteFixture = {
  jobId: string
  applicationId: string
  hasManualReviewData: boolean
}

type ShellRoute = {
  name: string
  path: string
}

const staticShellRoutes: ShellRoute[] = [
  { name: 'dashboard', path: '/' },
  { name: 'analytics', path: '/analytics' },
  { name: 'user administration', path: '/users' },
  { name: 'organization administration', path: '/organizations' },
  { name: 'failure queue', path: '/failure-queue' },
  { name: 'not found', path: '/not-found' },
]

function shellRoutes(fixture: RouteFixture): ShellRoute[] {
  return [
    ...staticShellRoutes,
    { name: 'job detail', path: `/jobs/${fixture.jobId}` },
    { name: 'application detail', path: `/applications/${fixture.applicationId}` },
    { name: 'manual review', path: `/manual-review/${fixture.applicationId}` },
  ]
}

function navigationToggle(page: Page) {
  return page.getByRole('button', { name: /^(collapse|expand) navigation$/i })
}

function navigationBackdrop(page: Page) {
  return page.locator('[data-testid="navigation-backdrop"], .navigation-backdrop')
}

async function gotoAuthenticatedShell(page: Page, path: string): Promise<void> {
  const response = await page.goto(path, { waitUntil: 'domcontentloaded' })
  expect(response, `Expected Stack B to respond for ${path}`).not.toBeNull()
  expect(response?.status(), `Expected Stack B not to fail for ${path}`).toBeLessThan(500)
  await expect(page.locator('.page'), `Expected the authenticated shell at ${path}`).toBeVisible()
  await expect(navigationToggle(page)).toBeVisible()
}

async function assertToggleContract(page: Page): Promise<void> {
  const toggle = navigationToggle(page)
  await expect(toggle).toHaveAttribute('aria-expanded', /^(true|false)$/)
  await expect(toggle).toHaveAttribute('aria-controls', /\S+/)

  const controlledId = await toggle.getAttribute('aria-controls')
  expect(controlledId).toBeTruthy()
  await expect(page.locator(`#${controlledId}`)).toHaveCount(1)
}

async function setNavigationExpanded(page: Page, expanded: boolean): Promise<void> {
  const toggle = navigationToggle(page)
  const current = (await toggle.getAttribute('aria-expanded')) === 'true'
  if (current !== expanded) {
    await toggle.click()
  }
  await expect(toggle).toHaveAttribute('aria-expanded', String(expanded))
}

async function assertNoPageLevelHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => ({
    clientWidth: document.documentElement.clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
  }))
  expect(
    overflow.scrollWidth,
    `Page-level horizontal overflow: ${overflow.scrollWidth}px content in ${overflow.clientWidth}px viewport`,
  ).toBeLessThanOrEqual(overflow.clientWidth + 1)
}

async function assertAxeAndContrastFloor(page: Page): Promise<void> {
  const accessibility = await new AxeBuilder({ page }).analyze()
  const seriousOrCritical = accessibility.violations.filter(
    ({ impact }) => impact === 'serious' || impact === 'critical',
  )
  expect(seriousOrCritical, 'Expected no serious or critical axe violations').toEqual([])

  const contrast = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze()
  expect(
    contrast.violations,
    'Expected visible text to satisfy the constitution 4.5:1 colour-contrast floor',
  ).toEqual([])
}

async function loadActor(request: APIRequestContext): Promise<Actor | undefined> {
  const response = await request.get('/api/auth/me', { failOnStatusCode: false })
  if (response.status() === 401 || response.status() === 403) return undefined
  expect(response.ok(), 'The authenticated actor endpoint must succeed').toBe(true)

  const body = await response.json()
  if (!body) return undefined
  return { role: String(body.role ?? body.globalRole ?? '').toLowerCase() }
}

async function loadRouteFixture(request: APIRequestContext): Promise<RouteFixture> {
  const jobsResponse = await request.get('/api/jobs', { failOnStatusCode: false })
  expect(jobsResponse.ok(), 'The authenticated route fixture requires GET /api/jobs').toBe(true)
  const jobs = (await jobsResponse.json()) as Array<{ id?: string }>
  const jobId = jobs[0]?.id ?? DYNAMIC_ROUTE_FALLBACK_ID

  const applicationsResponse = await request.get(`/api/jobs/${jobId}/applications`, {
    failOnStatusCode: false,
  })
  const applications = applicationsResponse.ok()
    ? ((await applicationsResponse.json()) as Array<{ id?: string }>)
    : []
  const applicationId = applications[0]?.id ?? DYNAMIC_ROUTE_FALLBACK_ID

  return {
    jobId,
    applicationId,
    hasManualReviewData: Boolean(applications[0]?.id),
  }
}

// Seed only missing keys so the suite's own sessionStorage assertions are left alone.
// Cached access tokens are deliberately withheld: the API rejects tokens older than 15
// minutes, so MSAL must redeem the restored refresh token for a freshly issued one.
async function seedMsalSession(target: Page | BrowserContext): Promise<void> {
  if (!capturedSession) return
  await target.addInitScript(entries => {
    // about:blank and other opaque-origin documents deny sessionStorage outright.
    try {
      for (const [key, value] of Object.entries(entries)) {
        if (key.includes('-accesstoken-')) continue
        if (sessionStorage.getItem(key) !== null) continue
        if (key.startsWith('msal.token.keys.')) {
          sessionStorage.setItem(key, JSON.stringify({ ...JSON.parse(value), accessToken: [] }))
          continue
        }
        sessionStorage.setItem(key, value)
      }
    } catch {
      // Nothing to seed on documents the suite never asserts against.
    }
  }, capturedSession.entries)
}

// The API rejects tokens older than 15 minutes, so a cached token that is merely
// unexpired is not good enough. Drop the cached access tokens to force MSAL to redeem
// the restored refresh token, then read the freshly issued one.
const MAX_TOKEN_AGE_MS = 10 * 60_000

async function mintAccessToken(browser: Browser): Promise<string | undefined> {
  const context = await browser.newContext({ storageState: resolvedAuthStatePath })
  try {
    await seedMsalSession(context)

    const page = await context.newPage()
    // MSAL only requests an API token when a page actually calls the API.
    await page.goto('/analytics')

    return await page.evaluate(
      async maxAgeMs => {
        const deadline = Date.now() + 60_000
        while (Date.now() < deadline) {
          for (let index = 0; index < sessionStorage.length; index += 1) {
            const key = sessionStorage.key(index)
            if (!key) continue
            let credential
            try {
              credential = JSON.parse(sessionStorage.getItem(key) ?? 'null')
            } catch {
              continue
            }
            if (credential?.credentialType !== 'AccessToken' || !credential.secret) continue
            const claims = JSON.parse(atob(credential.secret.split('.')[1]))
            if (Number(claims.iat) * 1000 < Date.now() - maxAgeMs) continue
            return credential.secret as string
          }
          await new Promise(resolve => setTimeout(resolve, 500))
        }
        return undefined
      },
      MAX_TOKEN_AGE_MS,
    )
  } finally {
    await context.close()
  }
}

test.describe('Stack B authenticated navigation shell', () => {
  test.skip(Boolean(prerequisiteError), prerequisiteError ?? '')
  test.use({
    storageState: resolvedAuthStatePath ?? { cookies: [], origins: [] },
  })

  test.beforeEach(async ({ page }) => {
    await seedMsalSession(page)
  })

  let actor: Actor
  let routeFixture: RouteFixture
  let api: APIRequestContext

  test.beforeAll(async ({ browser, playwright }) => {
    // Renewing the token means launching a browser and loading the Blazor WASM shell,
    // which comfortably exceeds the default per-hook budget.
    test.setTimeout(180_000)
    const baseURL = process.env[BASE_URL_ENV]!.trim()

    const accessToken = await mintAccessToken(browser)
    if (!accessToken) {
      test.skip(true, `Stack B auth fixture could not renew an access token: ${AUTH_STATE_ENV}`)
      return
    }

    api = await playwright.request.newContext({
      baseURL,
      extraHTTPHeaders: { Authorization: `Bearer ${accessToken}` },
    })

    try {
      const availability = await api.get('/', { failOnStatusCode: false, timeout: 5_000 })
      expect(availability.status(), 'The Stack B base URL returned a server error').toBeLessThan(500)
    } catch {
      test.skip(true, `Stack B base URL unavailable: ${baseURL}`)
    }

    const loadedActor = await loadActor(api)
    if (!loadedActor?.role) {
      test.skip(true, `Stack B auth fixture unavailable or unauthorized: ${AUTH_STATE_ENV}`)
      return
    }

    actor = loadedActor
    routeFixture = await loadRouteFixture(api)
  })

  test.afterAll(async () => {
    await api?.dispose()
  })

  for (const viewport of QUICKSTART_VIEWPORTS) {
    test(`${viewport.name} ${viewport.width}x${viewport.height} route inventory has an accessible control, no page overflow, and passes axe`, async ({ page }) => {
      await page.setViewportSize(viewport)

      for (const route of shellRoutes(routeFixture)) {
        await test.step(route.name, async () => {
          await gotoAuthenticatedShell(page, route.path)
          await assertToggleContract(page)
          await assertNoPageLevelHorizontalOverflow(page)
          await assertAxeAndContrastFloor(page)
        })
      }
    })
  }

  for (const viewport of QUICKSTART_VIEWPORTS.filter(({ compact }) => !compact)) {
    test(`${viewport.name} collapse releases sidebar width within one second`, async ({ page }) => {
      await page.setViewportSize(viewport)
      await gotoAuthenticatedShell(page, '/')
      await setNavigationExpanded(page, true)

      const content = page.locator('main > article.content')
      const sidebar = page.locator('.sidebar')
      const expandedContentWidth = (await content.boundingBox())?.width ?? 0
      expect((await sidebar.boundingBox())?.width ?? 0).toBeGreaterThan(0)

      await navigationToggle(page).click()
      await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'false')
      await expect
        .poll(async () => (await sidebar.boundingBox())?.width ?? 0, { timeout: 1_000 })
        .toBeLessThanOrEqual(1)
      await expect
        .poll(async () => (await content.boundingBox())?.width ?? 0, { timeout: 1_000 })
        .toBeGreaterThan(expandedContentWidth)
      await assertNoPageLevelHorizontalOverflow(page)
    })
  }

  for (const viewport of QUICKSTART_VIEWPORTS.filter(({ compact }) => compact)) {
    test(`${viewport.name} uses a width-neutral overlay closed by Escape and backdrop`, async ({ page }) => {
      await page.setViewportSize(viewport)
      await gotoAuthenticatedShell(page, '/')
      await setNavigationExpanded(page, false)

      const main = page.locator('.page > main')
      const closedWidth = (await main.boundingBox())?.width ?? 0
      await navigationToggle(page).click()
      await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'true')
      await expect(navigationBackdrop(page)).toBeVisible()
      expect((await main.boundingBox())?.width ?? 0).toBeGreaterThanOrEqual(closedWidth - 1)

      await page.keyboard.press('Escape')
      await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'false')
      await expect(navigationBackdrop(page)).toBeHidden()

      await navigationToggle(page).click()
      await expect(navigationBackdrop(page)).toBeVisible()
      // The drawer sits above the backdrop on the left, so dismiss from the far corner.
      await navigationBackdrop(page).click({
        position: { x: viewport.width - 2, y: viewport.height - 2 },
      })
      await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'false')
    })
  }

  test('desktop preference survives deep links, history, and same-tab reload while compact open state does not', async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 })
    await gotoAuthenticatedShell(page, '/')
    await setNavigationExpanded(page, false)

    await gotoAuthenticatedShell(page, `/applications/${routeFixture.applicationId}`)
    await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'false')
    await page.goBack({ waitUntil: 'domcontentloaded' })
    await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'false')
    await page.reload({ waitUntil: 'domcontentloaded' })
    await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'false')

    await page.setViewportSize({ width: 390, height: 844 })
    await navigationToggle(page).click()
    await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'true')
    await page.reload({ waitUntil: 'domcontentloaded' })
    await expect(navigationToggle(page)).toHaveAttribute('aria-expanded', 'false')
  })

  test('navigation toggles preserve the active Manual Review route and unsaved text', async ({ page }) => {
    expect(
      routeFixture.hasManualReviewData,
      'The Stack B auth fixture must expose at least one job application for the unsaved-input scenario',
    ).toBe(true)

    await page.setViewportSize({ width: 1440, height: 900 })
    const path = `/manual-review/${routeFixture.applicationId}`
    await gotoAuthenticatedShell(page, path)
    const overallComment = page
      .locator('label', { hasText: 'Overall Comment' })
      .locator('..')
      .locator('textarea')
    await expect(overallComment).toBeVisible()

    const unsavedText = `Unsaved navigation check ${Date.now()}`
    await overallComment.fill(unsavedText)
    await setNavigationExpanded(page, false)
    await setNavigationExpanded(page, true)

    expect(new URL(page.url()).pathname).toBe(path)
    await expect(overallComment).toHaveValue(unsavedText)
  })

  test('200% zoom equivalent reflows every shell route without page-level horizontal overflow', async ({ page }) => {
    // Browser zoom halves the CSS viewport. This models 200% zoom for the 1440x900 baseline.
    await page.setViewportSize({ width: 720, height: 450 })

    for (const route of shellRoutes(routeFixture)) {
      await test.step(route.name, async () => {
        await gotoAuthenticatedShell(page, route.path)
        await assertToggleContract(page)
        await assertNoPageLevelHorizontalOverflow(page)
        await assertAxeAndContrastFloor(page)
      })
    }
  })

  test('expanded navigation exposes exactly the current actor authorized links', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 })
    await gotoAuthenticatedShell(page, '/')
    await setNavigationExpanded(page, true)

    const authConfigResponse = await page.request.get('/api/auth/config')
    expect(authConfigResponse.ok()).toBe(true)
    const authConfig = (await authConfigResponse.json()) as { authMode?: string }
    const isEntra = authConfig.authMode?.toLowerCase() === 'entra'
    const expectedPaths = ['/']
    if (actor.role === 'admin' || (isEntra && actor.role === 'organization_admin')) {
      expectedPaths.push('/users')
    }
    if (isEntra && (actor.role === 'admin' || actor.role === 'organization_admin')) {
      expectedPaths.push('/organizations')
    }
    if (actor.role === 'admin') {
      expectedPaths.push('/failure-queue')
    }

    const actualPaths = await page
      .getByRole('navigation')
      .getByRole('link')
      .evaluateAll((links) => links.map((link) => new URL((link as HTMLAnchorElement).href).pathname))
    expect([...new Set(actualPaths)].sort()).toEqual(expectedPaths.sort())
  })
})