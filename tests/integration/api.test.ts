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

  // Analytics endpoint integration tests (require running server)
  describe('Analytics Endpoints', () => {
    it.skip('GET /api/stats/recruiters returns 401 when not authenticated', async () => {
      const response = await fetch(`${BASE_URL}/api/stats/recruiters`);
      expect(response.status).toBe(401);
    });

    it.skip('GET /api/stats/departments returns 401 when not authenticated', async () => {
      const response = await fetch(`${BASE_URL}/api/stats/departments`);
      expect(response.status).toBe(401);
    });

    it.skip('GET /api/stats/recruiters returns 200 with valid schema for admin', async () => {
      // Login as admin first
      const loginRes = await fetch(`${BASE_URL}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: 'admin', password: 'adm1n99' }),
      });
      expect(loginRes.status).toBe(200);

      const response = await fetch(`${BASE_URL}/api/stats/recruiters`);
      expect(response.status).toBe(200);

      const data = await response.json();
      expect(Array.isArray(data)).toBe(true);
      if (data.length > 0) {
        expect(data[0]).toHaveProperty('recruiterId');
        expect(data[0]).toHaveProperty('recruiterName');
        expect(data[0]).toHaveProperty('department');
        expect(data[0]).toHaveProperty('applicationsInQueue');
        expect(data[0]).toHaveProperty('manualReviewsPerformed');
        expect(data[0]).toHaveProperty('shortlistRecommendations');
        expect(data[0]).toHaveProperty('activeJobs');
      }
    });

    it.skip('GET /api/stats/departments returns 200 with valid schema for admin', async () => {
      const loginRes = await fetch(`${BASE_URL}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: 'admin', password: 'adm1n99' }),
      });
      expect(loginRes.status).toBe(200);

      const response = await fetch(`${BASE_URL}/api/stats/departments`);
      expect(response.status).toBe(200);

      const data = await response.json();
      expect(Array.isArray(data)).toBe(true);
      if (data.length > 0) {
        expect(data[0]).toHaveProperty('department');
        expect(data[0]).toHaveProperty('totalRecruiters');
        expect(data[0]).toHaveProperty('applicationsInQueue');
        expect(data[0]).toHaveProperty('recruiters');
        expect(Array.isArray(data[0].recruiters)).toBe(true);
        expect(data[0].totalRecruiters).toBe(data[0].recruiters.length);
      }
    });

    it.skip('GET /api/stats/recruiters returns 403 for business_panel role', async () => {
      // Would need a business_panel user to test
      // Login as business_panel user
      const loginRes = await fetch(`${BASE_URL}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: 'panel_user', password: 'panel123' }),
      });
      if (loginRes.status !== 200) return; // Skip if user doesn't exist

      const response = await fetch(`${BASE_URL}/api/stats/recruiters`);
      expect(response.status).toBe(403);
    });
  });
});
