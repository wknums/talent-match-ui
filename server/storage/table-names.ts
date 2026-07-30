import { isAzureSql } from './db.js'

// ---------------------------------------------------------------------------
// Centralized schema qualification for Azure SQL
//
// All repository files use T('TableName') instead of hardcoded table names.
// • Azure SQL  → [talentmatch].[TableName]  (schema-qualified)
// • SQLite     → TableName                   (unqualified, no schema support)
//
// The schema constant is defined once here. To add a new table, simply use
// T('NewTable') in your queries — no other changes required.
// ---------------------------------------------------------------------------

/** The dedicated database schema used in Azure SQL deployments. */
export const SCHEMA_NAME = 'talentmatch'

export const AUTHORIZATION_TABLES = {
  organizations: 'Organizations',
  departments: 'Departments',
  organizationMemberships: 'OrganizationMemberships',
  departmentMemberships: 'DepartmentMemberships',
  roleGroupMappings: 'RoleGroupMappings',
  roleAssignments: 'RoleAssignments',
} as const

/**
 * Return a schema-qualified table name for Azure SQL, or a plain table name
 * for SQLite.
 *
 * @example
 *   T('Applications')
 *   // Azure SQL → '[talentmatch].[Applications]'
 *   // SQLite    → 'Applications'
 */
export function T(tableName: string): string {
  return isAzureSql ? `[${SCHEMA_NAME}].[${tableName}]` : tableName
}
