#!/usr/bin/env node

import { randomUUID } from 'node:crypto'
import { readFileSync, unlinkSync } from 'node:fs'
import { pathToFileURL } from 'node:url'

const roleNames = new Set(['admin', 'organization_admin', 'recruiter', 'business_panel'])

const requiredFields = {
  'map-group': ['tenantId', 'groupObjectId', 'role', 'actorObjectId', 'newMappingId', 'newAuditEventId', 'correlationId'],
  'inspect-mapping': ['tenantId', 'mappingId', 'defaultDepartmentId'],
  'check-assignment': ['tenantId', 'userObjectId', 'mappingId', 'defaultDepartmentId'],
  assign: [
    'tenantId', 'userObjectId', 'mappingId', 'defaultDepartmentId', 'username',
    'fullName', 'email', 'actorObjectId', 'newUserId', 'newOrganizationMembershipId',
    'newMappedDepartmentMembershipId', 'newDefaultDepartmentMembershipId',
    'newRoleAssignmentId', 'newAuditEventId', 'correlationId',
  ],
  check: ['tenantId', 'userObjectId'],
  revoke: ['tenantId', 'userObjectId', 'assignmentId', 'actorObjectId', 'newAuditEventId', 'correlationId'],
  'check-revoke': ['tenantId', 'userObjectId', 'assignmentId'],
}

export function validateRoleScope({ role, organizationId, departmentId }) {
  const normalizedRole = role?.trim().toLowerCase()
  const normalizedOrganizationId = organizationId?.trim() || null
  const normalizedDepartmentId = departmentId?.trim() || null

  if (!roleNames.has(normalizedRole)) {
    throw new Error(`Unsupported role: ${role ?? ''}`)
  }
  if (normalizedRole === 'admin' && normalizedOrganizationId) {
    throw new Error('Role admin forbids organization and department scope.')
  }
  if (normalizedRole === 'admin' && normalizedDepartmentId) {
    throw new Error('Role admin forbids organization and department scope.')
  }
  if (normalizedRole === 'organization_admin' && !normalizedOrganizationId) {
    throw new Error('Role organization_admin requires organization scope.')
  }
  if (normalizedRole === 'organization_admin' && normalizedDepartmentId) {
    throw new Error('Role organization_admin forbids department scope.')
  }
  if (normalizedRole === 'recruiter' && !normalizedOrganizationId) {
    throw new Error('Role recruiter requires organization scope.')
  }
  if (normalizedRole === 'recruiter' && !normalizedDepartmentId) {
    throw new Error('Role recruiter requires department scope.')
  }
  if (normalizedRole === 'business_panel' && !normalizedOrganizationId) {
    throw new Error('Role business_panel requires organization scope.')
  }

  return {
    role: normalizedRole,
    organizationId: normalizedOrganizationId,
    departmentId: normalizedDepartmentId,
  }
}

export function validateRoleManagementConfiguration(operation, configuration) {
  const fields = requiredFields[operation]
  if (!fields) throw new Error(`Unsupported role-management operation: ${operation}`)

  const missingFields = fields.filter(field => !String(configuration[field] ?? '').trim())
  if (missingFields.length > 0) {
    throw new Error(`Missing role-management configuration: ${missingFields.join(', ')}`)
  }

  if (operation === 'map-group') {
    Object.assign(configuration, validateRoleScope(configuration))
  }
  return configuration
}

