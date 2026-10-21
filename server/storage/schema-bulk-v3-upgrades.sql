-- Stack B delegated Microsoft 365 bulk-scoring upgrade for Azure SQL.
-- This script is idempotent and runs before the shared additive schema batches.
-- It preserves completed history and refuses to translate active old-model work.

IF OBJECT_ID(N'[talentmatch].[BulkScoringRequests]', N'U') IS NOT NULL
   AND COL_LENGTH('talentmatch.BulkScoringRequests', 'AccessModel') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM [talentmatch].[BulkScoringRequests]
       WHERE [Status] IN ('draft', 'active')
   )
    THROW 51013, 'delegated_v3_old_active_work_requires_drain_or_discard', 1;
GO

IF OBJECT_ID(N'[talentmatch].[BulkImportSettings]', N'U') IS NOT NULL
   AND COL_LENGTH('talentmatch.BulkImportSettings', 'ConfigurationVersion') IS NULL
    ALTER TABLE [talentmatch].[BulkImportSettings]
        ADD [ConfigurationVersion] int NOT NULL
            CONSTRAINT [DF_BulkImportSettings_ConfigurationVersion] DEFAULT 0;
GO

IF OBJECT_ID(N'[talentmatch].[BulkScoringRequests]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkScoringRequests_Status_ExpiresAt' AND object_id = OBJECT_ID('talentmatch.BulkScoringRequests'))
        DROP INDEX [IX_BulkScoringRequests_Status_ExpiresAt] ON [talentmatch].[BulkScoringRequests];
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkScoringRequests_Status_OwningStack' AND object_id = OBJECT_ID('talentmatch.BulkScoringRequests'))
        DROP INDEX [IX_BulkScoringRequests_Status_OwningStack] ON [talentmatch].[BulkScoringRequests];

    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringRequests_AccessModel]', N'C') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] DROP CONSTRAINT [CK_BulkScoringRequests_AccessModel];
    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringRequests_OwningStack]', N'C') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] DROP CONSTRAINT [CK_BulkScoringRequests_OwningStack];
    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringRequests_SourceKind]', N'C') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] DROP CONSTRAINT [CK_BulkScoringRequests_SourceKind];

    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'CapturedScoringConfigVersionId') IS NULL
       AND COL_LENGTH('talentmatch.BulkScoringRequests', 'JobConfigVersionId') IS NOT NULL
        EXEC sp_rename N'[talentmatch].[BulkScoringRequests].[JobConfigVersionId]', N'CapturedScoringConfigVersionId', 'COLUMN';

    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'ConnectionId') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [ConnectionId] nvarchar(36) NULL;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'AcknowledgedByActorId') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [AcknowledgedByActorId] nvarchar(128) NULL;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'SourceAccountBindingHash') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [SourceAccountBindingHash] nvarchar(128) NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_SourceAccountBindingHash] DEFAULT N'';
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'RasterDpi') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [RasterDpi] int NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_RasterDpi] DEFAULT 150;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'MaxFilesPerRequest') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [MaxFilesPerRequest] int NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_MaxFilesPerRequest] DEFAULT 30000;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'MaxBytesPerFile') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [MaxBytesPerFile] bigint NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_MaxBytesPerFile] DEFAULT 8388608;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'MaxTotalBytes') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [MaxTotalBytes] bigint NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_MaxTotalBytes] DEFAULT 53687091200;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'DiscoveryStepMaxFiles') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [DiscoveryStepMaxFiles] int NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_DiscoveryStepMaxFiles] DEFAULT 1000;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'DiscoveryStepMaxEntries') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [DiscoveryStepMaxEntries] int NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_DiscoveryStepMaxEntries] DEFAULT 10000;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'DiscoveryStepMaxSeconds') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [DiscoveryStepMaxSeconds] int NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_DiscoveryStepMaxSeconds] DEFAULT 45;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'DraftReviewVersion') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [DraftReviewVersion] int NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_DraftReviewVersion] DEFAULT 0;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'IncludedItemCount') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [IncludedItemCount] int NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_IncludedItemCount] DEFAULT 0;
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'IncludedBytes') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD [IncludedBytes] bigint NOT NULL
            CONSTRAINT [DF_BulkScoringRequests_IncludedBytes] DEFAULT 0;
END;
GO

