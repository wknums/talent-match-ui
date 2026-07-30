-- ============================================================
-- TalentMatch Unified Database Schema
-- Single database shared by Stack A (Node/Express) and Stack B (.NET Blazor)
-- All tables live under the [talentmatch] schema in Azure SQL.
-- ============================================================

-- Schema preamble — create the talentmatch schema if it does not exist
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch')
    EXEC('CREATE SCHEMA [talentmatch]')

-- 1. USERS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].Users (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    Username        NVARCHAR(100)   NOT NULL,
    Role            NVARCHAR(20)    NOT NULL,
    FullName        NVARCHAR(200)   NOT NULL DEFAULT '',
    Email           NVARCHAR(320)   NOT NULL DEFAULT '',
    Department      NVARCHAR(100)   NOT NULL DEFAULT '',
    PasswordHash    NVARCHAR(128)   NULL,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    LastLogin       DATETIME2       NULL,
    PasswordResetRequired BIT       NOT NULL DEFAULT 0,
    AuthenticationProvider NVARCHAR(20) NOT NULL DEFAULT 'simple',
    EntraTenantId   NVARCHAR(36)    NULL,
    EntraObjectId   NVARCHAR(36)    NULL,
    IsActive        BIT             NOT NULL DEFAULT 1,
    CONSTRAINT CK_Users_IdentityProvider CHECK (
        (AuthenticationProvider = 'simple' AND PasswordHash IS NOT NULL AND EntraTenantId IS NULL AND EntraObjectId IS NULL)
        OR (AuthenticationProvider = 'entra' AND PasswordHash IS NULL AND EntraTenantId IS NOT NULL AND EntraObjectId IS NOT NULL AND PasswordResetRequired = 0)
    )
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_Users_Username')
    CREATE UNIQUE INDEX UX_Users_Username ON [talentmatch].Users (Username);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_Users_EntraIdentity')
    CREATE UNIQUE INDEX UX_Users_EntraIdentity ON [talentmatch].Users (EntraTenantId, EntraObjectId) WHERE AuthenticationProvider = 'entra';

-- 2. PASSWORD RESET REQUESTS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PasswordResetRequests' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].PasswordResetRequests (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    UserId          NVARCHAR(36)    NOT NULL,
    Username        NVARCHAR(100)   NOT NULL,
    FullName        NVARCHAR(200)   NOT NULL DEFAULT '',
    Reason          NVARCHAR(1000)  NOT NULL DEFAULT '',
    Status          NVARCHAR(20)    NOT NULL DEFAULT 'pending',
    RequestedAt     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    ResolvedAt      DATETIME2       NULL,
    ResolvedBy      NVARCHAR(100)   NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_PasswordResetRequests_Status')
    CREATE INDEX IX_PasswordResetRequests_Status ON [talentmatch].PasswordResetRequests (Status);

-- 3. JOBS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Jobs' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].Jobs (
    Id                      NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobCode                 NVARCHAR(50)    NOT NULL DEFAULT '',
    Title                   NVARCHAR(200)   NOT NULL,
    Department              NVARCHAR(100)   NOT NULL,
    Organisation            NVARCHAR(200)   NOT NULL DEFAULT '',
    PostingDate             DATETIME2       NOT NULL,
    Status                  NVARCHAR(20)    NOT NULL DEFAULT 'Active',
    JobDescription          NVARCHAR(MAX)   NULL,
    CurrentConfigVersionId  NVARCHAR(36)    NULL,
    CreatedBy               NVARCHAR(100)   NULL,
    CreatedAt               DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt               DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    SpecDocumentId          NVARCHAR(36)    NULL,
    RubricDocumentId        NVARCHAR(36)    NULL,
    OrganizationId          NVARCHAR(36)    NULL,
    DepartmentId            NVARCHAR(36)    NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_Department')
    CREATE INDEX IX_Jobs_Department ON [talentmatch].Jobs (Department);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_CreatedBy')
    CREATE INDEX IX_Jobs_CreatedBy ON [talentmatch].Jobs (CreatedBy);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_Organization_Department')
    CREATE INDEX IX_Jobs_Organization_Department ON [talentmatch].Jobs (OrganizationId, DepartmentId);

-- 4. JOB CONFIG VERSIONS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'JobConfigVersions' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].JobConfigVersions (
    Id                      NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobId                   NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Jobs(Id) ON DELETE CASCADE,
    VersionNumber           INT             NOT NULL DEFAULT 1,
    RubricJson             NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    MustHavesJson           NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    DesiredCriteriaJson     NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    RunsPerApplication      INT             NOT NULL DEFAULT 3,
    AggregationStrategy     NVARCHAR(20)    NOT NULL DEFAULT 'median',
    LonglistThreshold       FLOAT           NOT NULL DEFAULT 70,
    ShortlistThreshold      FLOAT           NOT NULL DEFAULT 85,
    VarianceThreshold       FLOAT           NOT NULL DEFAULT 15,
    RubricApprovalStatus    NVARCHAR(20)    NOT NULL DEFAULT 'draft',
    RubricSource            NVARCHAR(20)    NOT NULL DEFAULT 'manual',
    RawExtractionResponse   NVARCHAR(MAX)   NULL,
    CreatedAt               DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_JobConfigVersions_JobId')
    CREATE INDEX IX_JobConfigVersions_JobId ON [talentmatch].JobConfigVersions (JobId);

-- 5. APPLICATIONS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Applications' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].Applications (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobId           NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Jobs(Id) ON DELETE CASCADE,
    CandidateRef    NVARCHAR(100)   NOT NULL DEFAULT '',
    CandidateName          NVARCHAR(200)   NULL,
    CandidateEmail  NVARCHAR(320)   NULL,
    Status          NVARCHAR(30)    NOT NULL DEFAULT 'Queued',
    FinalScore      FLOAT           NULL,
    FinalDecision   NVARCHAR(30)    NULL,
    Variance        FLOAT           NULL,
    Flagged         BIT             NOT NULL DEFAULT 0,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    TestRunId       NVARCHAR(36)    NULL,
    LastError       NVARCHAR(MAX)   NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Applications_JobId')
    CREATE INDEX IX_Applications_JobId ON [talentmatch].Applications (JobId);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Applications_TestRunId')
    CREATE INDEX IX_Applications_TestRunId ON [talentmatch].Applications (TestRunId) WHERE TestRunId IS NOT NULL;
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Applications_JobId_Status')
    CREATE INDEX IX_Applications_JobId_Status ON [talentmatch].Applications (JobId, Status);

-- 6. APPLICATION DOCUMENTS (metadata only; blobs in Azure Blob Storage or DocumentBlobs table)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ApplicationDocuments' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].ApplicationDocuments (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId   NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Applications(Id) ON DELETE CASCADE,
    FileName        NVARCHAR(500)   NOT NULL DEFAULT '',
    MimeType        NVARCHAR(100)   NOT NULL DEFAULT '',
    SizeBytes       BIGINT          NOT NULL DEFAULT 0,
    Fingerprint     NVARCHAR(64)    NOT NULL DEFAULT '',
    BlobUri         NVARCHAR(1024)  NULL,
    ContentSha256   NVARCHAR(64)    NULL,
    UploadedAt      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApplicationDocuments_ApplicationId')
    CREATE INDEX IX_ApplicationDocuments_ApplicationId ON [talentmatch].ApplicationDocuments (ApplicationId);

-- 7. DOCUMENT BLOBS (raw file content - interim until Azure Blob Storage integration)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DocumentBlobs' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].DocumentBlobs (
    DocumentId      NVARCHAR(36)    NOT NULL PRIMARY KEY REFERENCES [talentmatch].ApplicationDocuments(Id) ON DELETE CASCADE,
    Content         NVARCHAR(MAX)   NOT NULL
);

