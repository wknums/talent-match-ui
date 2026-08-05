import type { BrowserContext, Page } from '@playwright/test'
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { sessionStatePathFor } from './auth-state-path'

export const AUTH_STATE_ENV = 'E2E_STACK_B_AUTH_STATE'
export const BASE_URL_ENV = 'E2E_STACK_B_BASE_URL'

const authStatePath = process.env[AUTH_STATE_ENV]?.trim()

export const resolvedAuthStatePath = authStatePath ? resolve(authStatePath) : undefined

export const prerequisiteError = !process.env[BASE_URL_ENV]?.trim()
  ? `Stack B E2E prerequisite unavailable: set ${BASE_URL_ENV} to the running Stack B URL.`
  : !resolvedAuthStatePath || !existsSync(resolvedAuthStatePath)
    ? `Stack B auth fixture unavailable: set ${AUTH_STATE_ENV} to an existing Playwright storage-state file.`
    : undefined

// storageState only carries cookies and localStorage. Blazor's MSAL cache lives in
// sessionStorage, so the capture helper records it in a sibling file.
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

// Cached access tokens are deliberately withheld: the API rejects tokens older than 15
// minutes, so MSAL must redeem the restored refresh token for a freshly issued one.
export async function seedMsalSession(target: Page | BrowserContext): Promise<void> {
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
      // Nothing to seed on documents the flow never asserts against.
    }
  }, capturedSession.entries)
}