const checkSql = `
SELECT
  targetUser.Id AS userId,
  targetUser.EntraTenantId AS tenantId,
  targetUser.EntraObjectId AS userObjectId,
  targetUser.Username AS username,
  targetUser.FullName AS fullName,
  targetUser.Email AS email,
  targetUser.IsActive AS isActive,
  targetUser.AuthorizationVersion AS authorizationVersion,
  COALESCE((
    SELECT organizationMembership.Id AS membershipId,
      organizationMembership.OrganizationId AS organizationId,
      organizationMembership.Status AS status,
      defaultDepartmentMembership.DepartmentId AS defaultDepartmentId
    FROM [talentmatch].[OrganizationMemberships] organizationMembership
    LEFT JOIN [talentmatch].[DepartmentMemberships] defaultDepartmentMembership
      ON defaultDepartmentMembership.Id = organizationMembership.DefaultDepartmentMembershipId
    WHERE organizationMembership.UserId = targetUser.Id
      AND organizationMembership.Status = 'active'
    ORDER BY organizationMembership.OrganizationId
    FOR JSON PATH
  ), '[]') AS organizationsJson,
  COALESCE((
    SELECT departmentMembership.Id AS membershipId,
      departmentMembership.OrganizationId AS organizationId,
      departmentMembership.DepartmentId AS departmentId,
      departmentMembership.Status AS status
    FROM [talentmatch].[DepartmentMemberships] departmentMembership
    WHERE departmentMembership.UserId = targetUser.Id
      AND departmentMembership.Status = 'active'
    ORDER BY departmentMembership.OrganizationId, departmentMembership.DepartmentId
    FOR JSON PATH
  ), '[]') AS departmentsJson,
  COALESCE((
    SELECT roleAssignment.Id AS assignmentId, roleAssignment.Role AS role,
      roleAssignment.OrganizationId AS organizationId,
      roleAssignment.DepartmentId AS departmentId,
      roleAssignment.RoleGroupMappingId AS mappingId,
      roleGroupMapping.GroupObjectId AS groupObjectId,
      roleAssignment.Source AS source, roleAssignment.Status AS status
    FROM [talentmatch].[RoleAssignments] roleAssignment
    LEFT JOIN [talentmatch].[RoleGroupMappings] roleGroupMapping
      ON roleGroupMapping.Id = roleAssignment.RoleGroupMappingId
    WHERE roleAssignment.TenantId = @tenantId
      AND roleAssignment.UserObjectId = @userObjectId
      AND roleAssignment.Status = 'active'
    ORDER BY roleAssignment.OrganizationId, roleAssignment.DepartmentId, roleAssignment.Role
    FOR JSON PATH
  ), '[]') AS assignmentsJson
FROM [talentmatch].[Users] targetUser
WHERE targetUser.AuthenticationProvider = 'entra'
  AND targetUser.EntraTenantId = @tenantId
  AND targetUser.EntraObjectId = @userObjectId;
`

const inspectMappingSql = `
SELECT mapping.Id AS mappingId, mapping.TenantId AS tenantId,
  mapping.GroupObjectId AS groupObjectId, mapping.Role AS role,
  mapping.OrganizationId AS organizationId, mapping.DepartmentId AS departmentId,
  mapping.Enabled AS enabled,
  CAST(CASE WHEN defaultDepartment.Id IS NOT NULL
    AND (mapping.OrganizationId IS NULL OR defaultDepartment.OrganizationId = mapping.OrganizationId)
    THEN 1 ELSE 0 END AS bit) AS defaultDepartmentReady,
  defaultDepartment.OrganizationId AS defaultOrganizationId
FROM [talentmatch].[RoleGroupMappings] mapping
LEFT JOIN [talentmatch].[Departments] defaultDepartment
  ON defaultDepartment.Id = @defaultDepartmentId AND defaultDepartment.Status = 'active'
WHERE mapping.Id = @mappingId AND mapping.TenantId = @tenantId;
`

const checkRevokeSql = `
SELECT roleAssignment.Id AS assignmentId,
  CAST(CASE WHEN roleAssignment.Status = 'revoked' THEN 1 ELSE 0 END AS bit) AS sqlRevoked,
  roleGroupMapping.GroupObjectId AS groupObjectId,
  (
    SELECT COUNT(*)
    FROM [talentmatch].[RoleAssignments] activeAssignment
    INNER JOIN [talentmatch].[RoleGroupMappings] activeMapping
      ON activeMapping.Id = activeAssignment.RoleGroupMappingId
    WHERE activeAssignment.TenantId = @tenantId
      AND activeAssignment.UserObjectId = @userObjectId
      AND activeAssignment.Status = 'active'
      AND activeMapping.GroupObjectId = roleGroupMapping.GroupObjectId
  ) AS activeGroupDependencyCount
FROM [talentmatch].[RoleAssignments] roleAssignment
LEFT JOIN [talentmatch].[RoleGroupMappings] roleGroupMapping
  ON roleGroupMapping.Id = roleAssignment.RoleGroupMappingId
WHERE roleAssignment.Id = @assignmentId
  AND roleAssignment.TenantId = @tenantId
  AND roleAssignment.UserObjectId = @userObjectId;
`