-- 8. EXTRACTION ARTIFACTS (1:1 with Application)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ExtractionArtifacts' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].ExtractionArtifacts (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId   NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Applications(Id) ON DELETE CASCADE,
    Markdown        NVARCHAR(MAX)   NOT NULL DEFAULT '',
    ToolVersion     NVARCHAR(50)    NOT NULL DEFAULT '',
    Confidence      FLOAT           NOT NULL DEFAULT 0,
    ExtractedAt     DATETIME2       NULL,
    Status          NVARCHAR(20)    NOT NULL DEFAULT 'pending',
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_ExtractionArtifacts_ApplicationId')
    CREATE UNIQUE INDEX UX_ExtractionArtifacts_ApplicationId ON [talentmatch].ExtractionArtifacts (ApplicationId);

-- 9. SCORING RUNS (N per application)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScoringRuns' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].ScoringRuns (
    Id                      NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId           NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Applications(Id) ON DELETE CASCADE,
    VersionId               NVARCHAR(36)    NOT NULL DEFAULT '',
    RunIndex                INT             NOT NULL,
    ModelDeploymentId       NVARCHAR(100)   NOT NULL DEFAULT '',
    PromptVersionId         NVARCHAR(36)    NOT NULL DEFAULT '',
    OverallScore            FLOAT           NOT NULL,
    SubScoresJson           NVARCHAR(MAX)   NOT NULL DEFAULT '{}',
    MustHaveResultJson      NVARCHAR(MAX)   NOT NULL DEFAULT '{}',
    EvidenceCitationsJson   NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    Rationale               NVARCHAR(MAX)   NOT NULL DEFAULT '',
    ImprovementRecsJson     NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    CreatedAt               DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    DurationMs              INT             NOT NULL DEFAULT 0,
    TokenUsageJson          NVARCHAR(MAX)   NULL,
    Status                  NVARCHAR(20)    NOT NULL DEFAULT 'Success',
    RawResponseText         NVARCHAR(MAX)   NULL,
    RawParsedResponseJson   NVARCHAR(MAX)   NULL,
    ParserWarningsJson      NVARCHAR(MAX)   NULL,
    ParserConfidence        FLOAT           NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScoringRuns_ApplicationId')
    CREATE INDEX IX_ScoringRuns_ApplicationId ON [talentmatch].ScoringRuns (ApplicationId);

