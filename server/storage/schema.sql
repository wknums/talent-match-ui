-- ============================================================
-- TalentMatch Unified Database Schema
-- Single database shared by Stack A (Node/Express) and Stack B (.NET Blazor)
-- ============================================================

-- 1. USERS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users')
CREATE TABLE Users (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    Username        NVARCHAR(100)   NOT NULL,
    Role            NVARCHAR(20)    NOT NULL,
    FullName        NVARCHAR(200)   NOT NULL DEFAULT '',
    Email           NVARCHAR(320)   NOT NULL DEFAULT '',
    Department      NVARCHAR(100)   NOT NULL DEFAULT '',
    PasswordHash    NVARCHAR(128)   NOT NULL,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    LastLogin       DATETIME2       NULL,
    PasswordResetRequired BIT       NOT NULL DEFAULT 0
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_Users_Username')
    CREATE UNIQUE INDEX UX_Users_Username ON Users (Username);

-- 2. PASSWORD RESET REQUESTS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PasswordResetRequests')
CREATE TABLE PasswordResetRequests (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    UserId          NVARCHAR(36)    NOT NULL,
    Username        NVARCHAR(100)   NOT NULL,
    FullName        NVARCHAR(200)   NOT NULL DEFAULT '',
    Status          NVARCHAR(20)    NOT NULL DEFAULT 'pending',
    RequestedAt     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    ResolvedAt      DATETIME2       NULL,
    ResolvedBy      NVARCHAR(100)   NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_PasswordResetRequests_Status')
    CREATE INDEX IX_PasswordResetRequests_Status ON PasswordResetRequests (Status);

-- 3. JOBS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Jobs')
CREATE TABLE Jobs (
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
    RubricDocumentId        NVARCHAR(36)    NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_Department')
    CREATE INDEX IX_Jobs_Department ON Jobs (Department);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_CreatedBy')
    CREATE INDEX IX_Jobs_CreatedBy ON Jobs (CreatedBy);

-- 4. JOB CONFIG VERSIONS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'JobConfigVersions')
CREATE TABLE JobConfigVersions (
    Id                      NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobId                   NVARCHAR(36)    NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,
    VersionNumber           INT             NOT NULL DEFAULT 1,
    RubricJson              NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
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
    CREATE INDEX IX_JobConfigVersions_JobId ON JobConfigVersions (JobId);

-- 5. APPLICATIONS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Applications')
CREATE TABLE Applications (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobId           NVARCHAR(36)    NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,
    CandidateRef    NVARCHAR(100)   NOT NULL DEFAULT '',
    CandidateName   NVARCHAR(200)   NULL,
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
    CREATE INDEX IX_Applications_JobId ON Applications (JobId);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Applications_TestRunId')
    CREATE INDEX IX_Applications_TestRunId ON Applications (TestRunId) WHERE TestRunId IS NOT NULL;
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Applications_JobId_Status')
    CREATE INDEX IX_Applications_JobId_Status ON Applications (JobId, Status);

-- 6. APPLICATION DOCUMENTS (metadata only; blobs in Azure Blob Storage or DocumentBlobs table)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ApplicationDocuments')
CREATE TABLE ApplicationDocuments (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId   NVARCHAR(36)    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
    FileName        NVARCHAR(500)   NOT NULL DEFAULT '',
    MimeType        NVARCHAR(100)   NOT NULL DEFAULT '',
    SizeBytes       BIGINT          NOT NULL DEFAULT 0,
    Fingerprint     NVARCHAR(64)    NOT NULL DEFAULT '',
    UploadedAt      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApplicationDocuments_ApplicationId')
    CREATE INDEX IX_ApplicationDocuments_ApplicationId ON ApplicationDocuments (ApplicationId);

-- 7. DOCUMENT BLOBS (raw file content - interim until Azure Blob Storage integration)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DocumentBlobs')
CREATE TABLE DocumentBlobs (
    DocumentId      NVARCHAR(36)    NOT NULL PRIMARY KEY REFERENCES ApplicationDocuments(Id) ON DELETE CASCADE,
    Content         NVARCHAR(MAX)   NOT NULL
);

-- 8. EXTRACTION ARTIFACTS (1:1 with Application)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ExtractionArtifacts')
CREATE TABLE ExtractionArtifacts (
    Id              NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId   NVARCHAR(36)    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
    Markdown        NVARCHAR(MAX)   NOT NULL DEFAULT '',
    ToolVersion     NVARCHAR(50)    NOT NULL DEFAULT '',
    Confidence      FLOAT           NOT NULL DEFAULT 0,
    ExtractedAt     DATETIME2       NULL,
    Status          NVARCHAR(20)    NOT NULL DEFAULT 'pending',
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_ExtractionArtifacts_ApplicationId')
    CREATE UNIQUE INDEX UX_ExtractionArtifacts_ApplicationId ON ExtractionArtifacts (ApplicationId);

-- 9. SCORING RUNS (N per application)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScoringRuns')
CREATE TABLE ScoringRuns (
    Id                      NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId           NVARCHAR(36)    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
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
    CREATE INDEX IX_ScoringRuns_ApplicationId ON ScoringRuns (ApplicationId);

-- 10. AGGREGATED RESULTS (1:1 with Application)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AggregatedResults')
CREATE TABLE AggregatedResults (
    Id                          NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId               NVARCHAR(36)    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
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
    CREATE UNIQUE INDEX UX_AggregatedResults_ApplicationId ON AggregatedResults (ApplicationId);

-- 11. MANUAL REVIEWS (1:1 with Application)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ManualReviews')
CREATE TABLE ManualReviews (
    Id                  NVARCHAR(36)    NOT NULL PRIMARY KEY,
    ApplicationId       NVARCHAR(36)    NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
    JobId               NVARCHAR(36)    NOT NULL DEFAULT '',
    RubricScoresJson    NVARCHAR(MAX)   NOT NULL DEFAULT '{}',
    OverallComment      NVARCHAR(MAX)   NOT NULL DEFAULT '',
    AdjustedFinalScore  FLOAT           NULL,
    AuditTrailJson      NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    LastModifiedBy      NVARCHAR(100)   NOT NULL DEFAULT ''
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_ManualReviews_ApplicationId')
    CREATE UNIQUE INDEX UX_ManualReviews_ApplicationId ON ManualReviews (ApplicationId);

-- 12. SCORING PROMPTS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScoringPrompts')
CREATE TABLE ScoringPrompts (
    Id                      NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobId                   NVARCHAR(36)    NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,
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
    CREATE INDEX IX_ScoringPrompts_JobId_Status ON ScoringPrompts (JobId, Status);

-- 13. PROMPT TEST RUNS
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PromptTestRuns')
CREATE TABLE PromptTestRuns (
    Id                  NVARCHAR(36)    NOT NULL PRIMARY KEY,
    JobId               NVARCHAR(36)    NOT NULL REFERENCES Jobs(Id),
    PromptId            NVARCHAR(36)    NOT NULL REFERENCES ScoringPrompts(Id) ON DELETE CASCADE,
    Status              NVARCHAR(30)    NOT NULL DEFAULT 'pending_scoring',
    ApplicationIdsJson  NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt         DATETIME2       NULL,
    ReviewedBy          NVARCHAR(36)    NULL,
    ReviewNotes         NVARCHAR(MAX)   NULL
);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_PromptTestRuns_PromptId')
    CREATE INDEX IX_PromptTestRuns_PromptId ON PromptTestRuns (PromptId);

-- 14. FAILURE QUEUE (DLQ)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FailureQueueItems')
CREATE TABLE FailureQueueItems (
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
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProcessingEvents')
CREATE TABLE ProcessingEvents (
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
    CREATE INDEX IX_ProcessingEvents_Timestamp ON ProcessingEvents (Timestamp DESC);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProcessingEvents_EntityType_Action')
    CREATE INDEX IX_ProcessingEvents_EntityType_Action ON ProcessingEvents (EntityType, Action);

-- Drop the old kv_store table if it exists (migrate data first!)
-- IF EXISTS (SELECT * FROM sys.tables WHERE name = 'kv_store') DROP TABLE kv_store;
