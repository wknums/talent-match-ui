#!/usr/bin/env node
// Capture a Playwright storage-state fixture for the Stack B navigation E2E suite.
//
// Usage:
//   E2E_STACK_B_BASE_URL=http://localhost:5104 \
//   E2E_STACK_B_USERNAME=<username> \
//   E2E_STACK_B_PASSWORD=<password> \
//   E2E_STACK_B_AUTH_STATE=artifacts/e2e/stack-b-auth-state.json \
//   node infra/scripts/capture-stack-b-auth-state.mjs
//
// Stack B issues an HttpOnly cookie from POST /api/auth/login when APP_AUTH_MODE=simple,
// which is exactly what Playwright storage state can carry. Entra mode caches its tokens in
// sessionStorage, which Playwright storage state does not persist, so the fixture is captured
// against a simple-mode Stack B instance.
//
// No credential or endpoint value is baked into this file; every input comes from the
// environment (load it from the active environment definition file before running).

import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'

const required = (name) => {
  const value = process.env[name]?.trim()
  if (!value) {
    console.error(`Missing required environment variable: ${name}`)
    process.exit(1)
  }
  return value
}

const baseUrl = required('E2E_STACK_B_BASE_URL').replace(/\/+$/, '')
const username = required('E2E_STACK_B_USERNAME')
const password = required('E2E_STACK_B_PASSWORD')
const outputPath = resolve(required('E2E_STACK_B_AUTH_STATE'))

const response = await fetch(`${baseUrl}/api/auth/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ username, password }),
  redirect: 'manual',
})

if (!response.ok) {
  console.error(`Sign-in failed: ${response.status} ${response.statusText}`)
  process.exit(1)
}

const setCookies = response.headers.getSetCookie?.() ?? []
if (setCookies.length === 0) {
  console.error('Sign-in succeeded but returned no cookie; is APP_AUTH_MODE=simple?')
  process.exit(1)
}

const { hostname, protocol } = new URL(baseUrl)

const cookies = setCookies.map((raw) => {
  const [pair, ...attributes] = raw.split(';').map((part) => part.trim())
  const separator = pair.indexOf('=')
  const attribute = (name) =>
    attributes
      .find((entry) => entry.toLowerCase().startsWith(`${name}=`))
      ?.slice(name.length + 1)

  const sameSite = attribute('samesite')?.toLowerCase()
  return {
    name: pair.slice(0, separator),
    value: pair.slice(separator + 1),
    domain: attribute('domain')?.replace(/^\./, '') ?? hostname,
    path: attribute('path') ?? '/',
    expires: -1,
    httpOnly: attributes.some((entry) => entry.toLowerCase() === 'httponly'),
    secure: attributes.some((entry) => entry.toLowerCase() === 'secure') || protocol === 'https:',
    sameSite: sameSite === 'none' ? 'None' : sameSite === 'strict' ? 'Strict' : 'Lax',
  }
})

mkdirSync(dirname(outputPath), { recursive: true })
writeFileSync(outputPath, `${JSON.stringify({ cookies, origins: [] }, null, 2)}\n`)
console.log(`Wrote Stack B storage state with ${cookies.length} cookie(s) to ${outputPath}`)