-- 10. AGGREGATED RESULTS (1:1 with Application)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AggregatedResults' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].AggregatedResults (
    Id                          NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId               NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Applications(Id) ON DELETE CASCADE,
    VersionId                   NVARCHAR(36)    NOT NULL DEFAULT '',
    FinalScore                  FLOAT           NOT NULL,
    FinalSubScoresJson          NVARCHAR(MAX)   NOT NULL DEFAULT '{}',
    Confidence                  FLOAT           NOT NULL,
    Variance                    FLOAT           NOT NULL,
    FinalDecision               NVARCHAR(30)    NOT NULL,
    RationaleText               NVARCHAR(MAX)   NOT NULL DEFAULT '',
    RecommendationsText         NVARCHAR(MAX)   NOT NULL DEFAULT '',
    AllRunsJson                 NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    CreatedAt                   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_AggregatedResults_ApplicationId')
    CREATE UNIQUE INDEX UX_AggregatedResults_ApplicationId ON [talentmatch].AggregatedResults (ApplicationId);

-- 11. MANUAL REVIEWS (1:1 with Application)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ManualReviews' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].ManualReviews (
    Id                  NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId       NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Applications(Id) ON DELETE CASCADE,
    JobId               NVARCHAR(36)    NOT NULL DEFAULT '',
    RubricScoresJson    NVARCHAR(MAX)   NOT NULL DEFAULT '{}',
    OverallComment      NVARCHAR(MAX)   NOT NULL DEFAULT '',
    AdjustedFinalScore  FLOAT           NULL,
    AuditTrailJson      NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    HumanEdited         BIT             NOT NULL DEFAULT 0,
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    LastModifiedBy      NVARCHAR(100)   NOT NULL DEFAULT ''
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_ManualReviews_ApplicationId')
    CREATE UNIQUE INDEX UX_ManualReviews_ApplicationId ON [talentmatch].ManualReviews (ApplicationId);

