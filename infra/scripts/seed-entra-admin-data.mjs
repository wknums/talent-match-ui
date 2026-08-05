#!/usr/bin/env node

import { randomUUID } from 'node:crypto'
import { readFileSync, unlinkSync } from 'node:fs'
import { pathToFileURL } from 'node:url'

const requiredConfigurationFields = [
  'tenantId',
  'objectId',
  'username',
  'fullName',
  'email',
  'organizationName',
  'departmentName',
]

export function validateBootstrapConfiguration(configuration) {
  const missingFields = requiredConfigurationFields.filter(
    field => !configuration[field]?.trim(),
  )

  if (missingFields.length > 0) {
    throw new Error(`Missing bootstrap configuration: ${missingFields.join(', ')}`)
  }

  return configuration
}

export function buildBootstrapSql(mode) {
  if (mode === 'check') return checkSql
  if (mode === 'apply') return applySql
  throw new Error(`Unsupported bootstrap mode: ${mode}`)
}

const checkSql = `
SELECT
  CAST(CASE WHEN bootstrapUser.Id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS userReady,
  CAST(CASE WHEN organization.Id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS organizationReady,
  CAST(CASE WHEN department.Id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS departmentReady,
  CAST(CASE WHEN organizationMembership.Id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS organizationMembershipReady,
  CAST(CASE WHEN departmentMembership.Id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS departmentMembershipReady,
  CAST(CASE WHEN roleAssignment.Id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS roleAssignmentReady,
  bootstrapUser.Id AS userId,
  bootstrapUser.AuthorizationVersion AS authorizationVersion,
  organization.Id AS organizationId,
  department.Id AS departmentId,
  roleAssignment.Id AS roleAssignmentId
FROM (VALUES (1)) AS seed(value)
OUTER APPLY (
  SELECT TOP (1) Id, AuthorizationVersion
  FROM [talentmatch].[Users]
  WHERE AuthenticationProvider = 'entra'
    AND EntraTenantId = @tenantId
    AND EntraObjectId = @objectId
    AND IsActive = 1
) AS bootstrapUser
OUTER APPLY (
  SELECT TOP (1) Id
  FROM [talentmatch].[Organizations]
  WHERE Name = @organizationName AND Status = 'active'
) AS organization
OUTER APPLY (
  SELECT TOP (1) Id
  FROM [talentmatch].[Departments]
  WHERE OrganizationId = organization.Id
    AND Name = @departmentName
    AND Status = 'active'
) AS department
OUTER APPLY (
  SELECT TOP (1) Id
  FROM [talentmatch].[DepartmentMemberships]
  WHERE UserId = bootstrapUser.Id
    AND OrganizationId = organization.Id
    AND DepartmentId = department.Id
    AND Status = 'active'
) AS departmentMembership
OUTER APPLY (
  SELECT TOP (1) Id
  FROM [talentmatch].[OrganizationMemberships]
  WHERE UserId = bootstrapUser.Id
    AND OrganizationId = organization.Id
    AND DefaultDepartmentMembershipId = departmentMembership.Id
    AND Status = 'active'
) AS organizationMembership
OUTER APPLY (
  SELECT TOP (1) Id
  FROM [talentmatch].[RoleAssignments]
  WHERE TenantId = @tenantId
    AND UserObjectId = @objectId
    AND Source = 'bootstrap'
    AND Role = 'admin'
    AND OrganizationId IS NULL
    AND DepartmentId IS NULL
    AND Status = 'active'
    AND UserId = bootstrapUser.Id
) AS roleAssignment;
`