const checkAssignmentSql = `
SELECT roleAssignment.Id AS assignmentId,
  CAST(CASE WHEN roleAssignment.Id IS NOT NULL
    AND roleAssignment.Status = 'active'
    AND targetUser.IsActive = 1
    AND mapping.Enabled = 1
    THEN 1 ELSE 0 END AS bit) AS assignmentReady,
  CAST(CASE WHEN organizationMembership.Id IS NOT NULL
    AND organizationMembership.Status = 'active'
    THEN 1 ELSE 0 END AS bit) AS organizationMembershipReady,
  CAST(CASE WHEN defaultDepartment.Id IS NOT NULL
    AND defaultDepartment.Status = 'active'
    AND defaultDepartmentMembership.Id IS NOT NULL
    AND defaultDepartmentMembership.Status = 'active'
    AND organizationMembership.DefaultDepartmentMembershipId = defaultDepartmentMembership.Id
    THEN 1 ELSE 0 END AS bit) AS defaultDepartmentReady,
  CAST(CASE WHEN roleAssignment.Id IS NOT NULL
    AND roleAssignment.Status = 'active'
    AND targetUser.IsActive = 1
    AND mapping.Enabled = 1
    AND organizationMembership.Id IS NOT NULL
    AND organizationMembership.Status = 'active'
    AND defaultDepartment.Id IS NOT NULL
    AND defaultDepartment.Status = 'active'
    AND defaultDepartmentMembership.Id IS NOT NULL
    AND defaultDepartmentMembership.Status = 'active'
    AND organizationMembership.DefaultDepartmentMembershipId = defaultDepartmentMembership.Id
    THEN 1 ELSE 0 END AS bit) AS converged
FROM [talentmatch].[RoleGroupMappings] mapping
LEFT JOIN [talentmatch].[RoleAssignments] roleAssignment
  ON roleAssignment.RoleGroupMappingId = mapping.Id
  AND roleAssignment.TenantId = @tenantId
  AND roleAssignment.UserObjectId = @userObjectId
  AND roleAssignment.Status = 'active'
LEFT JOIN [talentmatch].[Users] targetUser
  ON targetUser.Id = roleAssignment.UserId
  AND targetUser.EntraTenantId = @tenantId
  AND targetUser.EntraObjectId = @userObjectId
LEFT JOIN [talentmatch].[Departments] defaultDepartment
  ON defaultDepartment.Id = @defaultDepartmentId
LEFT JOIN [talentmatch].[DepartmentMemberships] defaultDepartmentMembership
  ON defaultDepartmentMembership.UserId = targetUser.Id
  AND defaultDepartmentMembership.DepartmentId = defaultDepartment.Id
  AND defaultDepartmentMembership.OrganizationId = defaultDepartment.OrganizationId
  AND defaultDepartmentMembership.Status = 'active'
LEFT JOIN [talentmatch].[OrganizationMemberships] organizationMembership
  ON organizationMembership.UserId = targetUser.Id
  AND organizationMembership.OrganizationId = defaultDepartment.OrganizationId
  AND organizationMembership.Status = 'active'
WHERE mapping.Id = @mappingId AND mapping.TenantId = @tenantId;
`