-- 12. SCORING PROMPTS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScoringPrompts' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].ScoringPrompts (
    Id                      NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobId                   NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Jobs(Id) ON DELETE CASCADE,
    VersionNumber           INT             NOT NULL,
    PromptText              NVARCHAR(MAX)   NOT NULL,
    Status                  NVARCHAR(30)    NOT NULL DEFAULT 'draft',
    CreatedAt               DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    LastModifiedAt          DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    Author                  NVARCHAR(100)   NOT NULL,
    Rating                  INT             NULL,
    Comments                NVARCHAR(MAX)   NULL,
    Source                  NVARCHAR(20)    NOT NULL DEFAULT 'manual',
    GenerationMetadataJson  NVARCHAR(MAX)   NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScoringPrompts_JobId_Status')
    CREATE INDEX IX_ScoringPrompts_JobId_Status ON [talentmatch].ScoringPrompts (JobId, Status);

-- 13. PROMPT TEST RUNS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PromptTestRuns' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].PromptTestRuns (
    Id                  NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobId               NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].Jobs(Id),
    PromptId            NVARCHAR(36)    NOT NULL REFERENCES [talentmatch].ScoringPrompts(Id) ON DELETE CASCADE,
    Status              NVARCHAR(30)    NOT NULL DEFAULT 'pending_scoring',
    ApplicationIdsJson  NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt         DATETIME2       NULL,
    ReviewedBy          NVARCHAR(36)    NULL,
    ReviewNotes         NVARCHAR(MAX)   NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_PromptTestRuns_PromptId')
    CREATE INDEX IX_PromptTestRuns_PromptId ON [talentmatch].PromptTestRuns (PromptId);

-- 14. FAILURE QUEUE (DLQ)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FailureQueueItems' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].FailureQueueItems (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId   NVARCHAR(36)    NOT NULL DEFAULT '',
    JobId           NVARCHAR(36)    NOT NULL DEFAULT '',
    EntityType      NVARCHAR(50)    NOT NULL DEFAULT 'Application',
    EntityId        NVARCHAR(36)    NOT NULL DEFAULT '',
    FailureType     NVARCHAR(50)    NOT NULL DEFAULT '',
    FailureReason   NVARCHAR(MAX)   NOT NULL DEFAULT '',
    AttemptCount    INT             NOT NULL DEFAULT 0,
    RetryCount      INT             NOT NULL DEFAULT 0,
    FirstFailedAt   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    LastAttemptedAt DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CanRetry        BIT             NOT NULL DEFAULT 1,
    Notes           NVARCHAR(MAX)   NULL
);

-- 15. PROCESSING EVENTS (audit ledger)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProcessingEvents' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].ProcessingEvents (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    Actor           NVARCHAR(100)   NOT NULL DEFAULT '',
    Action          NVARCHAR(100)   NOT NULL DEFAULT '',
    EntityType      NVARCHAR(50)    NOT NULL DEFAULT '',
    EntityId        NVARCHAR(36)    NOT NULL DEFAULT '',
    DetailsJson     NVARCHAR(MAX)   NOT NULL DEFAULT '{}',
    Timestamp       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CorrelationId   NVARCHAR(36)    NOT NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProcessingEvents_Timestamp')
    CREATE INDEX IX_ProcessingEvents_Timestamp ON [talentmatch].ProcessingEvents (Timestamp DESC);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProcessingEvents_EntityType_Action')
    CREATE INDEX IX_ProcessingEvents_EntityType_Action ON [talentmatch].ProcessingEvents (EntityType, Action);