const applySql = `
SET XACT_ABORT ON;
BEGIN TRANSACTION;

BEGIN TRY
  DECLARE @changed bit = 0;
  DECLARE @userId nvarchar(36);
  DECLARE @organizationId nvarchar(36);
  DECLARE @departmentId nvarchar(36);
  DECLARE @organizationMembershipId nvarchar(36);
  DECLARE @departmentMembershipId nvarchar(36);
  DECLARE @roleAssignmentId nvarchar(36);
  DECLARE @lockResult int;
  DECLARE @lockResource nvarchar(255) = CONCAT('talentmatch:entra-bootstrap:', @tenantId);

  EXEC @lockResult = sys.sp_getapplock
    @Resource = @lockResource,
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = 60000;
  IF @lockResult < 0
    THROW 51002, 'Could not acquire the bootstrap authorization lock.', 1;

  SELECT @userId = Id
  FROM [talentmatch].[Users] WITH (UPDLOCK, HOLDLOCK)
  WHERE AuthenticationProvider = 'entra'
    AND EntraTenantId = @tenantId
    AND EntraObjectId = @objectId;

  IF @userId IS NULL
  BEGIN
    IF EXISTS (SELECT 1 FROM [talentmatch].[Users] WHERE Username = @username)
      THROW 51001, 'The bootstrap username belongs to another application identity.', 1;

    SET @userId = @newUserId;
    INSERT INTO [talentmatch].[Users] (
      Id, Username, Role, FullName, Email, Department, PasswordHash, CreatedAt,
      PasswordResetRequired, AuthenticationProvider, EntraTenantId, EntraObjectId, IsActive
    ) VALUES (
      @userId, @username, 'admin', @fullName, @email, @departmentName, NULL, SYSUTCDATETIME(),
      0, 'entra', @tenantId, @objectId, 1
    );
    SET @changed = 1;
  END
  ELSE IF EXISTS (
    SELECT 1 FROM [talentmatch].[Users]
    WHERE Id = @userId
      AND (Username <> @username OR Role <> 'admin' OR FullName <> @fullName
        OR Email <> @email OR Department <> @departmentName OR IsActive <> 1)
  )
  BEGIN
    UPDATE [talentmatch].[Users]
    SET Username = @username, Role = 'admin', FullName = @fullName, Email = @email,
      Department = @departmentName, PasswordHash = NULL, PasswordResetRequired = 0,
      IsActive = 1
    WHERE Id = @userId;
    SET @changed = 1;
  END;

  SELECT @organizationId = Id
  FROM [talentmatch].[Organizations] WITH (UPDLOCK, HOLDLOCK)
  WHERE Name = @organizationName AND Status = 'active';

  IF @organizationId IS NULL
  BEGIN
    SET @organizationId = @newOrganizationId;
    INSERT INTO [talentmatch].[Organizations] (Id, Name, Status, CreatedAt, UpdatedAt, UpdatedBy)
    VALUES (@organizationId, @organizationName, 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), @objectId);
    SET @changed = 1;
  END;

  SELECT @departmentId = Id
  FROM [talentmatch].[Departments] WITH (UPDLOCK, HOLDLOCK)
  WHERE OrganizationId = @organizationId AND Name = @departmentName AND Status = 'active';

  IF @departmentId IS NULL
  BEGIN
    SET @departmentId = @newDepartmentId;
    INSERT INTO [talentmatch].[Departments] (Id, OrganizationId, Name, Status, CreatedAt, UpdatedAt, UpdatedBy)
    VALUES (@departmentId, @organizationId, @departmentName, 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), @objectId);
    SET @changed = 1;
  END;

  SELECT TOP (1) @departmentMembershipId = Id
  FROM [talentmatch].[DepartmentMemberships] WITH (UPDLOCK, HOLDLOCK)
  WHERE UserId = @userId AND OrganizationId = @organizationId AND DepartmentId = @departmentId
  ORDER BY CASE WHEN Status = 'active' THEN 0 ELSE 1 END, EffectiveAt DESC;

  IF @departmentMembershipId IS NULL
  BEGIN
    SET @departmentMembershipId = @newDepartmentMembershipId;
    INSERT INTO [talentmatch].[DepartmentMemberships] (Id, UserId, OrganizationId, DepartmentId, Status, EffectiveAt, RevokedAt, UpdatedBy)
    VALUES (@departmentMembershipId, @userId, @organizationId, @departmentId, 'active', SYSUTCDATETIME(), NULL, @objectId);
    SET @changed = 1;
  END
  ELSE IF EXISTS (
    SELECT 1 FROM [talentmatch].[DepartmentMemberships]
    WHERE Id = @departmentMembershipId AND (Status <> 'active' OR OrganizationId <> @organizationId)
  )
  BEGIN
    UPDATE [talentmatch].[DepartmentMemberships]
    SET Status = 'active', EffectiveAt = SYSUTCDATETIME(),
      RevokedAt = NULL, UpdatedBy = @objectId
    WHERE Id = @departmentMembershipId;
    SET @changed = 1;
  END;

  SELECT TOP (1) @organizationMembershipId = Id
  FROM [talentmatch].[OrganizationMemberships] WITH (UPDLOCK, HOLDLOCK)
  WHERE UserId = @userId AND OrganizationId = @organizationId
  ORDER BY CASE WHEN Status = 'active' THEN 0 ELSE 1 END, EffectiveAt DESC;

  IF @organizationMembershipId IS NULL
  BEGIN
    SET @organizationMembershipId = @newOrganizationMembershipId;
    INSERT INTO [talentmatch].[OrganizationMemberships] (
      Id, UserId, OrganizationId, DefaultDepartmentMembershipId, Status, EffectiveAt, RevokedAt, UpdatedBy
    ) VALUES (
      @organizationMembershipId, @userId, @organizationId, @departmentMembershipId,
      'active', SYSUTCDATETIME(), NULL, @objectId
    );
    SET @changed = 1;
  END
  ELSE IF EXISTS (
    SELECT 1 FROM [talentmatch].[OrganizationMemberships]
    WHERE Id = @organizationMembershipId
      AND (Status <> 'active'
        -- A membership predating the default column holds NULL, which "<>" alone cannot detect.
        OR DefaultDepartmentMembershipId IS NULL
        OR DefaultDepartmentMembershipId <> @departmentMembershipId)
  )
  BEGIN
    UPDATE [talentmatch].[OrganizationMemberships]
    SET DefaultDepartmentMembershipId = @departmentMembershipId,
      Status = 'active', EffectiveAt = SYSUTCDATETIME(), RevokedAt = NULL, UpdatedBy = @objectId
    WHERE Id = @organizationMembershipId;
    SET @changed = 1;
  END;

  IF EXISTS (
    SELECT 1 FROM [talentmatch].[RoleAssignments] WITH (UPDLOCK, HOLDLOCK)
    WHERE TenantId = @tenantId AND Source = 'bootstrap' AND Status = 'active'
      AND UserObjectId <> @objectId
  )
    THROW 51003, 'Another active bootstrap administrator already exists for this tenant.', 1;

  SELECT TOP (1) @roleAssignmentId = Id
  FROM [talentmatch].[RoleAssignments] WITH (UPDLOCK, HOLDLOCK)
  WHERE TenantId = @tenantId
    AND UserObjectId = @objectId
    AND Source = 'bootstrap'
    AND Role = 'admin'
    AND OrganizationId IS NULL
    AND DepartmentId IS NULL
  ORDER BY CASE WHEN Status = 'active' THEN 0 ELSE 1 END, EffectiveAt DESC;

  IF @roleAssignmentId IS NULL
  BEGIN
    SET @roleAssignmentId = @newRoleAssignmentId;
    INSERT INTO [talentmatch].[RoleAssignments] (
      Id, UserId, TenantId, UserObjectId, Role, OrganizationId, DepartmentId,
      RoleGroupMappingId, Source, Status, EffectiveAt, RevokedAt, CreatedAt, UpdatedAt, UpdatedBy
    ) VALUES (
      @roleAssignmentId, @userId, @tenantId, @objectId, 'admin', NULL, NULL,
      NULL, 'bootstrap', 'active', SYSUTCDATETIME(), NULL, SYSUTCDATETIME(), SYSUTCDATETIME(), @objectId
    );
    SET @changed = 1;
  END
  ELSE IF EXISTS (
    SELECT 1 FROM [talentmatch].[RoleAssignments]
    WHERE Id = @roleAssignmentId AND (UserId <> @userId OR Status <> 'active')
  )
  BEGIN
    UPDATE [talentmatch].[RoleAssignments]
    SET UserId = @userId, Status = 'active', EffectiveAt = SYSUTCDATETIME(), RevokedAt = NULL,
      UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @objectId
    WHERE Id = @roleAssignmentId;
    SET @changed = 1;
  END;

  UPDATE [talentmatch].[RoleAssignments]
  SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @objectId
  WHERE TenantId = @tenantId AND UserObjectId = @objectId
    AND Source = 'bootstrap' AND Status = 'active' AND Id <> @roleAssignmentId;
  IF @@ROWCOUNT > 0 SET @changed = 1;

  IF NOT EXISTS (
    SELECT 1
    FROM [talentmatch].[OrganizationMemberships] organizationMembership
    INNER JOIN [talentmatch].[DepartmentMemberships] departmentMembership
      ON departmentMembership.Id = organizationMembership.DefaultDepartmentMembershipId
      AND departmentMembership.UserId = organizationMembership.UserId
      AND departmentMembership.OrganizationId = organizationMembership.OrganizationId
    WHERE organizationMembership.Id = @organizationMembershipId
      AND organizationMembership.Status = 'active'
      AND departmentMembership.Id = @departmentMembershipId
      AND departmentMembership.Status = 'active'
  )
    THROW 51004, 'The bootstrap organization default is invalid.', 1;

  IF (SELECT COUNT(*) FROM [talentmatch].[RoleAssignments]
      WHERE TenantId = @tenantId AND UserObjectId = @objectId
        AND Source = 'bootstrap' AND Role = 'admin' AND Status = 'active'
        AND OrganizationId IS NULL AND DepartmentId IS NULL AND UserId = @userId) <> 1
    THROW 51005, 'The bootstrap administrator assignment did not converge.', 1;

  IF @changed = 1
  BEGIN
    UPDATE [talentmatch].[Users]
    SET AuthorizationVersion = AuthorizationVersion + 1
    WHERE Id = @userId;
  END;

  DECLARE @detailsJson nvarchar(max) = (
    SELECT @tenantId AS tenantId, @objectId AS objectId, @organizationId AS organizationId,
      @departmentId AS departmentId, @departmentMembershipId AS defaultDepartmentMembershipId,
      @roleAssignmentId AS roleAssignmentId
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
  );

  INSERT INTO [talentmatch].[ProcessingEvents] (
    Id, Actor, Action, EntityType, EntityId, DetailsJson, Timestamp, CorrelationId
  ) VALUES (
    @newAuditEventId, @objectId,
    CASE WHEN @changed = 1 THEN 'auth.seed.applied' ELSE 'auth.seed.checked' END,
    'AuthorizationSeed', @userId, @detailsJson, SYSUTCDATETIME(), @correlationId
  );

  COMMIT TRANSACTION;

  SELECT @changed AS changed, @userId AS userId, @organizationId AS organizationId,
    @departmentId AS departmentId, @departmentMembershipId AS defaultDepartmentMembershipId,
    @roleAssignmentId AS roleAssignmentId,
    (SELECT AuthorizationVersion FROM [talentmatch].[Users] WHERE Id = @userId) AS authorizationVersion;
END TRY
BEGIN CATCH
  IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
`

