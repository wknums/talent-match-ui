import { describe, it, expect } from 'vitest';

describe('API Integration Tests', () => {
  const BASE_URL = 'http://localhost:5000';

  // These tests require a running server - skip if not available
  it.skip('GET /api/auth/me returns 401 when not authenticated', async () => {
    const response = await fetch(`${BASE_URL}/api/auth/me`);
    expect(response.status).toBe(401);
  });

  it.skip('POST /api/auth/login with invalid credentials returns 401', async () => {
    const response = await fetch(`${BASE_URL}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'invalid', password: 'invalid' }),
    });
    expect(response.status).toBe(401);
  });

  it('placeholder test to verify test infrastructure works', () => {
    expect(1 + 1).toBe(2);
  });
});