IF OBJECT_ID(N'[talentmatch].[BulkScoringRequests]', N'U') IS NOT NULL
BEGIN
    UPDATE [talentmatch].[BulkScoringRequests]
    SET [IncludedItemCount] = [DiscoveredCount],
        [IncludedBytes] = [TotalEligibleBytes],
        [AcknowledgedByActorId] = CASE WHEN [AcknowledgedAt] IS NULL THEN NULL ELSE [OwnerActorId] END,
        [SourceKind] = CASE WHEN [SourceKind] = N'onedrive' THEN N'onedrive_business' ELSE [SourceKind] END;

    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'AccessModel') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] DROP COLUMN [AccessModel];
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'OwningStack') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] DROP COLUMN [OwningStack];
    IF COL_LENGTH('talentmatch.BulkScoringRequests', 'LimitsJson') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] DROP COLUMN [LimitsJson];

    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringRequests_Acknowledgment]', N'C') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD CONSTRAINT [CK_BulkScoringRequests_Acknowledgment]
            CHECK ((AcknowledgedAt IS NULL AND AcknowledgedByActorId IS NULL) OR (AcknowledgedAt IS NOT NULL AND AcknowledgedByActorId = OwnerActorId));
    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringRequests_FrozenLimits]', N'C') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD CONSTRAINT [CK_BulkScoringRequests_FrozenLimits]
            CHECK (MaxFilesPerRequest BETWEEN 1 AND 100000 AND MaxBytesPerFile > 0 AND MaxTotalBytes BETWEEN 1048576 AND 1099511627776 AND DiscoveryStepMaxFiles BETWEEN 1 AND 1000 AND DiscoveryStepMaxEntries > 0 AND DiscoveryStepMaxSeconds > 0);
    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringRequests_IncludedLimits]', N'C') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD CONSTRAINT [CK_BulkScoringRequests_IncludedLimits]
            CHECK (IncludedItemCount >= 0 AND IncludedItemCount <= MaxFilesPerRequest AND IncludedBytes >= 0 AND IncludedBytes <= MaxTotalBytes);
    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringRequests_SourceKind]', N'C') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringRequests] ADD CONSTRAINT [CK_BulkScoringRequests_SourceKind]
            CHECK (SourceKind IN ('sharepoint', 'onedrive_business'));
END;
GO

IF OBJECT_ID(N'[talentmatch].[BulkScoringItems]', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringItems_Reason]', N'C') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkScoringItems] DROP CONSTRAINT [CK_BulkScoringItems_Reason];

    IF COL_LENGTH('talentmatch.BulkScoringItems', 'OriginalPageCount') IS NULL
       AND COL_LENGTH('talentmatch.BulkScoringItems', 'PageCount') IS NOT NULL
        EXEC sp_rename N'[talentmatch].[BulkScoringItems].[PageCount]', N'OriginalPageCount', 'COLUMN';
    IF COL_LENGTH('talentmatch.BulkScoringItems', 'ConvertedPageCount') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringItems] ADD [ConvertedPageCount] int NULL;
    IF COL_LENGTH('talentmatch.BulkScoringItems', 'RasterDpiUsed') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringItems] ADD [RasterDpiUsed] int NULL;
    IF COL_LENGTH('talentmatch.BulkScoringItems', 'MinimumPageSsim') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringItems] ADD [MinimumPageSsim] decimal(6,5) NULL;
    IF COL_LENGTH('talentmatch.BulkScoringItems', 'RasterFidelityValidatedAt') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringItems] ADD [RasterFidelityValidatedAt] datetime2 NULL;
    IF COL_LENGTH('talentmatch.BulkScoringItems', 'ScoringConfigVersionId') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringItems] ADD [ScoringConfigVersionId] nvarchar(36) NULL;
END;
GO

