import type { AddressInfo } from 'node:net';
import type { Server } from 'node:http';
import { createHash } from 'crypto';
import express from 'express';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createAuthMiddleware } from '../../server/middleware/auth.js';
import { createAuthRouter } from '../../server/routes/auth.js';
import { createUsersRouter } from '../../server/routes/users.js';
import { auditService } from '../../server/services/audit.js';
import { setCurrentUser } from '../../server/session.js';
import { userRepo } from '../../server/storage/repos/index.js';
import type { StoredUser } from '../../server/storage/repos/user-repo.js';

// Test SHA-256 hash generation for passwords
function hashPassword(password: string): string {
  return createHash('sha256').update(password).digest('hex');
}

describe('Auth - SHA-256 Password Hashing', () => {
  it('generates consistent SHA-256 hash for same input', () => {
    const hash1 = hashPassword('admin123');
    const hash2 = hashPassword('admin123');
    expect(hash1).toBe(hash2);
  });

  it('generates different hashes for different passwords', () => {
    const hash1 = hashPassword('password1');
    const hash2 = hashPassword('password2');
    expect(hash1).not.toBe(hash2);
  });

  it('generates a 64-character hex string', () => {
    const hash = hashPassword('test');
    expect(hash).toHaveLength(64);
    expect(hash).toMatch(/^[0-9a-f]{64}$/);
  });

  it('handles empty string', () => {
    const hash = hashPassword('');
    expect(hash).toHaveLength(64);
  });
});

describe('Stack A simple auth mode', () => {
  const adminUser: StoredUser = {
    userId: 'simple-admin-id',
    username: 'admin',
    role: 'admin',
    authenticationProvider: 'simple',
    department: 'all',
    fullName: 'Simple Administrator',
    email: 'admin@example.com',
    createdAt: '2026-08-01T00:00:00.000Z',
    passwordHash: hashPassword('adm1n99'),
  };

  let server: Server;
  let baseUrl: string;

  beforeAll(async () => {
    const app = express();
    const authMiddleware = createAuthMiddleware({ mode: 'simple' });
    app.use(express.json());
    app.use('/api/auth', createAuthRouter({ mode: 'simple' }));
    app.use('/api/users', authMiddleware, createUsersRouter({ mode: 'simple' }));

    await new Promise<void>((resolve) => {
      server = app.listen(0, '127.0.0.1', () => {
        const address = server.address() as AddressInfo;
        baseUrl = `http://127.0.0.1:${address.port}`;
        resolve();
      });
    });
  });

  afterAll(async () => {
    await new Promise<void>((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
    });
  });

  beforeEach(() => {
    vi.restoreAllMocks();
    setCurrentUser(null);
    vi.spyOn(userRepo, 'getByUsername').mockResolvedValue(adminUser);
    vi.spyOn(userRepo, 'getById').mockResolvedValue(adminUser);
    vi.spyOn(userRepo, 'getAll').mockResolvedValue([adminUser]);
    vi.spyOn(userRepo, 'update').mockResolvedValue();
    vi.spyOn(auditService, 'appendEvent').mockResolvedValue();
  });

  it('preserves local password login and user administration', async () => {
    const loginResponse = await fetch(`${baseUrl}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'admin', password: 'adm1n99' }),
    });
    expect(loginResponse.status).toBe(200);

    const usersResponse = await fetch(`${baseUrl}/api/users`);
    expect(usersResponse.status).toBe(200);
    expect(await usersResponse.json()).toEqual([
      expect.not.objectContaining({ passwordHash: expect.anything() }),
    ]);
  });

  it('preserves the local password reset path', async () => {
    setCurrentUser(adminUser);

    const response = await fetch(`${baseUrl}/api/users/${adminUser.userId}/reset-password`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ newPassword: 'new-password99' }),
    });

    expect(response.status).toBe(200);
    expect(userRepo.update).toHaveBeenCalledWith(adminUser.userId, {
      passwordHash: hashPassword('new-password99'),
    });
  });

  it('returns a canonical correlated error when simple authentication is required', async () => {
    const response = await fetch(`${baseUrl}/api/users`);
    const body = await response.json() as Record<string, string>;

    expect(response.status).toBe(401);
    expect(body).toEqual({
      error: 'auth_required',
      message: 'Authentication is required.',
      correlationId: response.headers.get('x-correlation-id'),
    });
  });

  it.each([
    '/api/access-management/users',
    '/api/organizations/organization-id/departments',
  ])('does not expose Entra administration endpoint %s', async (path) => {
    setCurrentUser(adminUser);

    const response = await fetch(`${baseUrl}${path}`);

    expect(response.status).toBe(404);
  });
});
