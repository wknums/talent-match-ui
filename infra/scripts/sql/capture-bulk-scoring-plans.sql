-- Run against the intended application database with VIEW DATABASE STATE.
-- Catalog reads only: no workload replay, Query Store changes, plan forcing, or DDL.
-- Plans can contain compiled parameter values; keep exports out of source control.
SET NOCOUNT ON;

DECLARE @CapturedAt datetimeoffset = SYSUTCDATETIME();
DECLARE @Since datetimeoffset = DATEADD(day, -7, @CapturedAt);

SELECT DB_NAME() AS database_name, @CapturedAt AS captured_at_utc,
    @Since AS runtime_window_start_utc, actual_state_desc, desired_state_desc,
    readonly_reason, query_capture_mode_desc, wait_stats_capture_mode_desc, current_storage_size_mb,
    max_storage_size_mb
FROM sys.database_query_store_options;

;WITH ClassifiedQueries AS
(
    SELECT q.query_id, q.last_execution_time, qt.query_sql_text,
        CASE
            WHEN qt.query_sql_text LIKE N'%AS [[]ApplicationId]%'
                AND qt.query_sql_text LIKE N'%[[]ScoringPrompts]%'
                AND qt.query_sql_text LIKE N'%[[]ScoringOwner] IS NULL%'
                THEN N'claim-candidates'
            WHEN qt.query_sql_text LIKE N'%UPDATE %'
                AND qt.query_sql_text LIKE N'%[[]ScoringPrompts]%'
                AND qt.query_sql_text LIKE N'%[[]ScoringOwner] IS NULL%'
                THEN N'claim-write'
            WHEN qt.query_sql_text LIKE N'%UPDATE %'
                AND qt.query_sql_text LIKE N'%[[]ScoringLeaseUntil] = CASE%'
                AND qt.query_sql_text LIKE N'%[[]ScoringOwner]%'
                THEN N'lease-renewal'
            WHEN qt.query_sql_text LIKE N'%UPDATE %'
                AND qt.query_sql_text LIKE N'%[[]FinalScore]%'
                AND qt.query_sql_text LIKE N'%[[]ScoringOwner]%'
                THEN N'finalization-application'
            WHEN qt.query_sql_text LIKE N'%UPDATE %'
                AND qt.query_sql_text LIKE N'%[[]ScoringOwner] = NULL%'
                AND qt.query_sql_text LIKE N'%[[]Status] <>%'
                THEN N'finalization-legacy-release'
            WHEN qt.query_sql_text LIKE N'%UPDATE %'
                AND qt.query_sql_text LIKE N'%[[]ScoringOwner] = @%'
                AND qt.query_sql_text LIKE N'%[[]ScoringLeaseUntil] >%'
                THEN N'ownership-write-review'
            WHEN qt.query_sql_text LIKE N'%COUNT(CASE%'
                AND qt.query_sql_text LIKE N'%@jobId%'
                AND qt.query_sql_text LIKE N'%NeedsManualReview%'
                THEN N'application-summary'
            WHEN qt.query_sql_text LIKE N'%[[]AggregatedResults]%'
                AND qt.query_sql_text LIKE N'%@applicationId%'
                THEN N'finalization-aggregate-read'
            WHEN qt.query_sql_text LIKE N'%INSERT INTO %'
                AND (qt.query_sql_text LIKE N'%[[]AggregatedResults]%'
                    OR qt.query_sql_text LIKE N'%[[]ScoringRuns]%')
                THEN N'scoring-result-write-review'
            WHEN qt.query_sql_text LIKE N'%UPDATE %'
                AND qt.query_sql_text LIKE N'%[[]AggregatedResults]%'
                THEN N'scoring-result-write-review'
            WHEN qt.query_sql_text LIKE N'%[[]Jobs]%'
                AND qt.query_sql_text LIKE N'%[[]JobConfigVersions]%'
                AND qt.query_sql_text LIKE N'%@id%'
                AND qt.query_sql_text NOT LIKE N'%[[]Applications]%'
                THEN N'summary-job-lookup-review'
        END AS query_family
    FROM sys.query_store_query AS q
    JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
    WHERE q.last_execution_time >= @Since
        AND qt.query_sql_text LIKE N'%[[]talentmatch]%'
        AND qt.query_sql_text NOT LIKE N'%sys.query_store%'
),
Runtime AS
(
    -- Weight interval averages by execution count, including in-memory/disk rows.
    -- Intersecting boundary intervals are included, not prorated.
    SELECT rs.plan_id,
        SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions ELSE 0 END) AS executions,
        SUM(CASE WHEN rs.execution_type = 3 THEN rs.count_executions ELSE 0 END) AS aborted_executions,
        SUM(CASE WHEN rs.execution_type = 4 THEN rs.count_executions ELSE 0 END) AS exception_executions,
        SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions * rs.avg_duration END)
            / NULLIF(SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions ELSE 0 END), 0)
            / 1000.0 AS avg_duration_ms,
        MAX(CASE WHEN rs.execution_type = 0 THEN rs.max_duration END) / 1000.0 AS max_duration_ms,
        SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions * rs.avg_cpu_time END)
            / NULLIF(SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions ELSE 0 END), 0)
            / 1000.0 AS avg_cpu_ms,
        SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions * rs.avg_logical_io_reads END)
            / NULLIF(SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions ELSE 0 END), 0)
            AS avg_logical_reads_pages,
        SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions * rs.avg_logical_io_writes END)
            / NULLIF(SUM(CASE WHEN rs.execution_type = 0 THEN rs.count_executions ELSE 0 END), 0)
            AS avg_logical_writes_pages,
        MAX(rs.last_execution_time) AS runtime_last_execution_utc
    FROM sys.query_store_runtime_stats AS rs
    JOIN sys.query_store_runtime_stats_interval AS i
        ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
    WHERE i.end_time > @Since AND i.start_time <= @CapturedAt
    GROUP BY rs.plan_id
),
Waits AS
(
    SELECT ws.plan_id, ws.wait_category_desc,
        SUM(ws.total_query_wait_time_ms) AS total_wait_ms
    FROM sys.query_store_wait_stats AS ws
    JOIN sys.query_store_runtime_stats_interval AS i
        ON i.runtime_stats_interval_id = ws.runtime_stats_interval_id
    WHERE ws.execution_type = 0
        AND i.end_time > @Since AND i.start_time <= @CapturedAt
    GROUP BY ws.plan_id, ws.wait_category_desc
)
SELECT q.query_family, q.query_id, q.last_execution_time AS query_last_execution_utc,
    p.plan_id, p.last_execution_time AS plan_last_execution_utc,
    p.is_forced_plan, r.executions, r.aborted_executions, r.exception_executions,
    r.avg_duration_ms, r.max_duration_ms, r.avg_cpu_ms,
    r.avg_logical_reads_pages, r.avg_logical_writes_pages,
    r.runtime_last_execution_utc,
    (SELECT w.wait_category_desc, w.total_wait_ms
        FROM Waits AS w
        WHERE w.plan_id = p.plan_id
        ORDER BY w.total_wait_ms DESC
        FOR JSON PATH) AS waits_json,
    q.query_sql_text, p.query_plan