IF OBJECT_ID(N'[talentmatch].[BulkScoringItems]', N'U') IS NOT NULL
BEGIN
    UPDATE [talentmatch].[BulkScoringItems]
    SET [ConvertedPageCount] = CASE WHEN [ScoredVersion] = N'converted' THEN [OriginalPageCount] ELSE [ConvertedPageCount] END;

    ALTER TABLE [talentmatch].[BulkScoringItems] ADD CONSTRAINT [CK_BulkScoringItems_Reason]
        CHECK (ReasonCode IS NULL OR ReasonCode IN ('unsupported_type', 'file_too_large', 'limit_exceeded', 'empty_file', 'not_convertible', 'access_denied', 'authorization_lapsed', 'source_missing', 'source_changed', 'duplicate_in_request', 'duplicate_in_job', 'conversion_failed', 'conversion_timeout', 'converted_type_invalid', 'converted_magic_invalid', 'converted_document_invalid', 'converted_too_large', 'conversion_page_mismatch', 'conversion_page_invalid', 'conversion_dimension_mismatch', 'conversion_ssim_below_threshold', 'conversion_blank_page', 'conversion_cropped_page', 'scoring_failed', 'cancelled_by_user', 'transient_exhausted'));
END;
GO

IF OBJECT_ID(N'[talentmatch].[BulkScoringItemContents]', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'[talentmatch].[CK_BulkScoringItemContents_Kind]', N'C') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkScoringItemContents] DROP CONSTRAINT [CK_BulkScoringItemContents_Kind];
    IF COL_LENGTH('talentmatch.BulkScoringItemContents', 'DeletionRequestedAt') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringItemContents] ADD [DeletionRequestedAt] datetime2 NULL;
    IF COL_LENGTH('talentmatch.BulkScoringItemContents', 'DeletedAt') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringItemContents] ADD [DeletedAt] datetime2 NULL;
    IF COL_LENGTH('talentmatch.BulkScoringItemContents', 'CleanupReasonCode') IS NULL
        ALTER TABLE [talentmatch].[BulkScoringItemContents] ADD [CleanupReasonCode] nvarchar(max) NULL;
    ALTER TABLE [talentmatch].[BulkScoringItemContents] ADD CONSTRAINT [CK_BulkScoringItemContents_Kind]
        CHECK (Kind IN ('original', 'converted', 'transient'));
END;
GO

IF OBJECT_ID(N'[talentmatch].[BulkRequestAuthorizations]', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'[talentmatch].[CK_BulkRequestAuthorizations_Status]', N'C') IS NOT NULL
        ALTER TABLE [talentmatch].[BulkRequestAuthorizations] DROP CONSTRAINT [CK_BulkRequestAuthorizations_Status];
    IF COL_LENGTH('talentmatch.BulkRequestAuthorizations', 'ProductActorId') IS NULL
       AND COL_LENGTH('talentmatch.BulkRequestAuthorizations', 'RecruiterActorId') IS NOT NULL
        EXEC sp_rename N'[talentmatch].[BulkRequestAuthorizations].[RecruiterActorId]', N'ProductActorId', 'COLUMN';
    IF COL_LENGTH('talentmatch.BulkRequestAuthorizations', 'SourceTenantId') IS NULL
        ALTER TABLE [talentmatch].[BulkRequestAuthorizations] ADD [SourceTenantId] nvarchar(64) NOT NULL
            CONSTRAINT [DF_BulkRequestAuthorizations_SourceTenantId] DEFAULT N'';
    IF COL_LENGTH('talentmatch.BulkRequestAuthorizations', 'SourceAccountBindingHash') IS NULL
        ALTER TABLE [talentmatch].[BulkRequestAuthorizations] ADD [SourceAccountBindingHash] nvarchar(128) NOT NULL
            CONSTRAINT [DF_BulkRequestAuthorizations_SourceAccountBindingHash] DEFAULT N'';
    IF COL_LENGTH('talentmatch.BulkRequestAuthorizations', 'SourceDriveId') IS NULL
        ALTER TABLE [talentmatch].[BulkRequestAuthorizations] ADD [SourceDriveId] nvarchar(200) NOT NULL
            CONSTRAINT [DF_BulkRequestAuthorizations_SourceDriveId] DEFAULT N'';
    IF COL_LENGTH('talentmatch.BulkRequestAuthorizations', 'SourceFolderItemId') IS NULL
        ALTER TABLE [talentmatch].[BulkRequestAuthorizations] ADD [SourceFolderItemId] nvarchar(200) NOT NULL
            CONSTRAINT [DF_BulkRequestAuthorizations_SourceFolderItemId] DEFAULT N'';
    ALTER TABLE [talentmatch].[BulkRequestAuthorizations] ADD CONSTRAINT [CK_BulkRequestAuthorizations_Status]
        CHECK (Status IN ('active', 'lapsed', 'revoked', 'accessLost', 'discarded', 'unavailableHistorical'));
