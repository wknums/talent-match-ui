-- ============================================================
-- TalentMatch Unified Database Schema — SQLite (dev)
-- Mirror of schema.sql (Azure SQL) for local development.
-- ============================================================

-- 1. USERS
CREATE TABLE IF NOT EXISTS Users (
    Id              TEXT    NOT NULL PRIMARY KEY,
    Username        TEXT    NOT NULL,
    Role            TEXT    NOT NULL,
    FullName        TEXT    NOT NULL DEFAULT '',
    Email           TEXT    NOT NULL DEFAULT '',
    Department      TEXT    NOT NULL DEFAULT '',
    PasswordHash    TEXT    NULL,
    CreatedAt       TEXT    NOT NULL DEFAULT (datetime('now')),
    LastLogin       TEXT    NULL,
    PasswordResetRequired INTEGER NOT NULL DEFAULT 0,
    AuthenticationProvider TEXT NOT NULL DEFAULT 'simple',
    EntraTenantId TEXT NULL,
    EntraObjectId TEXT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1,
    CHECK ((AuthenticationProvider = 'simple' AND PasswordHash IS NOT NULL AND EntraTenantId IS NULL AND EntraObjectId IS NULL) OR (AuthenticationProvider = 'entra' AND PasswordHash IS NULL AND EntraTenantId IS NOT NULL AND EntraObjectId IS NOT NULL AND PasswordResetRequired = 0))
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_Username ON Users (Username);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_EntraIdentity ON Users (EntraTenantId, EntraObjectId) WHERE AuthenticationProvider = 'entra';

-- 2. PASSWORD RESET REQUESTS
CREATE TABLE IF NOT EXISTS PasswordResetRequests (
    Id              TEXT    NOT NULL PRIMARY KEY,
    UserId          TEXT    NOT NULL,
    Username        TEXT    NOT NULL,
    FullName        TEXT    NOT NULL DEFAULT '',
    Reason          TEXT    NOT NULL DEFAULT '',
    Status          TEXT    NOT NULL DEFAULT 'pending',
    RequestedAt     TEXT    NOT NULL DEFAULT (datetime('now')),
    CreatedAt       TEXT    NOT NULL DEFAULT (datetime('now')),
    ResolvedAt      TEXT    NULL,
    ResolvedBy      TEXT    NULL
);
CREATE INDEX IF NOT EXISTS IX_PasswordResetRequests_Status ON PasswordResetRequests (Status);

-- 3. JOBS
CREATE TABLE IF NOT EXISTS Jobs (
    Id                      TEXT    NOT NULL PRIMARY KEY,
    JobCode                 TEXT    NOT NULL DEFAULT '',
    Title                   TEXT    NOT NULL,
    Department              TEXT    NOT NULL,
    Organisation            TEXT    NOT NULL DEFAULT '',
    PostingDate             TEXT    NOT NULL,
    Status                  TEXT    NOT NULL DEFAULT 'Active',
    JobDescription          TEXT    NULL,
    CurrentConfigVersionId  TEXT    NULL,
    CreatedBy               TEXT    NULL,
    CreatedAt               TEXT    NOT NULL DEFAULT (datetime('now')),
    UpdatedAt               TEXT    NOT NULL DEFAULT (datetime('now')),
    SpecDocumentId          TEXT    NULL,
    RubricDocumentId        TEXT    NULL,
    OrganizationId          TEXT    NULL,
    DepartmentId            TEXT    NULL
);
CREATE INDEX IF NOT EXISTS IX_Jobs_Department ON Jobs (Department);
CREATE INDEX IF NOT EXISTS IX_Jobs_CreatedBy ON Jobs (CreatedBy);
CREATE INDEX IF NOT EXISTS IX_Jobs_Organization_Department ON Jobs (OrganizationId, DepartmentId);

-- 4. JOB CONFIG VERSIONS
CREATE TABLE IF NOT EXISTS JobConfigVersions (
    Id                      TEXT    NOT NULL PRIMARY KEY,
    JobId                   TEXT    NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,
    VersionNumber           INTEGER NOT NULL DEFAULT 1,
    RubricJson              TEXT    NOT NULL DEFAULT '[]',
    MustHavesJson           TEXT    NOT NULL DEFAULT '[]',
    DesiredCriteriaJson     TEXT    NOT NULL DEFAULT '[]',
    RunsPerApplication      INTEGER NOT NULL DEFAULT 3,
    AggregationStrategy     TEXT    NOT NULL DEFAULT 'median',
    LonglistThreshold       REAL    NOT NULL DEFAULT 70,
    ShortlistThreshold      REAL    NOT NULL DEFAULT 85,
    VarianceThreshold       REAL    NOT NULL DEFAULT 15,
    RubricApprovalStatus    TEXT    NOT NULL DEFAULT 'draft',
    RubricSource            TEXT    NOT NULL DEFAULT 'manual',
    RawExtractionResponse   TEXT    NULL,
    CreatedAt               TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS IX_JobConfigVersions_JobId ON JobConfigVersions (JobId);

-- 5. APPLICATIONS
CREATE TABLE IF NOT EXISTS Applications (
    Id              TEXT    NOT NULL PRIMARY KEY,
    JobId           TEXT    NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,
    CandidateRef    TEXT    NOT NULL DEFAULT '',
    CandidateName   TEXT    NULL,
    CandidateEmail  TEXT    NULL,
    Status          TEXT    NOT NULL DEFAULT 'Queued',
    FinalScore      REAL    NULL,
    FinalDecision   TEXT    NULL,
    Variance        REAL    NULL,
    Flagged         INTEGER NOT NULL DEFAULT 0,
    CreatedAt       TEXT    NOT NULL DEFAULT (datetime('now')),
    UpdatedAt       TEXT    NOT NULL DEFAULT (datetime('now')),
    TestRunId       TEXT    NULL,
    LastError       TEXT    NULL
);
CREATE INDEX IF NOT EXISTS IX_Applications_JobId ON Applications (JobId);
CREATE INDEX IF NOT EXISTS IX_Applications_TestRunId ON Applications (TestRunId);
CREATE INDEX IF NOT EXISTS IX_Applications_JobId_Status ON Applications (JobId, Status);

-- 6. APPLICATION DOCUMENTS
CREATE TABLE IF NOT EXISTS ApplicationDocuments (
    Id              TEXT    NOT NULL PRIMARY KEY,
    ApplicationId   TEXT    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
    FileName        TEXT    NOT NULL DEFAULT '',
    MimeType        TEXT    NOT NULL DEFAULT '',
    SizeBytes       INTEGER NOT NULL DEFAULT 0,
    Fingerprint     TEXT    NOT NULL DEFAULT '',
    UploadedAt      TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS IX_ApplicationDocuments_ApplicationId ON ApplicationDocuments (ApplicationId);

-- 7. DOCUMENT BLOBS
CREATE TABLE IF NOT EXISTS DocumentBlobs (
    DocumentId      TEXT    NOT NULL PRIMARY KEY REFERENCES ApplicationDocuments(Id) ON DELETE CASCADE,
    Content         TEXT    NOT NULL
);

-- 8. EXTRACTION ARTIFACTS
CREATE TABLE IF NOT EXISTS ExtractionArtifacts (
    Id              TEXT    NOT NULL PRIMARY KEY,
    ApplicationId   TEXT    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
    Markdown        TEXT    NOT NULL DEFAULT '',
    ToolVersion     TEXT    NOT NULL DEFAULT '',
    Confidence      REAL    NOT NULL DEFAULT 0,
    ExtractedAt     TEXT    NULL,
    Status          TEXT    NOT NULL DEFAULT 'pending',
    CreatedAt       TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ExtractionArtifacts_ApplicationId ON ExtractionArtifacts (ApplicationId);

-- 9. SCORING RUNS
CREATE TABLE IF NOT EXISTS ScoringRuns (
    Id                      TEXT    NOT NULL PRIMARY KEY,
    ApplicationId           TEXT    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
    VersionId               TEXT    NOT NULL DEFAULT '',
    RunIndex                INTEGER NOT NULL,
    ModelDeploymentId       TEXT    NOT NULL DEFAULT '',
    PromptVersionId         TEXT    NOT NULL DEFAULT '',
    OverallScore            REAL    NOT NULL,
    SubScoresJson           TEXT    NOT NULL DEFAULT '{}',
    MustHaveResultJson      TEXT    NOT NULL DEFAULT '{}',
    EvidenceCitationsJson   TEXT    NOT NULL DEFAULT '[]',
    Rationale               TEXT    NOT NULL DEFAULT '',
    ImprovementRecsJson     TEXT    NOT NULL DEFAULT '[]',
    CreatedAt               TEXT    NOT NULL DEFAULT (datetime('now')),
    DurationMs              INTEGER NOT NULL DEFAULT 0,
    TokenUsageJson          TEXT    NULL,
    Status                  TEXT    NOT NULL DEFAULT 'Success',
    RawResponseText         TEXT    NULL,
    RawParsedResponseJson   TEXT    NULL,
    ParserWarningsJson      TEXT    NULL,
    ParserConfidence        REAL    NULL
);
CREATE INDEX IF NOT EXISTS IX_ScoringRuns_ApplicationId ON ScoringRuns (ApplicationId);

-- 10. AGGREGATED RESULTS
CREATE TABLE IF NOT EXISTS AggregatedResults (
    Id                          TEXT    NOT NULL PRIMARY KEY,
    ApplicationId               TEXT    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
    VersionId                   TEXT    NOT NULL DEFAULT '',
    FinalScore                  REAL    NOT NULL,
    FinalSubScoresJson          TEXT    NOT NULL DEFAULT '{}',
    Confidence                  REAL    NOT NULL,
    Variance                    REAL    NOT NULL,
    FinalDecision               TEXT    NOT NULL,
    RationaleText               TEXT    NOT NULL DEFAULT '',
    RecommendationsText         TEXT    NOT NULL DEFAULT '',
    AllRunsJson                 TEXT    NOT NULL DEFAULT '[]',
    CreatedAt                   TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_AggregatedResults_ApplicationId ON AggregatedResults (ApplicationId);

-- 11. MANUAL REVIEWS
CREATE TABLE IF NOT EXISTS ManualReviews (
    Id                  TEXT    NOT NULL PRIMARY KEY,
    ApplicationId       TEXT    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
    JobId               TEXT    NOT NULL DEFAULT '',
    RubricScoresJson    TEXT    NOT NULL DEFAULT '{}',
    OverallComment      TEXT    NOT NULL DEFAULT '',
    AdjustedFinalScore  REAL    NULL,
    AuditTrailJson      TEXT    NOT NULL DEFAULT '[]',
    HumanEdited         INTEGER NOT NULL DEFAULT 0,
    CreatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
    UpdatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
    LastModifiedBy      TEXT    NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ManualReviews_ApplicationId ON ManualReviews (ApplicationId);

-- 12. SCORING PROMPTS
CREATE TABLE IF NOT EXISTS ScoringPrompts (
    Id                      TEXT    NOT NULL PRIMARY KEY,
    JobId                   TEXT    NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,
    VersionNumber           INTEGER NOT NULL,
    PromptText              TEXT    NOT NULL,
    Status                  TEXT    NOT NULL DEFAULT 'draft',
    CreatedAt               TEXT    NOT NULL DEFAULT (datetime('now')),
    LastModifiedAt          TEXT    NOT NULL DEFAULT (datetime('now')),
    Author                  TEXT    NOT NULL,
    Rating                  INTEGER NULL,
    Comments                TEXT    NULL,
    Source                  TEXT    NOT NULL DEFAULT 'manual',
    GenerationMetadataJson  TEXT    NULL
);
CREATE INDEX IF NOT EXISTS IX_ScoringPrompts_JobId_Status ON ScoringPrompts (JobId, Status);

-- 13. PROMPT TEST RUNS
CREATE TABLE IF NOT EXISTS PromptTestRuns (
    Id                  TEXT    NOT NULL PRIMARY KEY,
    JobId               TEXT    NOT NULL REFERENCES Jobs(Id),
    PromptId            TEXT    NOT NULL REFERENCES ScoringPrompts(Id) ON DELETE CASCADE,
    Status              TEXT    NOT NULL DEFAULT 'pending_scoring',
    ApplicationIdsJson  TEXT    NOT NULL DEFAULT '[]',
    CreatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
    CompletedAt         TEXT    NULL,
    ReviewedBy          TEXT    NULL,
    ReviewNotes         TEXT    NULL
);
CREATE INDEX IF NOT EXISTS IX_PromptTestRuns_PromptId ON PromptTestRuns (PromptId);

-- 14. FAILURE QUEUE (DLQ)
CREATE TABLE IF NOT EXISTS FailureQueueItems (
    Id              TEXT    NOT NULL PRIMARY KEY,
    ApplicationId   TEXT    NOT NULL DEFAULT '',
    JobId           TEXT    NOT NULL DEFAULT '',
        EntityType      TEXT    NOT NULL DEFAULT 'Application',
        EntityId        TEXT    NOT NULL DEFAULT '',
    FailureType     TEXT    NOT NULL DEFAULT '',
    FailureReason   TEXT    NOT NULL DEFAULT '',
    AttemptCount    INTEGER NOT NULL DEFAULT 0,
        RetryCount      INTEGER NOT NULL DEFAULT 0,
    FirstFailedAt   TEXT    NOT NULL DEFAULT (datetime('now')),
    LastAttemptedAt TEXT    NOT NULL DEFAULT (datetime('now')),
        CreatedAt       TEXT    NOT NULL DEFAULT '',
        UpdatedAt       TEXT    NOT NULL DEFAULT '',
    CanRetry        INTEGER NOT NULL DEFAULT 1,
    Notes           TEXT    NULL
);

-- 15. PROCESSING EVENTS (audit ledger)
CREATE TABLE IF NOT EXISTS ProcessingEvents (
    Id              TEXT    NOT NULL PRIMARY KEY,
    Actor           TEXT    NOT NULL DEFAULT '',
    Action          TEXT    NOT NULL DEFAULT '',
    EntityType      TEXT    NOT NULL DEFAULT '',
    EntityId        TEXT    NOT NULL DEFAULT '',
    DetailsJson     TEXT    NOT NULL DEFAULT '{}',
    Timestamp       TEXT    NOT NULL DEFAULT (datetime('now')),
    CorrelationId   TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ProcessingEvents_Timestamp ON ProcessingEvents (Timestamp);
CREATE INDEX IF NOT EXISTS IX_ProcessingEvents_EntityType_Action ON ProcessingEvents (EntityType, Action);

-- 16. SCORING BATCHES (platform-mode submissions)
CREATE TABLE IF NOT EXISTS ScoringBatches (
    BatchId             TEXT    NOT NULL PRIMARY KEY,
    JobId               TEXT    NOT NULL,
    PromptVersionId     TEXT    NOT NULL,
    ApplicationIdsJson  TEXT    NOT NULL,
    RunCount            INTEGER NOT NULL DEFAULT 1,
    Status              TEXT    NOT NULL DEFAULT 'pending',
    SubmissionId        TEXT    NULL,
    PollUrl             TEXT    NULL,
    Attempt             INTEGER NOT NULL DEFAULT 0,
    SubmittedAt         TEXT    NULL,
    LastPolledAt        TEXT    NULL,
    NextPollAt          TEXT    NOT NULL DEFAULT (datetime('now')),
    LastError           TEXT    NULL,
    LeaseOwner          TEXT    NULL,
    LeasedUntil         TEXT    NULL,
    ResultJson          TEXT    NULL,
    CancelRequested     INTEGER NOT NULL DEFAULT 0,
    CreatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
    UpdatedAt           TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS IX_ScoringBatches_JobId ON ScoringBatches (JobId);
CREATE INDEX IF NOT EXISTS IX_ScoringBatches_Status_NextPollAt ON ScoringBatches (Status, NextPollAt);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ScoringBatches_SubmissionId
    ON ScoringBatches (SubmissionId) WHERE SubmissionId IS NOT NULL;

-- 17. SCORING JOB PROGRESS
CREATE TABLE IF NOT EXISTS ScoringJobProgress (
    JobId             TEXT    NOT NULL PRIMARY KEY,
    TotalApps         INTEGER NOT NULL DEFAULT 0,
    BatchesPending    INTEGER NOT NULL DEFAULT 0,
    BatchesSubmitted  INTEGER NOT NULL DEFAULT 0,
    BatchesCompleted  INTEGER NOT NULL DEFAULT 0,
    BatchesFailed     INTEGER NOT NULL DEFAULT 0,
    AppsCompleted     INTEGER NOT NULL DEFAULT 0,
    AppsFailed        INTEGER NOT NULL DEFAULT 0,
    CancelRequested   INTEGER NOT NULL DEFAULT 0,
    StartedAt         TEXT    NOT NULL DEFAULT (datetime('now')),
    UpdatedAt         TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- 18. NORMALIZED ORGANIZATION AUTHORIZATION
CREATE TABLE IF NOT EXISTS Organizations (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'active' CHECK (Status IN ('active', 'retired')),
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedBy TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Organizations_ActiveName ON Organizations (Name) WHERE Status = 'active';

CREATE TABLE IF NOT EXISTS Departments (
    Id TEXT NOT NULL PRIMARY KEY,
    OrganizationId TEXT NOT NULL REFERENCES Organizations(Id),
    Name TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'active' CHECK (Status IN ('active', 'retired')),
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedBy TEXT NOT NULL,
    UNIQUE (Id, OrganizationId)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Departments_ActiveOrganizationName ON Departments (OrganizationId, Name) WHERE Status = 'active';

CREATE TABLE IF NOT EXISTS OrganizationMemberships (
    Id TEXT NOT NULL PRIMARY KEY,
    UserId TEXT NOT NULL REFERENCES Users(Id),
    OrganizationId TEXT NOT NULL REFERENCES Organizations(Id),
    Status TEXT NOT NULL DEFAULT 'active',
    EffectiveAt TEXT NOT NULL DEFAULT (datetime('now')),
    RevokedAt TEXT NULL,
    UpdatedBy TEXT NOT NULL,
    CHECK ((Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_OrganizationMemberships_ActiveUserOrganization ON OrganizationMemberships (UserId, OrganizationId) WHERE Status = 'active';

CREATE TABLE IF NOT EXISTS DepartmentMemberships (
    Id TEXT NOT NULL PRIMARY KEY,
    UserId TEXT NOT NULL REFERENCES Users(Id),
    OrganizationId TEXT NOT NULL,
    DepartmentId TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'active',
    EffectiveAt TEXT NOT NULL DEFAULT (datetime('now')),
    RevokedAt TEXT NULL,
    UpdatedBy TEXT NOT NULL,
    FOREIGN KEY (DepartmentId, OrganizationId) REFERENCES Departments(Id, OrganizationId),
    CHECK ((Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_DepartmentMemberships_ActiveUserDepartment ON DepartmentMemberships (UserId, DepartmentId) WHERE Status = 'active';

CREATE TABLE IF NOT EXISTS RoleGroupMappings (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL,
    GroupObjectId TEXT NOT NULL,
    Role TEXT NOT NULL,
    OrganizationId TEXT NULL,
    DepartmentId TEXT NULL,
    Enabled INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedBy TEXT NOT NULL,
    UNIQUE (TenantId, GroupObjectId),
    CHECK ((Role = 'admin' AND OrganizationId IS NULL AND DepartmentId IS NULL) OR (Role = 'organization_admin' AND OrganizationId IS NOT NULL AND DepartmentId IS NULL) OR (Role = 'recruiter' AND OrganizationId IS NOT NULL AND DepartmentId IS NOT NULL) OR (Role = 'business_panel' AND OrganizationId IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS RoleAssignments (
    Id TEXT NOT NULL PRIMARY KEY,
    UserId TEXT NOT NULL REFERENCES Users(Id),
    TenantId TEXT NOT NULL,
    UserObjectId TEXT NOT NULL,
    Role TEXT NOT NULL,
    OrganizationId TEXT NULL,
    DepartmentId TEXT NULL,
    RoleGroupMappingId TEXT NULL REFERENCES RoleGroupMappings(Id),
    Source TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'active',
    EffectiveAt TEXT NOT NULL DEFAULT (datetime('now')),
    RevokedAt TEXT NULL,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedBy TEXT NOT NULL,
    CHECK ((Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_RoleAssignments_ActiveGroup ON RoleAssignments (TenantId, UserObjectId, RoleGroupMappingId) WHERE Status = 'active' AND RoleGroupMappingId IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_RoleAssignments_Idempotency ON RoleAssignments (TenantId, UserObjectId, Source, Role, OrganizationId, DepartmentId);

