/**
 * Simple in-memory session store.
 * Replaces the KV-based AUTH_CURRENT_USER pattern.
 * Single-server development model — one session at a time.
 */
import type { User } from './middleware/auth.js'

let _currentUser: User | null = null

export function setCurrentUser(user: User | null): void {
  _currentUser = user
}

export function getCurrentUser(): User | null {
  return _currentUser
}