FROM ClassifiedQueries AS q
LEFT JOIN sys.query_store_plan AS p ON p.query_id = q.query_id
LEFT JOIN Runtime AS r ON r.plan_id = p.plan_id
WHERE q.query_family IS NOT NULL
ORDER BY q.query_family, q.query_id, p.plan_id;

SELECT t.name AS table_name, i.name AS index_name, i.type_desc,
    i.is_unique, i.is_disabled, i.filter_definition,
    keys.key_columns, included.include_columns, row_counts.row_count,
    STATS_DATE(i.object_id, i.index_id) AS statistics_updated_at
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.indexes AS i ON i.object_id = t.object_id AND i.index_id > 0
OUTER APPLY
(
    SELECT STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(c.name)
        + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N' ASC' END), N', ')
        WITHIN GROUP (ORDER BY ic.key_ordinal) AS key_columns
    FROM sys.index_columns AS ic
    JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
) AS keys
OUTER APPLY
(
    SELECT STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(c.name)), N', ')
        WITHIN GROUP (ORDER BY ic.index_column_id) AS include_columns
    FROM sys.index_columns AS ic
    JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
) AS included
OUTER APPLY
(
    SELECT SUM(ps.row_count) AS row_count
    FROM sys.dm_db_partition_stats AS ps
    WHERE ps.object_id = t.object_id AND ps.index_id IN (0, 1)
) AS row_counts
WHERE s.name = N'talentmatch'
    AND t.name IN (N'Applications', N'Jobs', N'JobConfigVersions', N'ScoringPrompts',
        N'PromptTestRuns', N'BulkScoringItems', N'ApplicationDocuments',
        N'DocumentBlobs', N'AggregatedResults', N'ScoringRuns')
ORDER BY t.name, i.index_id;
