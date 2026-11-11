IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'talentmatch.BulkAuditEvents')
      AND name = N'IX_BulkAuditEvents_SubjectId_EventType'
)
    CREATE INDEX [IX_BulkAuditEvents_SubjectId_EventType]
        ON [talentmatch].[BulkAuditEvents] ([SubjectId], [EventType]);
