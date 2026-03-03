import fs from 'node:fs'
import path from 'node:path'
import type { StorageProvider } from './types.js'

const DATA_DIR = path.resolve(process.cwd(), '.data')
const KV_FILE = path.join(DATA_DIR, 'kv-store.json')

/**
 * Local file-backed key-value storage.
 * Data persists in .data/kv-store.json.
 */
export class LocalKVStorage implements StorageProvider {
  private store: Record<string, unknown> = {}

  async initialize(): Promise<void> {
    if (!fs.existsSync(DATA_DIR)) {
      fs.mkdirSync(DATA_DIR, { recursive: true })
    }
    if (fs.existsSync(KV_FILE)) {
      const raw = fs.readFileSync(KV_FILE, 'utf-8')
      this.store = JSON.parse(raw)
    }
  }

  private persist(): void {
    fs.writeFileSync(KV_FILE, JSON.stringify(this.store, null, 2), 'utf-8')
  }

  async get<T>(key: string): Promise<T | undefined> {
    return this.store[key] as T | undefined
  }

  async set<T>(key: string, value: T): Promise<void> {
    this.store[key] = value
    this.persist()
  }

  async delete(key: string): Promise<void> {
    delete this.store[key]
    this.persist()
  }

  async keys(): Promise<string[]> {
    return Object.keys(this.store)
  }
}