-- 16. SCORING BATCHES (platform-mode submissions; see specs/008-platform-mode-shift/platform-contract.md)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScoringBatches' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].ScoringBatches (
    BatchId             NVARCHAR(36)     NOT NULL PRIMARY KEY,
    JobId               NVARCHAR(36)     NOT NULL,
    PromptVersionId     NVARCHAR(36)     NOT NULL,
    ApplicationIdsJson  NVARCHAR(MAX)    NOT NULL,
    RunCount            INT              NOT NULL DEFAULT 1,
    Status              NVARCHAR(20)     NOT NULL DEFAULT 'pending',
    SubmissionId        NVARCHAR(128)    NULL,
    PollUrl             NVARCHAR(512)    NULL,
    Attempt             INT              NOT NULL DEFAULT 0,
    SubmittedAt         DATETIME2        NULL,
    LastPolledAt        DATETIME2        NULL,
    NextPollAt          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    LastError           NVARCHAR(MAX)    NULL,
    LeaseOwner          NVARCHAR(128)    NULL,
    LeasedUntil         DATETIME2        NULL,
    ResultJson          NVARCHAR(MAX)    NULL,
    CancelRequested     BIT              NOT NULL DEFAULT 0,
    CreatedAt           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScoringBatches_JobId')
    CREATE INDEX IX_ScoringBatches_JobId ON [talentmatch].ScoringBatches (JobId);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScoringBatches_Status_NextPollAt')
    CREATE INDEX IX_ScoringBatches_Status_NextPollAt ON [talentmatch].ScoringBatches (Status, NextPollAt);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_ScoringBatches_SubmissionId')
    CREATE UNIQUE INDEX UX_ScoringBatches_SubmissionId ON [talentmatch].ScoringBatches (SubmissionId) WHERE SubmissionId IS NOT NULL;

-- 17. SCORING JOB PROGRESS (job-level rollup, one row per job in platform mode)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScoringJobProgress' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].ScoringJobProgress (
    JobId             NVARCHAR(36)   NOT NULL PRIMARY KEY,
    TotalApps         INT            NOT NULL DEFAULT 0,
    BatchesPending    INT            NOT NULL DEFAULT 0,
    BatchesSubmitted  INT            NOT NULL DEFAULT 0,
    BatchesCompleted  INT            NOT NULL DEFAULT 0,
    BatchesFailed     INT            NOT NULL DEFAULT 0,
    AppsCompleted     INT            NOT NULL DEFAULT 0,
    AppsFailed        INT            NOT NULL DEFAULT 0,
    CancelRequested   BIT            NOT NULL DEFAULT 0,
    StartedAt         DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt         DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);

