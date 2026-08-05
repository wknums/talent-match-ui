-- Reconciliation that MUST run before the schema.sql batch loop.
--
-- Every table in schema.sql is wrapped in a create-only "IF NOT EXISTS ... CREATE TABLE" guard,
-- so anything added to a table after its first release -- columns and inline constraints alike --
-- never reaches a database that already has the table. Later batches in schema.sql depend on
-- those objects (indexes on Users and Jobs, and the OrganizationMemberships default-department
-- foreign key, which needs a candidate key on DepartmentMemberships). Those batches are not
-- skipped, so they fail and abort initialization before the post-loop reconciliation in the
-- Stack A and Stack B bootstraps can repair anything.
--
-- Only objects a later schema.sql batch depends on belong here, plus the Users authorization
-- columns: the Stack B bootstrap owns the Azure SQL schema and its post-loop reconciliation never
-- covered them, so a database that predates them would never receive them. The OBJECT_ID guards
-- keep this inert on a fresh database, where the tables do not exist yet and the loop creates them
-- complete.
--
-- Batches are separated by a line containing only GO.

IF OBJECT_ID('talentmatch.Users', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('talentmatch.Users', 'AuthenticationProvider') IS NULL
        ALTER TABLE [talentmatch].Users
            ADD AuthenticationProvider NVARCHAR(20) NOT NULL CONSTRAINT DF_Users_AuthenticationProvider DEFAULT 'simple';
    IF COL_LENGTH('talentmatch.Users', 'EntraTenantId') IS NULL
        ALTER TABLE [talentmatch].Users ADD EntraTenantId NVARCHAR(36) NULL;
    IF COL_LENGTH('talentmatch.Users', 'EntraObjectId') IS NULL
        ALTER TABLE [talentmatch].Users ADD EntraObjectId NVARCHAR(36) NULL;
    IF COL_LENGTH('talentmatch.Users', 'IsActive') IS NULL
        ALTER TABLE [talentmatch].Users
            ADD IsActive BIT NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT 1;
    IF COL_LENGTH('talentmatch.Users', 'AuthorizationVersion') IS NULL
        ALTER TABLE [talentmatch].Users
            ADD AuthorizationVersion INT NOT NULL CONSTRAINT DF_Users_AuthorizationVersion DEFAULT 0;
    -- Entra-authenticated users carry no password hash.
    ALTER TABLE [talentmatch].Users ALTER COLUMN PasswordHash NVARCHAR(128) NULL;
END;
GO

IF OBJECT_ID('talentmatch.Jobs', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('talentmatch.Jobs', 'OrganizationId') IS NULL
        ALTER TABLE [talentmatch].Jobs ADD OrganizationId NVARCHAR(36) NULL;
    IF COL_LENGTH('talentmatch.Jobs', 'DepartmentId') IS NULL
        ALTER TABLE [talentmatch].Jobs ADD DepartmentId NVARCHAR(36) NULL;
END;
GO

IF OBJECT_ID('talentmatch.OrganizationMemberships', 'U') IS NOT NULL
   AND COL_LENGTH('talentmatch.OrganizationMemberships', 'DefaultDepartmentMembershipId') IS NULL
    ALTER TABLE [talentmatch].OrganizationMemberships
        ADD DefaultDepartmentMembershipId NVARCHAR(36) NULL;
GO

-- The composite default-department foreign key resolves against this candidate key.
IF OBJECT_ID('talentmatch.DepartmentMemberships', 'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.key_constraints
       WHERE name = 'UQ_DepartmentMemberships_Id_User_Organization'
         AND parent_object_id = OBJECT_ID('talentmatch.DepartmentMemberships')
   )
    ALTER TABLE [talentmatch].DepartmentMemberships
        ADD CONSTRAINT UQ_DepartmentMemberships_Id_User_Organization UNIQUE (Id, UserId, OrganizationId);
GO

-- Jobs created before organizations existed carry NULL scope. JobAuthorization rejects unscoped
-- jobs outright, so these rows are invisible to every caller including global admins. Adopt them
-- into an organization/department matching the free-text Organisation/Department already on the row.
IF OBJECT_ID('talentmatch.Jobs', 'U') IS NOT NULL
   AND OBJECT_ID('talentmatch.Organizations', 'U') IS NOT NULL
   AND OBJECT_ID('talentmatch.Departments', 'U') IS NOT NULL
   AND COL_LENGTH('talentmatch.Jobs', 'OrganizationId') IS NOT NULL
   AND COL_LENGTH('talentmatch.Jobs', 'DepartmentId') IS NOT NULL
BEGIN
    DECLARE @backfillActor NVARCHAR(100) = 'system:schema-backfill';

    INSERT INTO [talentmatch].Organizations (Id, Name, Status, UpdatedBy)
    SELECT LOWER(CONVERT(NVARCHAR(36), NEWID())), source.Name, 'active', @backfillActor
    FROM (
        SELECT DISTINCT LTRIM(RTRIM(j.Organisation)) AS Name
        FROM [talentmatch].Jobs j
        WHERE (j.OrganizationId IS NULL OR j.DepartmentId IS NULL)
          AND NULLIF(LTRIM(RTRIM(j.Organisation)), '') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(j.Department)), '') IS NOT NULL
    ) source
    WHERE NOT EXISTS (
        SELECT 1 FROM [talentmatch].Organizations o
        WHERE o.Status = 'active' AND o.Name = source.Name
    );

    INSERT INTO [talentmatch].Departments (Id, OrganizationId, Name, Status, UpdatedBy)
    SELECT LOWER(CONVERT(NVARCHAR(36), NEWID())), source.OrganizationId, source.Name, 'active', @backfillActor
    FROM (
        SELECT DISTINCT o.Id AS OrganizationId, LTRIM(RTRIM(j.Department)) AS Name
        FROM [talentmatch].Jobs j
        INNER JOIN [talentmatch].Organizations o
            ON o.Status = 'active' AND o.Name = LTRIM(RTRIM(j.Organisation))
        WHERE (j.OrganizationId IS NULL OR j.DepartmentId IS NULL)
          AND NULLIF(LTRIM(RTRIM(j.Department)), '') IS NOT NULL
    ) source
    WHERE NOT EXISTS (
        SELECT 1 FROM [talentmatch].Departments d
        WHERE d.Status = 'active'
          AND d.OrganizationId = source.OrganizationId
          AND d.Name = source.Name
    );

    UPDATE j
    SET j.OrganizationId = o.Id,
        j.DepartmentId = d.Id
    FROM [talentmatch].Jobs j
    INNER JOIN [talentmatch].Organizations o
        ON o.Status = 'active' AND o.Name = LTRIM(RTRIM(j.Organisation))
    INNER JOIN [talentmatch].Departments d
        ON d.Status = 'active' AND d.OrganizationId = o.Id AND d.Name = LTRIM(RTRIM(j.Department))
    WHERE j.OrganizationId IS NULL OR j.DepartmentId IS NULL;
END;
GO