END;
GO

IF OBJECT_ID(N'[talentmatch].[BulkAuditEvents]', N'U') IS NULL
CREATE TABLE [talentmatch].[BulkAuditEvents] (
    [AuditEventId] nvarchar(36) NOT NULL,
    [EventType] nvarchar(100) NOT NULL,
    [CorrelationId] nvarchar(36) NOT NULL,
    [ActorType] nvarchar(20) NOT NULL,
    [ActorId] nvarchar(128) NOT NULL,
    [InitiatedByActorId] nvarchar(128) NULL,
    [OccurredAt] datetime2 NOT NULL,
    [SubjectType] nvarchar(100) NOT NULL,
    [SubjectId] nvarchar(128) NOT NULL,
    [SafeFactsJson] nvarchar(max) NOT NULL,
    [SchemaVersion] int NOT NULL,
    [Boundary] nvarchar(20) NOT NULL,
    CONSTRAINT [PK_BulkAuditEvents] PRIMARY KEY ([AuditEventId]),
    CONSTRAINT [CK_BulkAuditEvents_ActorType] CHECK (ActorType IN ('user', 'service')),
    CONSTRAINT [CK_BulkAuditEvents_Boundary] CHECK (Boundary IN ('transactional', 'durable_outbox', 'reconciliation')),
    CONSTRAINT [CK_BulkAuditEvents_SchemaVersion] CHECK (SchemaVersion > 0)
);
GO

IF OBJECT_ID(N'[talentmatch].[BulkSourceAdmissionEvents]', N'U') IS NULL
CREATE TABLE [talentmatch].[BulkSourceAdmissionEvents] (
    [Id] nvarchar(36) NOT NULL,
    [Kind] nvarchar(32) NOT NULL,
    [SubjectHash] nvarchar(128) NOT NULL,
    [OccurredAt] datetime2 NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    CONSTRAINT [PK_BulkSourceAdmissionEvents] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_BulkSourceAdmissionEvents_Expiry] CHECK (ExpiresAt > OccurredAt),
    CONSTRAINT [CK_BulkSourceAdmissionEvents_Kind] CHECK (Kind IN ('connection_actor', 'connection_ip', 'callback_ip'))
);
GO

