/**
 * Storage provider interface - abstracts KV operations
 * over local file storage or Azure SQL.
 */
export interface StorageProvider {
  get<T>(key: string): Promise<T | undefined>
  set<T>(key: string, value: T): Promise<void>
  delete(key: string): Promise<void>
  keys(): Promise<string[]>
  initialize(): Promise<void>
}
