import { describe, it, expect } from 'vitest';
import { createHash } from 'crypto';

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