async function execute() {
  const mode = process.argv[2]
  const configuration = validateBootstrapConfiguration({
    tenantId: process.env.AZURE_TENANT_ID,
    objectId: process.env.ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID,
    username: process.env.ENTRA_BOOTSTRAP_ADMIN_USERNAME,
    fullName: process.env.ENTRA_BOOTSTRAP_ADMIN_FULL_NAME,
    email: process.env.ENTRA_BOOTSTRAP_ADMIN_EMAIL,
    organizationName: process.env.ENTRA_BOOTSTRAP_ORGANIZATION_NAME,
    departmentName: process.env.ENTRA_BOOTSTRAP_DEPARTMENT_NAME,
  })
  const server = process.env.SQL_SERVER_FQDN?.trim()
  const database = process.env.SQL_DATABASE_NAME?.trim()
  const tokenFile = process.env.SQL_TOKEN_FILE?.trim()

  if (!server || !database || !tokenFile) {
    throw new Error('SQL_SERVER_FQDN, SQL_DATABASE_NAME, and SQL_TOKEN_FILE are required.')
  }

  const accessToken = readFileSync(tokenFile, 'utf8').trim()
  unlinkSync(tokenFile)
  if (!accessToken) throw new Error('The Azure SQL access token file was empty.')

  const mssqlModule = await import('mssql')
  const sql = mssqlModule.default ?? mssqlModule
  const pool = await sql.connect({
    server,
    database,
    connectionTimeout: 90_000,
    requestTimeout: 180_000,
    options: { encrypt: true, trustServerCertificate: false },
    authentication: {
      type: 'azure-active-directory-access-token',
      options: { token: accessToken },
    },
  })

  try {
    const request = pool.request()
      .input('tenantId', sql.NVarChar, configuration.tenantId)
      .input('objectId', sql.NVarChar, configuration.objectId)
      .input('username', sql.NVarChar, configuration.username)
      .input('fullName', sql.NVarChar, configuration.fullName)
      .input('email', sql.NVarChar, configuration.email)
      .input('organizationName', sql.NVarChar, configuration.organizationName)
      .input('departmentName', sql.NVarChar, configuration.departmentName)

    if (mode === 'apply') {
      request
        .input('newUserId', sql.NVarChar, randomUUID())
        .input('newOrganizationId', sql.NVarChar, randomUUID())
        .input('newDepartmentId', sql.NVarChar, randomUUID())
        .input('newOrganizationMembershipId', sql.NVarChar, randomUUID())
        .input('newDepartmentMembershipId', sql.NVarChar, randomUUID())
        .input('newRoleAssignmentId', sql.NVarChar, randomUUID())
        .input('newAuditEventId', sql.NVarChar, randomUUID())
        .input('correlationId', sql.NVarChar, randomUUID())
    }

    const result = await request.query(buildBootstrapSql(mode))
    console.log(JSON.stringify({ mode, ...result.recordset[0] }))
  } finally {
    await pool.close()
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  execute().catch(error => {
    console.error(`[entra-admin-seed] ${error.message}`)
    process.exitCode = 1
  })
}