// One-time helper: opens Stack B in a visible browser, waits for a manual Entra
// sign-in, then records the fixture the Playwright suite needs.
//
// Blazor's AddMsalAuthentication caches credentials in sessionStorage, which
// Playwright's storageState does not capture, so the MSAL entries and the API
// access token are written to a sibling file alongside the standard state.
import { chromium } from '@playwright/test'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { sessionStatePathFor } from './auth-state-path'

const BASE_URL_ENV = 'E2E_STACK_B_BASE_URL'
const AUTH_STATE_ENV = 'E2E_STACK_B_AUTH_STATE'
const SIGN_IN_TIMEOUT_MS = 10 * 60_000

const MSAL_KEY_PREFIXES = [
  'msal.',
  'Microsoft.AspNetCore.Components.WebAssembly.Authentication',
] as const

// Routes whose first render issues an authorized API call, which is what makes MSAL
// request the API access token this fixture needs.
const TOKEN_PRIMING_ROUTES = ['/analytics', '/users', '/organizations', '/'] as const

async function main(): Promise<void> {
  const baseUrl = process.env[BASE_URL_ENV]?.trim()
  const authStatePath = process.env[AUTH_STATE_ENV]?.trim()

  if (!baseUrl || !authStatePath) {
    console.error(
      `Set ${BASE_URL_ENV} to the running Stack B URL and ${AUTH_STATE_ENV} to the ` +
        'storage-state file to write, then rerun.',
    )
    process.exitCode = 1
    return
  }

  const resolvedStatePath = resolve(authStatePath)
  mkdirSync(dirname(resolvedStatePath), { recursive: true })

  const browser = await chromium.launch({ headless: false })
  try {
    const context = await browser.newContext({ baseURL: baseUrl })
    // tsx compiles with esbuild's keepNames, which rewrites named functions into calls to
    // a __name helper that only exists in the Node bundle, not in the page. Serialized
    // evaluate() callbacks therefore need a no-op shim.
    await context.addInitScript(
      'globalThis.__name = globalThis.__name || function (value) { return value }',
    )
    const page = await context.newPage()
    await page.goto('/')

    console.log(`Opened ${baseUrl}. Complete the Entra sign-in in the browser window.`)
    console.log('Capture completes automatically once an access token is cached.')

    // Sign-in only caches an id token. Wait for the account first so the API-token
    // acquisition below runs against a signed-in session.
    await page.waitForFunction(
      () => JSON.parse(sessionStorage.getItem('msal.account.keys') ?? '[]').length > 0,
      undefined,
      { timeout: SIGN_IN_TIMEOUT_MS, polling: 500 },
    )
    console.log('Sign-in detected. Visiting API-backed routes to force token acquisition...')

    // Blazor's AddMsalAuthentication acquires the API access token lazily, on the first
    // authorized HTTP call, so land on pages that make one instead of waiting on '/'.
    const hasAccessToken = () =>
      page.evaluate(() => {
        for (let index = 0; index < sessionStorage.length; index += 1) {
          const key = sessionStorage.key(index)
          if (!key?.startsWith('msal.token.keys.')) continue
          try {
            const tokenIndex = JSON.parse(sessionStorage.getItem(key) ?? '{}')
            if (Array.isArray(tokenIndex.accessToken) && tokenIndex.accessToken.length > 0) {
              return true
            }
          } catch {
            // A partially written index just means the acquisition is still in flight.
          }
        }
        return false
      })

    for (const route of TOKEN_PRIMING_ROUTES) {
      if (await hasAccessToken()) break
      await page.goto(route)
      await page.waitForTimeout(3_000)
    }

    if (!(await hasAccessToken())) {
      throw new Error(
        'Signed in, but no API access token was cached. Open an authorized page (for ' +
          `example ${TOKEN_PRIMING_ROUTES[0]}) in the browser window and rerun.`,
      )
    }

    const captured = await page.evaluate(prefixes => {
      const all: Record<string, string> = {}
      for (let index = 0; index < sessionStorage.length; index += 1) {
        const key = sessionStorage.key(index)
        if (key) all[key] = sessionStorage.getItem(key) ?? ''
      }

      // MSAL credential and account keys are unprefixed
      // (<homeAccountId>-<environment>-accesstoken-...), so the "msal.*.keys" indexes are
      // the only reliable way to find them. "msal.account.keys" is a flat array;
      // "msal.token.keys.<clientId>" is an object of arrays per credential type.
      const wanted = new Set(
        Object.keys(all).filter(key => prefixes.some(prefix => key.startsWith(prefix))),
      )
      const collect = (value: unknown): void => {
        if (Array.isArray(value)) {
          for (const entry of value) wanted.add(String(entry))
        } else if (value && typeof value === 'object') {
          for (const nested of Object.values(value)) collect(nested)
        }
      }
      for (const key of [...wanted]) {
        if (!key.startsWith('msal.') || !key.includes('.keys')) continue
        try {
          collect(JSON.parse(all[key] || 'null'))
        } catch {
          // A malformed index simply contributes nothing.
        }
      }

      const entries: Record<string, string> = {}
      for (const key of wanted) {
        if (all[key] !== undefined) entries[key] = all[key]
      }

      let accessToken = ''
      for (const value of Object.values(entries)) {
        let credential
        try {
          credential = JSON.parse(value || 'null')
        } catch {
          continue
        }
        if (credential?.credentialType === 'AccessToken' && credential.secret) {
          accessToken = credential.secret
        }
      }

      const accountKeys: string[] = JSON.parse(all['msal.account.keys'] || '[]')
      const accountCount = accountKeys.filter(key => entries[key] !== undefined).length

      return { origin: location.origin, entries, accessToken, accountCount }
    }, MSAL_KEY_PREFIXES)

    if (!captured.accessToken) {
      throw new Error('Signed in, but no MSAL access token was found in sessionStorage.')
    }
    if (captured.accountCount === 0) {
      throw new Error('Signed in, but no MSAL account entry was captured; renewal would fail.')
    }

    await context.storageState({ path: resolvedStatePath })
    const sessionPath = sessionStatePathFor(resolvedStatePath)
    writeFileSync(sessionPath, `${JSON.stringify(captured, null, 2)}\n`, 'utf8')

    console.log(`Wrote ${resolvedStatePath}`)
    console.log(`Wrote ${sessionPath}`)
    console.log('Both files hold live credentials. Do not commit them.')
  } finally {
    await browser.close()
  }
}

await main()
