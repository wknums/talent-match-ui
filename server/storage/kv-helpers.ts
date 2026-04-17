import type { StorageProvider } from './types.js'

export async function getArray<T>(storage: StorageProvider, key: string): Promise<T[]> {
  let value = await storage.get<T[] | string>(key)
  // Guard against double-serialised values (stored as JSON string instead of array)
  if (typeof value === 'string') {
    try { value = JSON.parse(value) } catch { return [] }
  }
  return Array.isArray(value) ? value : []
}

export async function setArray<T>(storage: StorageProvider, key: string, value: T[]): Promise<void> {
  await storage.set(key, value)
}

export async function pushToArray<T>(storage: StorageProvider, key: string, item: T): Promise<void> {
  const arr = await getArray<T>(storage, key)
  arr.push(item)
  await storage.set(key, arr)
}

export async function removeFromArray<T>(
  storage: StorageProvider,
  key: string,
  predicate: (item: T) => boolean,
): Promise<T[]> {
  const arr = await getArray<T>(storage, key)
  const removed: T[] = []
  const kept: T[] = []
  for (const item of arr) {
    if (predicate(item)) {
      removed.push(item)
    } else {
      kept.push(item)
    }
  }
  await storage.set(key, kept)
  return removed
}

export async function getOrDefault<T>(storage: StorageProvider, key: string, defaultValue: T): Promise<T> {
  const value = await storage.get<T>(key)
  return value ?? defaultValue
}