IF OBJECT_ID(N'[talentmatch].[BulkSourceConnections]', N'U') IS NULL
CREATE TABLE [talentmatch].[BulkSourceConnections] (
    [Id] nvarchar(36) NOT NULL,
    [ProductActorId] nvarchar(128) NOT NULL,
    [JobId] nvarchar(36) NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [CanonicalUrlHash] nvarchar(64) NOT NULL,
    [PendingFolderUrlEncrypted] varbinary(max) NULL,
    [StateHash] nvarchar(128) NULL,
    [PkceVerifierEncrypted] varbinary(max) NULL,
    [RedirectAfterPath] nvarchar(2048) NOT NULL,
    [SourceTenantId] nvarchar(64) NULL,
    [SourceAccountBindingHash] nvarchar(128) NULL,
    [SourceAccountDisplay] nvarchar(256) NULL,
    [SourceKind] nvarchar(20) NULL,
    [SourceCloud] nvarchar(20) NULL,
    [SourceDriveId] nvarchar(200) NULL,
    [SourceFolderItemId] nvarchar(200) NULL,
    [SourceFolderName] nvarchar(500) NULL,
    [SourceFolderPath] nvarchar(2000) NULL,
    [SourceWebUrl] nvarchar(2048) NULL,
    [ProtectedPayload] varbinary(max) NULL,
    [WrappedDataKey] varbinary(max) NULL,
    [KeyVersion] nvarchar(200) NULL,
    [ObtainedAt] datetime2 NOT NULL,
    [LastActivityAt] datetime2 NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    [ConsumedByRequestId] nvarchar(36) NULL,
    [DiscardedAt] datetime2 NULL,
    [FailureCode] nvarchar(100) NULL,
    [ConcurrencyVersion] int NOT NULL,
    CONSTRAINT [PK_BulkSourceConnections] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_BulkSourceConnections_Consumed] CHECK ((Status <> 'consumed') OR (ConsumedByRequestId IS NOT NULL AND PendingFolderUrlEncrypted IS NULL AND StateHash IS NULL AND PkceVerifierEncrypted IS NULL AND ProtectedPayload IS NULL AND WrappedDataKey IS NULL)),
    CONSTRAINT [CK_BulkSourceConnections_Expiry] CHECK (ExpiresAt >= LastActivityAt AND ExpiresAt >= ObtainedAt),
    CONSTRAINT [CK_BulkSourceConnections_SourceKind] CHECK (SourceKind IS NULL OR SourceKind IN ('sharepoint', 'onedrive_business')),
    CONSTRAINT [CK_BulkSourceConnections_Status] CHECK (Status IN ('authorizing', 'authorized', 'verified', 'consumed', 'expired', 'abandoned', 'failed')),
    CONSTRAINT [FK_BulkSourceConnections_Jobs_JobId] FOREIGN KEY ([JobId]) REFERENCES [talentmatch].[Jobs] ([Id]) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkAuditEvents_CorrelationId' AND object_id = OBJECT_ID('talentmatch.BulkAuditEvents'))
    CREATE INDEX [IX_BulkAuditEvents_CorrelationId] ON [talentmatch].[BulkAuditEvents] ([CorrelationId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkAuditEvents_SubjectType_SubjectId_OccurredAt' AND object_id = OBJECT_ID('talentmatch.BulkAuditEvents'))
    CREATE INDEX [IX_BulkAuditEvents_SubjectType_SubjectId_OccurredAt] ON [talentmatch].[BulkAuditEvents] ([SubjectType], [SubjectId], [OccurredAt]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkAuditEvents_SubjectId_EventType' AND object_id = OBJECT_ID('talentmatch.BulkAuditEvents'))
    CREATE INDEX [IX_BulkAuditEvents_SubjectId_EventType] ON [talentmatch].[BulkAuditEvents] ([SubjectId], [EventType]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkScoringRequests_ConnectionId' AND object_id = OBJECT_ID('talentmatch.BulkScoringRequests'))
    CREATE UNIQUE INDEX [IX_BulkScoringRequests_ConnectionId] ON [talentmatch].[BulkScoringRequests] ([ConnectionId]) WHERE [ConnectionId] IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkScoringRequests_Status_TerminalAt' AND object_id = OBJECT_ID('talentmatch.BulkScoringRequests'))
    CREATE INDEX [IX_BulkScoringRequests_Status_TerminalAt] ON [talentmatch].[BulkScoringRequests] ([Status], [TerminalAt]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkSourceAdmissionEvents_ExpiresAt' AND object_id = OBJECT_ID('talentmatch.BulkSourceAdmissionEvents'))
    CREATE INDEX [IX_BulkSourceAdmissionEvents_ExpiresAt] ON [talentmatch].[BulkSourceAdmissionEvents] ([ExpiresAt]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkSourceAdmissionEvents_Kind_SubjectHash_ExpiresAt' AND object_id = OBJECT_ID('talentmatch.BulkSourceAdmissionEvents'))
    CREATE INDEX [IX_BulkSourceAdmissionEvents_Kind_SubjectHash_ExpiresAt] ON [talentmatch].[BulkSourceAdmissionEvents] ([Kind], [SubjectHash], [ExpiresAt]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkSourceConnections_JobId' AND object_id = OBJECT_ID('talentmatch.BulkSourceConnections'))
    CREATE INDEX [IX_BulkSourceConnections_JobId] ON [talentmatch].[BulkSourceConnections] ([JobId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkSourceConnections_ProductActorId_JobId_Status_ExpiresAt' AND object_id = OBJECT_ID('talentmatch.BulkSourceConnections'))
    CREATE INDEX [IX_BulkSourceConnections_ProductActorId_JobId_Status_ExpiresAt] ON [talentmatch].[BulkSourceConnections] ([ProductActorId], [JobId], [Status], [ExpiresAt]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BulkSourceConnections_StateHash' AND object_id = OBJECT_ID('talentmatch.BulkSourceConnections'))
    CREATE UNIQUE INDEX [IX_BulkSourceConnections_StateHash] ON [talentmatch].[BulkSourceConnections] ([StateHash]) WHERE [StateHash] IS NOT NULL;
GO
