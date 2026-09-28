/**
 * Simple in-memory session store.
 * Replaces the KV-based AUTH_CURRENT_USER pattern.
 * Single-server development model — one session at a time.
 */
import type { User } from './middleware/auth.js'

let _currentUser: User | null = null

function requireSimpleMode(): void {
  if (process.env.APP_AUTH_MODE === 'entra') {
    throw new Error('The in-memory session is unavailable in Entra authentication mode.')
  }
}

export function setCurrentUser(user: User | null): void {
  requireSimpleMode()
  _currentUser = user
}

export function getCurrentUser(): User | null {
  requireSimpleMode()
  return _currentUser
}