-- 18. NORMALIZED ORGANIZATION AUTHORIZATION
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Organizations' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].Organizations (
    Id NVARCHAR(36) NOT NULL PRIMARY KEY,
    Name NVARCHAR(200) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'active',
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedBy NVARCHAR(100) NOT NULL,
    CONSTRAINT CK_Organizations_Status CHECK (Status IN ('active', 'retired'))
);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_Organizations_ActiveName')
    CREATE UNIQUE INDEX UX_Organizations_ActiveName ON [talentmatch].Organizations (Name) WHERE Status = 'active';

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Departments' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].Departments (
    Id NVARCHAR(36) NOT NULL PRIMARY KEY,
    OrganizationId NVARCHAR(36) NOT NULL REFERENCES [talentmatch].Organizations(Id),
    Name NVARCHAR(100) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'active',
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedBy NVARCHAR(100) NOT NULL,
    CONSTRAINT UQ_Departments_Id_Organization UNIQUE (Id, OrganizationId),
    CONSTRAINT CK_Departments_Status CHECK (Status IN ('active', 'retired'))
);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_Departments_ActiveOrganizationName')
    CREATE UNIQUE INDEX UX_Departments_ActiveOrganizationName ON [talentmatch].Departments (OrganizationId, Name) WHERE Status = 'active';

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OrganizationMemberships' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].OrganizationMemberships (
    Id NVARCHAR(36) NOT NULL PRIMARY KEY,
    UserId NVARCHAR(36) NOT NULL REFERENCES [talentmatch].Users(Id),
    OrganizationId NVARCHAR(36) NOT NULL REFERENCES [talentmatch].Organizations(Id),
    Status NVARCHAR(20) NOT NULL DEFAULT 'active',
    EffectiveAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RevokedAt DATETIME2 NULL,
    UpdatedBy NVARCHAR(100) NOT NULL,
    CONSTRAINT CK_OrganizationMemberships_Status CHECK ((Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL))
);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_OrganizationMemberships_ActiveUserOrganization')
    CREATE UNIQUE INDEX UX_OrganizationMemberships_ActiveUserOrganization ON [talentmatch].OrganizationMemberships (UserId, OrganizationId) WHERE Status = 'active';

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DepartmentMemberships' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].DepartmentMemberships (
    Id NVARCHAR(36) NOT NULL PRIMARY KEY,
    UserId NVARCHAR(36) NOT NULL REFERENCES [talentmatch].Users(Id),
    OrganizationId NVARCHAR(36) NOT NULL,
    DepartmentId NVARCHAR(36) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'active',
    EffectiveAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RevokedAt DATETIME2 NULL,
    UpdatedBy NVARCHAR(100) NOT NULL,
    CONSTRAINT FK_DepartmentMemberships_Department FOREIGN KEY (DepartmentId, OrganizationId) REFERENCES [talentmatch].Departments(Id, OrganizationId),
    CONSTRAINT CK_DepartmentMemberships_Status CHECK ((Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL))
);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_DepartmentMemberships_ActiveUserDepartment')
    CREATE UNIQUE INDEX UX_DepartmentMemberships_ActiveUserDepartment ON [talentmatch].DepartmentMemberships (UserId, DepartmentId) WHERE Status = 'active';

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'RoleGroupMappings' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].RoleGroupMappings (
    Id NVARCHAR(36) NOT NULL PRIMARY KEY,
    TenantId NVARCHAR(36) NOT NULL,
    GroupObjectId NVARCHAR(36) NOT NULL,
    Role NVARCHAR(30) NOT NULL,
    OrganizationId NVARCHAR(36) NULL,
    DepartmentId NVARCHAR(36) NULL,
    Enabled BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedBy NVARCHAR(100) NOT NULL,
    CONSTRAINT UQ_RoleGroupMappings_TenantGroup UNIQUE (TenantId, GroupObjectId),
    CONSTRAINT CK_RoleGroupMappings_Scope CHECK ((Role = 'admin' AND OrganizationId IS NULL AND DepartmentId IS NULL) OR (Role = 'organization_admin' AND OrganizationId IS NOT NULL AND DepartmentId IS NULL) OR (Role = 'recruiter' AND OrganizationId IS NOT NULL AND DepartmentId IS NOT NULL) OR (Role = 'business_panel' AND OrganizationId IS NOT NULL))
);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'RoleAssignments' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].RoleAssignments (
    Id NVARCHAR(36) NOT NULL PRIMARY KEY,
    UserId NVARCHAR(36) NOT NULL REFERENCES [talentmatch].Users(Id),
    TenantId NVARCHAR(36) NOT NULL,
    UserObjectId NVARCHAR(36) NOT NULL,
    Role NVARCHAR(30) NOT NULL,
    OrganizationId NVARCHAR(36) NULL,
    DepartmentId NVARCHAR(36) NULL,
    RoleGroupMappingId NVARCHAR(36) NULL REFERENCES [talentmatch].RoleGroupMappings(Id),
    Source NVARCHAR(20) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'active',
    EffectiveAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RevokedAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedBy NVARCHAR(100) NOT NULL,
    CONSTRAINT CK_RoleAssignments_Status CHECK ((Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL))
);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_RoleAssignments_ActiveGroup')
    CREATE UNIQUE INDEX UX_RoleAssignments_ActiveGroup ON [talentmatch].RoleAssignments (TenantId, UserObjectId, RoleGroupMappingId) WHERE Status = 'active' AND RoleGroupMappingId IS NOT NULL;
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_RoleAssignments_Idempotency')
    CREATE UNIQUE INDEX UX_RoleAssignments_Idempotency ON [talentmatch].RoleAssignments (TenantId, UserObjectId, Source, Role, OrganizationId, DepartmentId);