const mapGroupSql = `
SET XACT_ABORT ON;
BEGIN TRANSACTION;

BEGIN TRY
  DECLARE @mappingId nvarchar(36);
  DECLARE @changed bit = 0;
  DECLARE @lockResult int;
  DECLARE @lockResource nvarchar(255) = CONCAT('talentmatch:role-group:', @tenantId, ':', @groupObjectId);

  EXEC @lockResult = sys.sp_getapplock
    @Resource = @lockResource, @LockMode = 'Exclusive',
    @LockOwner = 'Transaction', @LockTimeout = 60000;
  IF @lockResult < 0 THROW 51101, 'Could not acquire the role-group mapping lock.', 1;

  IF @organizationId IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM [talentmatch].[Organizations] WITH (UPDLOCK, HOLDLOCK)
    WHERE Id = @organizationId AND Status = 'active'
  ) THROW 51102, 'The mapped organization is missing or inactive.', 1;

  IF @departmentId IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM [talentmatch].[Departments] WITH (UPDLOCK, HOLDLOCK)
    WHERE Id = @departmentId AND OrganizationId = @organizationId AND Status = 'active'
  ) THROW 51103, 'The mapped department is missing, inactive, or belongs to another organization.', 1;

  SELECT @mappingId = Id
  FROM [talentmatch].[RoleGroupMappings] WITH (UPDLOCK, HOLDLOCK)
  WHERE TenantId = @tenantId AND GroupObjectId = @groupObjectId;

  IF @mappingId IS NULL
  BEGIN
    SET @mappingId = @newMappingId;
    INSERT INTO [talentmatch].[RoleGroupMappings] (
      Id, TenantId, GroupObjectId, Role, OrganizationId, DepartmentId,
      Enabled, CreatedAt, UpdatedAt, UpdatedBy
    ) VALUES (
      @mappingId, @tenantId, @groupObjectId, @role, @organizationId, @departmentId,
      1, SYSUTCDATETIME(), SYSUTCDATETIME(), @actorObjectId
    );
    SET @changed = 1;
  END
  ELSE
  BEGIN
    IF EXISTS (
      SELECT 1 FROM [talentmatch].[RoleGroupMappings]
      WHERE Id = @mappingId AND (
        Role <> @role
        OR ISNULL(OrganizationId, '') <> ISNULL(@organizationId, '')
        OR ISNULL(DepartmentId, '') <> ISNULL(@departmentId, '')
      )
    ) THROW 51104, 'The group is already mapped to a different immutable role scope.', 1;

    UPDATE [talentmatch].[RoleGroupMappings]
    SET Enabled = 1, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @actorObjectId
    WHERE Id = @mappingId AND Enabled = 0;
    IF @@ROWCOUNT > 0 SET @changed = 1;
  END;

  DECLARE @detailsJson nvarchar(max) = (
    SELECT @tenantId AS tenantId, @groupObjectId AS groupObjectId,
      @role AS role, @organizationId AS organizationId, @departmentId AS departmentId,
      @changed AS changed FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
  );
  INSERT INTO [talentmatch].[ProcessingEvents] (
    Id, Actor, Action, EntityType, EntityId, DetailsJson, Timestamp, CorrelationId
  ) VALUES (
    @newAuditEventId, @actorObjectId, 'auth.role_group.mapped',
    'RoleGroupMapping', @mappingId, @detailsJson, SYSUTCDATETIME(), @correlationId
  );

  COMMIT TRANSACTION;
  SELECT 'map-group' AS operation, @mappingId AS mappingId, @changed AS changed,
    CAST(1 AS bit) AS converged;
END TRY
BEGIN CATCH
  IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
`

