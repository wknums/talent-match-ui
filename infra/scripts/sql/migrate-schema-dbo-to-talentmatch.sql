-- ============================================================
-- Idempotent Migration: dbo → talentmatch schema
-- Transfers all 15 TalentMatch application tables from [dbo]
-- to [talentmatch]. Safe to re-run — skips tables already in
-- the target schema.
-- ============================================================

SET NOCOUNT ON;

-- Step 1: Create the talentmatch schema if it does not exist
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch')
BEGIN
    EXEC('CREATE SCHEMA [talentmatch]');
    PRINT '[SCHEMA] Created schema [talentmatch]';
END
ELSE
BEGIN
    PRINT '[SCHEMA] Schema [talentmatch] already exists — skipped';
END

-- Step 2: Transfer each table from [dbo] to [talentmatch]
DECLARE @transferred INT = 0;
DECLARE @skipped     INT = 0;
DECLARE @failed      INT = 0;

DECLARE @tables TABLE (TableName NVARCHAR(128));
INSERT INTO @tables (TableName) VALUES
    ('Users'),
    ('PasswordResetRequests'),
    ('Jobs'),
    ('JobConfigVersions'),
    ('Applications'),
    ('ApplicationDocuments'),
    ('DocumentBlobs'),
    ('ExtractionArtifacts'),
    ('ScoringRuns'),
    ('AggregatedResults'),
    ('ManualReviews'),
    ('ScoringPrompts'),
    ('PromptTestRuns'),
    ('FailureQueueItems'),
    ('ProcessingEvents');

DECLARE @tbl NVARCHAR(128);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT TableName FROM @tables;

OPEN cur;
FETCH NEXT FROM cur INTO @tbl;

WHILE @@FETCH_STATUS = 0
BEGIN
    -- Check if the table exists in dbo
    IF EXISTS (
        SELECT 1 FROM sys.tables
        WHERE name = @tbl AND schema_id = SCHEMA_ID('dbo')
    )
    BEGIN
        -- Check it's not already in talentmatch
        IF NOT EXISTS (
            SELECT 1 FROM sys.tables
            WHERE name = @tbl AND schema_id = SCHEMA_ID('talentmatch')
        )
        BEGIN
            BEGIN TRY
                DECLARE @sql NVARCHAR(500) = 'ALTER SCHEMA [talentmatch] TRANSFER [dbo].[' + @tbl + ']';
                EXEC sp_executesql @sql;
                SET @transferred = @transferred + 1;
                PRINT '[TRANSFERRED] ' + @tbl;
            END TRY
            BEGIN CATCH
                SET @failed = @failed + 1;
                PRINT '[FAILED] ' + @tbl + ' — ' + ERROR_MESSAGE();
            END CATCH
        END
        ELSE
        BEGIN
            SET @skipped = @skipped + 1;
            PRINT '[SKIPPED] ' + @tbl + ' — already in [talentmatch]';
        END
    END
    ELSE
    BEGIN
        -- Table might already be in talentmatch, or might not exist at all
        IF EXISTS (
            SELECT 1 FROM sys.tables
            WHERE name = @tbl AND schema_id = SCHEMA_ID('talentmatch')
        )
        BEGIN
            SET @skipped = @skipped + 1;
            PRINT '[SKIPPED] ' + @tbl + ' — already in [talentmatch]';
        END
        ELSE
        BEGIN
            SET @skipped = @skipped + 1;
            PRINT '[SKIPPED] ' + @tbl + ' — not found in [dbo] or [talentmatch]';
        END
    END

    FETCH NEXT FROM cur INTO @tbl;
END

CLOSE cur;
DEALLOCATE cur;

-- Step 3: Summary
PRINT '';
PRINT '========================================';
PRINT 'Migration Summary';
PRINT '  Transferred: ' + CAST(@transferred AS NVARCHAR(10));
PRINT '  Skipped:     ' + CAST(@skipped AS NVARCHAR(10));
PRINT '  Failed:      ' + CAST(@failed AS NVARCHAR(10));
PRINT '========================================';
