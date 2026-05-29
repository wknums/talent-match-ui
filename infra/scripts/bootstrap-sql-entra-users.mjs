#!/usr/bin/env node

import sql from 'mssql'
import { DefaultAzureCredential } from '@azure/identity'

const server = process.env.SQL_SERVER_FQDN
const database = process.env.SQL_DATABASE_NAME
const identityNames = [
  process.env.STACK_A_IDENTITY_NAME,
  process.env.STACK_B_IDENTITY_NAME,
].filter(Boolean)

if (!server || !database) {
  throw new Error('SQL_SERVER_FQDN and SQL_DATABASE_NAME are required for Azure SQL bootstrap.')
}

if (identityNames.length === 0) {
  throw new Error('At least one managed identity name is required for Azure SQL bootstrap.')
}

const credential = new DefaultAzureCredential()
const token = await credential.getToken('https://database.windows.net/.default')

if (!token?.token) {
  throw new Error('Failed to acquire an Azure SQL access token from DefaultAzureCredential.')
}

const pool = await sql.connect({
  server,
  database,
  connectionTimeout: 90_000,
  requestTimeout: 180_000,
  options: {
    encrypt: true,
    trustServerCertificate: false,
  },
  authentication: {
    type: 'azure-active-directory-access-token',
    options: {
      token: token.token,
    },
  },
})

try {
  for (const identityName of identityNames) {
    const escapedIdentityName = identityName.replace(/]/g, ']]')
    const identityLiteral = identityName.replace(/'/g, "''")

    const query = `
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'${identityLiteral}')
    CREATE USER [${escapedIdentityName}] FROM EXTERNAL PROVIDER;

IF NOT EXISTS (
    SELECT 1
    FROM sys.database_role_members drm
    JOIN sys.database_principals role_p ON drm.role_principal_id = role_p.principal_id
    JOIN sys.database_principals member_p ON drm.member_principal_id = member_p.principal_id
    WHERE role_p.name = N'db_datareader'
      AND member_p.name = N'${identityLiteral}'
)
    ALTER ROLE [db_datareader] ADD MEMBER [${escapedIdentityName}];

IF NOT EXISTS (
    SELECT 1
    FROM sys.database_role_members drm
    JOIN sys.database_principals role_p ON drm.role_principal_id = role_p.principal_id
    JOIN sys.database_principals member_p ON drm.member_principal_id = member_p.principal_id
    WHERE role_p.name = N'db_datawriter'
      AND member_p.name = N'${identityLiteral}'
)
    ALTER ROLE [db_datawriter] ADD MEMBER [${escapedIdentityName}];

IF NOT EXISTS (
    SELECT 1
    FROM sys.database_role_members drm
    JOIN sys.database_principals role_p ON drm.role_principal_id = role_p.principal_id
    JOIN sys.database_principals member_p ON drm.member_principal_id = member_p.principal_id
    WHERE role_p.name = N'db_ddladmin'
      AND member_p.name = N'${identityLiteral}'
)
    ALTER ROLE [db_ddladmin] ADD MEMBER [${escapedIdentityName}];
`

    await pool.request().query(query)
    console.log(`[sql-bootstrap] Ensured Azure SQL user and roles for ${identityName}`)
  }
} finally {
  await pool.close()
}