const assignSql = `
SET XACT_ABORT ON;
BEGIN TRANSACTION;

BEGIN TRY
  DECLARE @userId nvarchar(36);
  DECLARE @mappingRole nvarchar(30);
  DECLARE @mappingOrganizationId nvarchar(36);
  DECLARE @mappingDepartmentId nvarchar(36);
  DECLARE @membershipOrganizationId nvarchar(36);
  DECLARE @mappedDepartmentMembershipId nvarchar(36);
  DECLARE @defaultDepartmentMembershipId nvarchar(36);
  DECLARE @organizationMembershipId nvarchar(36);
  DECLARE @assignmentId nvarchar(36);
  DECLARE @changed bit = 0;
  DECLARE @lockResult int;
  DECLARE @lockResource nvarchar(255) = CONCAT('talentmatch:role-assign:', @tenantId, ':', @userObjectId, ':', @mappingId);

  EXEC @lockResult = sys.sp_getapplock
    @Resource = @lockResource, @LockMode = 'Exclusive',
    @LockOwner = 'Transaction', @LockTimeout = 60000;
  IF @lockResult < 0 THROW 51201, 'Could not acquire the role-assignment lock.', 1;

  SELECT @mappingRole = Role, @mappingOrganizationId = OrganizationId,
    @mappingDepartmentId = DepartmentId
  FROM [talentmatch].[RoleGroupMappings] WITH (UPDLOCK, HOLDLOCK)
  WHERE Id = @mappingId AND TenantId = @tenantId AND Enabled = 1;
  IF @mappingRole IS NULL THROW 51202, 'The role-group mapping is missing or disabled.', 1;

  SELECT @membershipOrganizationId = OrganizationId
  FROM [talentmatch].[Departments] WITH (UPDLOCK, HOLDLOCK)
  WHERE Id = @defaultDepartmentId AND Status = 'active'
    AND (@mappingOrganizationId IS NULL OR OrganizationId = @mappingOrganizationId);
  IF @membershipOrganizationId IS NULL
    THROW 51203, 'The explicit default department is invalid for the mapped organization.', 1;

  SELECT @userId = Id
  FROM [talentmatch].[Users] WITH (UPDLOCK, HOLDLOCK)
  WHERE AuthenticationProvider = 'entra'
    AND EntraTenantId = @tenantId AND EntraObjectId = @userObjectId;

  IF @userId IS NULL
  BEGIN
    IF EXISTS (SELECT 1 FROM [talentmatch].[Users] WHERE Username = @username)
      THROW 51204, 'The Entra username belongs to another application identity.', 1;
    SET @userId = @newUserId;
    INSERT INTO [talentmatch].[Users] (
      Id, Username, Role, FullName, Email, Department, PasswordHash, CreatedAt,
      PasswordResetRequired, AuthenticationProvider, EntraTenantId, EntraObjectId, IsActive
    ) VALUES (
      @userId, @username, @mappingRole, @fullName, @email, '', NULL, SYSUTCDATETIME(),
      0, 'entra', @tenantId, @userObjectId, 1
    );
    SET @changed = 1;
  END
  ELSE
  BEGIN
    UPDATE [talentmatch].[Users]
    SET Username = @username, FullName = @fullName, Email = @email,
      PasswordHash = NULL, PasswordResetRequired = 0, IsActive = 1
    WHERE Id = @userId AND (
      Username <> @username OR FullName <> @fullName OR Email <> @email
      OR IsActive <> 1 OR AuthenticationProvider <> 'entra'
    );
    IF @@ROWCOUNT > 0 SET @changed = 1;
  END;

  IF @mappingDepartmentId IS NOT NULL
  BEGIN
    SELECT TOP (1) @mappedDepartmentMembershipId = Id
    FROM [talentmatch].[DepartmentMemberships] WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @userId AND DepartmentId = @mappingDepartmentId
    ORDER BY CASE WHEN Status = 'active' THEN 0 ELSE 1 END, EffectiveAt DESC;

    IF @mappedDepartmentMembershipId IS NULL
    BEGIN
      SET @mappedDepartmentMembershipId = @newMappedDepartmentMembershipId;
      INSERT INTO [talentmatch].[DepartmentMemberships] (
        Id, UserId, OrganizationId, DepartmentId, Status, EffectiveAt, RevokedAt, UpdatedBy
      ) VALUES (
        @mappedDepartmentMembershipId, @userId, @membershipOrganizationId,
        @mappingDepartmentId, 'active', SYSUTCDATETIME(), NULL, @actorObjectId
      );
      SET @changed = 1;
    END
    ELSE
    BEGIN
      UPDATE [talentmatch].[DepartmentMemberships]
      SET Status = 'active', EffectiveAt = SYSUTCDATETIME(), RevokedAt = NULL,
        UpdatedBy = @actorObjectId
      WHERE Id = @mappedDepartmentMembershipId AND Status <> 'active';
      IF @@ROWCOUNT > 0 SET @changed = 1;
    END;
  END;

  SELECT TOP (1) @defaultDepartmentMembershipId = Id
  FROM [talentmatch].[DepartmentMemberships] WITH (UPDLOCK, HOLDLOCK)
  WHERE UserId = @userId AND DepartmentId = @defaultDepartmentId
  ORDER BY CASE WHEN Status = 'active' THEN 0 ELSE 1 END, EffectiveAt DESC;

  IF @defaultDepartmentMembershipId IS NULL
  BEGIN
    SET @defaultDepartmentMembershipId = CASE
      WHEN @mappingDepartmentId = @defaultDepartmentId THEN @newMappedDepartmentMembershipId
      ELSE @newDefaultDepartmentMembershipId END;
    INSERT INTO [talentmatch].[DepartmentMemberships] (
      Id, UserId, OrganizationId, DepartmentId, Status, EffectiveAt, RevokedAt, UpdatedBy
    ) VALUES (
      @defaultDepartmentMembershipId, @userId, @membershipOrganizationId,
      @defaultDepartmentId, 'active', SYSUTCDATETIME(), NULL, @actorObjectId
    );
    SET @changed = 1;
  END
  ELSE
  BEGIN
    UPDATE [talentmatch].[DepartmentMemberships]
    SET Status = 'active', EffectiveAt = SYSUTCDATETIME(), RevokedAt = NULL,
      UpdatedBy = @actorObjectId
    WHERE Id = @defaultDepartmentMembershipId AND Status <> 'active';
    IF @@ROWCOUNT > 0 SET @changed = 1;
  END;

  SELECT TOP (1) @organizationMembershipId = Id
  FROM [talentmatch].[OrganizationMemberships] WITH (UPDLOCK, HOLDLOCK)
  WHERE UserId = @userId AND OrganizationId = @membershipOrganizationId
  ORDER BY CASE WHEN Status = 'active' THEN 0 ELSE 1 END, EffectiveAt DESC;

  IF @organizationMembershipId IS NULL
  BEGIN
    SET @organizationMembershipId = @newOrganizationMembershipId;
    INSERT INTO [talentmatch].[OrganizationMemberships] (
      Id, UserId, OrganizationId, DefaultDepartmentMembershipId,
      Status, EffectiveAt, RevokedAt, UpdatedBy
    ) VALUES (
      @organizationMembershipId, @userId, @membershipOrganizationId,
      @defaultDepartmentMembershipId, 'active', SYSUTCDATETIME(), NULL, @actorObjectId
    );
    SET @changed = 1;
  END
  ELSE
  BEGIN
    UPDATE [talentmatch].[OrganizationMemberships]
    SET DefaultDepartmentMembershipId = @defaultDepartmentMembershipId,
      Status = 'active', EffectiveAt = SYSUTCDATETIME(), RevokedAt = NULL,
      UpdatedBy = @actorObjectId
    WHERE Id = @organizationMembershipId AND (
      Status <> 'active' OR DefaultDepartmentMembershipId <> @defaultDepartmentMembershipId
    );
    IF @@ROWCOUNT > 0 SET @changed = 1;
  END;

  SELECT TOP (1) @assignmentId = Id
  FROM [talentmatch].[RoleAssignments] WITH (UPDLOCK, HOLDLOCK)
  WHERE TenantId = @tenantId AND UserObjectId = @userObjectId
    AND RoleGroupMappingId = @mappingId
  ORDER BY CASE WHEN Status = 'active' THEN 0 ELSE 1 END, EffectiveAt DESC;

  IF @assignmentId IS NULL
  BEGIN
    SET @assignmentId = @newRoleAssignmentId;
    INSERT INTO [talentmatch].[RoleAssignments] (
      Id, UserId, TenantId, UserObjectId, Role, OrganizationId, DepartmentId,
      RoleGroupMappingId, Source, Status, EffectiveAt, RevokedAt,
      CreatedAt, UpdatedAt, UpdatedBy
    ) VALUES (
      @assignmentId, @userId, @tenantId, @userObjectId, @mappingRole,
      @mappingOrganizationId, @mappingDepartmentId, @mappingId, 'group', 'active',
      SYSUTCDATETIME(), NULL, SYSUTCDATETIME(), SYSUTCDATETIME(), @actorObjectId
    );
    SET @changed = 1;
  END
  ELSE
  BEGIN
    UPDATE [talentmatch].[RoleAssignments]
    SET UserId = @userId, Role = @mappingRole,
      OrganizationId = @mappingOrganizationId, DepartmentId = @mappingDepartmentId,
      Source = 'group', Status = 'active', EffectiveAt = SYSUTCDATETIME(),
      RevokedAt = NULL, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @actorObjectId
    WHERE Id = @assignmentId AND Status <> 'active';
    IF @@ROWCOUNT > 0 SET @changed = 1;
  END;

  IF @changed = 1
    UPDATE [talentmatch].[Users]
    SET AuthorizationVersion = AuthorizationVersion + 1 WHERE Id = @userId;

  IF NOT EXISTS (
    SELECT 1 FROM [talentmatch].[OrganizationMemberships] organizationMembership
    INNER JOIN [talentmatch].[DepartmentMemberships] departmentMembership
      ON departmentMembership.Id = organizationMembership.DefaultDepartmentMembershipId
      AND departmentMembership.UserId = organizationMembership.UserId
      AND departmentMembership.OrganizationId = organizationMembership.OrganizationId
    WHERE organizationMembership.Id = @organizationMembershipId
      AND organizationMembership.Status = 'active'
      AND departmentMembership.Id = @defaultDepartmentMembershipId
      AND departmentMembership.Status = 'active'
  ) THROW 51205, 'The explicit default did not converge.', 1;

  DECLARE @detailsJson nvarchar(max) = (
    SELECT @tenantId AS tenantId, @userObjectId AS userObjectId,
      @mappingId AS mappingId, @assignmentId AS assignmentId,
      @membershipOrganizationId AS organizationId,
      @defaultDepartmentId AS defaultDepartmentId, @changed AS changed
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
  );
  INSERT INTO [talentmatch].[ProcessingEvents] (
    Id, Actor, Action, EntityType, EntityId, DetailsJson, Timestamp, CorrelationId
  ) VALUES (
    @newAuditEventId, @actorObjectId,
    CASE WHEN @changed = 1 THEN 'auth.assignment.activated' ELSE 'auth.assignment.checked' END,
    'RoleAssignment', @assignmentId, @detailsJson, SYSUTCDATETIME(), @correlationId
  );

  COMMIT TRANSACTION;
  SELECT 'assign' AS operation, @assignmentId AS assignmentId,
    @userId AS userId, @defaultDepartmentMembershipId AS defaultDepartmentMembershipId,
    @changed AS changed, CAST(1 AS bit) AS converged;
END TRY
BEGIN CATCH
  IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
`

