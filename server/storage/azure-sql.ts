import type { StorageProvider } from './types.js'

/**
 * Azure SQL-backed key-value storage.
 * Stores KV pairs in a table with key (NVARCHAR PK) and value (NVARCHAR(MAX) as JSON).
 *
 * Requires: npm install mssql
 * Env vars: AZURE_SQL_CONNECTION_STRING
 */
export class AzureSQLStorage implements StorageProvider {
  private pool: any = null
  private connectionString: string

  constructor() {
    const connStr = process.env.AZURE_SQL_CONNECTION_STRING
    if (!connStr) {
      throw new Error(
        'AZURE_SQL_CONNECTION_STRING environment variable is required for Azure SQL storage'
      )
    }
    this.connectionString = connStr
  }

  async initialize(): Promise<void> {
    const sql = await import('mssql')
    this.pool = await sql.default.connect(this.connectionString)

    // Create the KV table if it doesn't exist
    await this.pool.request().query(`
      IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'kv_store')
      CREATE TABLE kv_store (
        [key] NVARCHAR(450) NOT NULL PRIMARY KEY,
        [value] NVARCHAR(MAX) NOT NULL,
        [updated_at] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
      )
    `)
  }

  async get<T>(key: string): Promise<T | undefined> {
    const result = await this.pool
      .request()
      .input('key', key)
      .query('SELECT [value] FROM kv_store WHERE [key] = @key')

    if (result.recordset.length === 0) return undefined
    return JSON.parse(result.recordset[0].value) as T
  }

  async set<T>(key: string, value: T): Promise<void> {
    const jsonValue = JSON.stringify(value)
    await this.pool
      .request()
      .input('key', key)
      .input('value', jsonValue)
      .query(`
        MERGE kv_store AS target
        USING (SELECT @key AS [key]) AS source
        ON target.[key] = source.[key]
        WHEN MATCHED THEN
          UPDATE SET [value] = @value, [updated_at] = GETUTCDATE()
        WHEN NOT MATCHED THEN
          INSERT ([key], [value]) VALUES (@key, @value);
      `)
  }

  async delete(key: string): Promise<void> {
    await this.pool
      .request()
      .input('key', key)
      .query('DELETE FROM kv_store WHERE [key] = @key')
  }

  async keys(): Promise<string[]> {
    const result = await this.pool
      .request()
      .query('SELECT [key] FROM kv_store ORDER BY [key]')
    return result.recordset.map((r: { key: string }) => r.key)
  }
}
