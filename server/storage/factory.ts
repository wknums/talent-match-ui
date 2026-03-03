import type { StorageProvider } from './types.js'
import { LocalKVStorage } from './local-kv.js'

export async function createStorageProvider(): Promise<StorageProvider> {
  const provider = process.env.STORAGE_PROVIDER || 'local'

  let storage: StorageProvider

  switch (provider) {
    case 'azuresql': {
      // Dynamic import so mssql is only loaded when needed
      const { AzureSQLStorage } = await import('./azure-sql.js')
      storage = new AzureSQLStorage()
      break
    }
    case 'local':
    default:
      storage = new LocalKVStorage()
      break
  }

  await storage.initialize()
  console.log(`Storage provider initialized: ${provider}`)
  return storage
}