const revokeSql = `
SET XACT_ABORT ON;
BEGIN TRANSACTION;

BEGIN TRY
  DECLARE @userId nvarchar(36);
  DECLARE @mappingId nvarchar(36);
  DECLARE @groupObjectId nvarchar(36);
  DECLARE @changed bit = 0;
  DECLARE @lockResult int;
  DECLARE @lockResource nvarchar(255) = CONCAT('talentmatch:role-revoke:', @tenantId, ':', @userObjectId, ':', @assignmentId);

  EXEC @lockResult = sys.sp_getapplock
    @Resource = @lockResource, @LockMode = 'Exclusive',
    @LockOwner = 'Transaction', @LockTimeout = 60000;
  IF @lockResult < 0 THROW 51301, 'Could not acquire the role-revocation lock.', 1;

  SELECT @userId = assignment.UserId, @mappingId = assignment.RoleGroupMappingId,
    @groupObjectId = mapping.GroupObjectId
  FROM [talentmatch].[RoleAssignments] assignment WITH (UPDLOCK, HOLDLOCK)
  INNER JOIN [talentmatch].[RoleGroupMappings] mapping
    ON mapping.Id = assignment.RoleGroupMappingId
  WHERE assignment.Id = @assignmentId
    AND assignment.TenantId = @tenantId
    AND assignment.UserObjectId = @userObjectId
    AND assignment.Source = 'group';
  IF @userId IS NULL THROW 51302, 'The targeted group assignment was not found.', 1;

  UPDATE [talentmatch].[RoleAssignments]
  SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(),
    UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @actorObjectId
  WHERE Id = @assignmentId AND UserObjectId = @userObjectId AND Status = 'active';
  IF @@ROWCOUNT > 0 SET @changed = 1;

  IF @changed = 1
    UPDATE [talentmatch].[Users]
    SET AuthorizationVersion = AuthorizationVersion + 1 WHERE Id = @userId;

  DECLARE @activeGroupDependencyCount int = (
    SELECT COUNT(*)
    FROM [talentmatch].[RoleAssignments] activeAssignment
    INNER JOIN [talentmatch].[RoleGroupMappings] activeMapping
      ON activeMapping.Id = activeAssignment.RoleGroupMappingId
    WHERE activeAssignment.TenantId = @tenantId
      AND activeAssignment.UserObjectId = @userObjectId
      AND activeAssignment.Status = 'active'
      AND activeMapping.GroupObjectId = @groupObjectId
  );

  DECLARE @detailsJson nvarchar(max) = (
    SELECT @tenantId AS tenantId, @userObjectId AS userObjectId,
      @assignmentId AS assignmentId, @mappingId AS mappingId,
      @groupObjectId AS groupObjectId,
      @activeGroupDependencyCount AS activeGroupDependencyCount,
      @changed AS changed FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
  );
  INSERT INTO [talentmatch].[ProcessingEvents] (
    Id, Actor, Action, EntityType, EntityId, DetailsJson, Timestamp, CorrelationId
  ) VALUES (
    @newAuditEventId, @actorObjectId, 'auth.assignment.revoked',
    'RoleAssignment', @assignmentId, @detailsJson, SYSUTCDATETIME(), @correlationId
  );

  COMMIT TRANSACTION;
  SELECT 'revoke' AS operation, @assignmentId AS assignmentId,
    @groupObjectId AS groupObjectId,
    @activeGroupDependencyCount AS activeGroupDependencyCount,
    @changed AS changed, CAST(1 AS bit) AS sqlRevoked;
END TRY
BEGIN CATCH
  IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
`

