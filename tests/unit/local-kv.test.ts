import { describe, it, expect, beforeEach, vi } from 'vitest';
import { LocalKVStorage } from '../../server/storage/local-kv.js';

// Mock the fs module to avoid actual file system operations
vi.mock('node:fs', () => ({
  default: {
    existsSync: vi.fn().mockReturnValue(false),
    mkdirSync: vi.fn(),
    readFileSync: vi.fn(),
    writeFileSync: vi.fn(),
  },
}));

describe('Local KV Storage', () => {
  let kv: LocalKVStorage;

  beforeEach(async () => {
    kv = new LocalKVStorage();
    await kv.initialize();
  });

  it('should store and retrieve a value', async () => {
    await kv.set('name', 'Alice');
    const value = await kv.get<string>('name');
    expect(value).toBe('Alice');
  });

  it('should return undefined for missing keys', async () => {
    const value = await kv.get('nonexistent');
    expect(value).toBeUndefined();
  });

  it('should delete a key', async () => {
    await kv.set('toDelete', 'value');
    await kv.delete('toDelete');
    const value = await kv.get('toDelete');
    expect(value).toBeUndefined();
  });

  it('should list keys', async () => {
    await kv.set('key1', 'val1');
    await kv.set('key2', 'val2');
    const allKeys = await kv.keys();
    expect(allKeys).toContain('key1');
    expect(allKeys).toContain('key2');
  });

  it('should store and retrieve JSON values', async () => {
    const data = { id: 1, name: 'Test', tags: ['a', 'b'] };
    await kv.set('jsonKey', data);
    const result = await kv.get<typeof data>('jsonKey');
    expect(result).toEqual(data);
  });

  it('should overwrite existing keys', async () => {
    await kv.set('key', 'original');
    await kv.set('key', 'updated');
    const value = await kv.get<string>('key');
    expect(value).toBe('updated');
  });
});