const sqlByOperation = {
  'map-group': mapGroupSql,
  'inspect-mapping': inspectMappingSql,
  'check-assignment': checkAssignmentSql,
  assign: assignSql,
  check: checkSql,
  revoke: revokeSql,
  'check-revoke': checkRevokeSql,
}

export function buildRoleManagementSql(operation) {
  const sql = sqlByOperation[operation]
  if (!sql) throw new Error(`Unsupported role-management operation: ${operation}`)
  return sql
}

function parseOptions(argumentsList) {
  const options = {}
  for (let index = 0; index < argumentsList.length; index += 2) {
    const key = argumentsList[index]
    const value = argumentsList[index + 1]
    if (!key?.startsWith('--') || value === undefined || value.startsWith('--')) {
      throw new Error(`Invalid role-management option near ${key ?? '<end>'}.`)
    }
    const name = key.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())
    options[name] = value
  }
  return options
}

function configurationFor(operation, options) {
  const base = {
    tenantId: process.env.AZURE_TENANT_ID,
    actorObjectId: process.env.ENTRA_ROLE_OPERATOR_OBJECT_ID,
    ...options,
  }

  if (operation === 'map-group') {
    Object.assign(base, {
      newMappingId: randomUUID(),
      newAuditEventId: randomUUID(),
      correlationId: randomUUID(),
    })
  } else if (operation === 'assign') {
    Object.assign(base, {
      username: process.env.ENTRA_TARGET_USERNAME,
      fullName: process.env.ENTRA_TARGET_FULL_NAME,
      email: process.env.ENTRA_TARGET_EMAIL,
      newUserId: randomUUID(),
      newOrganizationMembershipId: randomUUID(),
      newMappedDepartmentMembershipId: randomUUID(),
      newDefaultDepartmentMembershipId: randomUUID(),
      newRoleAssignmentId: randomUUID(),
      newAuditEventId: randomUUID(),
      correlationId: randomUUID(),
    })
  } else if (operation === 'revoke') {
    Object.assign(base, {
      newAuditEventId: randomUUID(),
      correlationId: randomUUID(),
    })
  }
  return validateRoleManagementConfiguration(operation, base)
}

function bindRequest(request, sqlModule, statement, configuration) {
  const nullableFields = new Set(['organizationId', 'departmentId'])
  for (const [name, value] of Object.entries(configuration)) {
    if (!statement.includes(`@${name}`)) continue
    request.input(name, sqlModule.NVarChar, nullableFields.has(name) && !value ? null : value)
  }
  return request
}

function normalizeResult(operation, row) {
  if (!row) return { operation, found: false }
  const result = { operation, ...row }
  for (const [name, value] of Object.entries(result)) {
    if (name.endsWith('Json')) {
      const targetName = name.slice(0, -4)
      result[targetName] = value ? JSON.parse(value) : []
      delete result[name]
    }
  }
  return result
}

async function execute() {
  const operation = process.argv[2]
  const options = parseOptions(process.argv.slice(3))
  const configuration = configurationFor(operation, options)
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
    const statement = buildRoleManagementSql(operation)
    const request = pool.request()
    bindRequest(request, sql, statement, configuration)
    const queryResult = await request.query(statement)
    console.log(JSON.stringify(normalizeResult(operation, queryResult.recordset[0])))
  } finally {
    await pool.close()
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  execute().catch(error => {
    console.error(`[entra-role-data] ${error.message}`)
    process.exitCode = 1
  })
}