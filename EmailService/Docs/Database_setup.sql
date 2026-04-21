-- ============================================================
-- EmailService — Database Setup Script
-- Step 1 of N: Create Database + Tables + Sample Data
--
-- Generic email notification service.
-- Works for any system: QMS, Asset Management, HR, etc.
-- The caller decides what goes in Subject + Body.
--
-- Run this entire file in SSMS (F5 or Execute)
-- Target: SQL Server 2019+ (Express / Developer / Production)
-- ============================================================


-- ============================================================
-- SECTION 1: Create & Select Database
-- ============================================================

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'EmailService')
BEGIN
    CREATE DATABASE EmailService;
    PRINT 'Database created: EmailService';
END
ELSE
BEGIN
    PRINT 'Database already exists — skipping CREATE';
END
GO

USE EmailService;
GO


-- ============================================================
-- SECTION 2: Drop Tables (only if re-running from scratch)
--
-- Drop child (EmailRecipient) BEFORE parent (WorkflowLog)
-- because of the FK constraint.
-- ============================================================

IF OBJECT_ID('dbo.EmailRecipient', 'U') IS NOT NULL
    DROP TABLE dbo.EmailRecipient;

IF OBJECT_ID('dbo.WorkflowLog', 'U') IS NOT NULL
    DROP TABLE dbo.WorkflowLog;

PRINT 'Old tables dropped (or skipped if first run)';
GO


-- ============================================================
-- SECTION 3: Create WorkflowLog (parent table)
--
-- WHY Status (NVARCHAR) instead of IsSent (BIT)?
--   IsSent = 0/1 is ambiguous during a crash:
--     → Job sends email, crashes before setting IsSent = 1
--     → Next cycle picks it again → DUPLICATE EMAIL
--   Status = 'Processing' prevents that:
--     → Job marks 'Processing' BEFORE sending (atomic)
--     → Crash leaves status as 'Processing' → skipped next cycle
--     → No duplicate. No data loss.
--
-- WHY RetryCount + ErrorMessage?
--   Ops visibility: "Why did 50 emails fail at 3am?"
--   Alerting: "If RetryCount > 3, page on-call engineer"
--   Debugging: ErrorMessage = exact exception text
--
-- WHY Subject + Body stored in DB (not hardcoded in C#)?
--   Generic reuse: QMS passes its own subject/body.
--                  Asset system passes its own subject/body.
--   No code deploy needed to change email content.
--   Future: can reference a template table from here.
--
-- WHY 3 indexes?
--   IX_Status                → fast "get pending" lookup (most common query)
--   IX_CreatedDate           → date-range monitoring queries
--   IX_Status_CreatedDate    → composite: covers the exact job query:
--       WHERE Status = 'Pending' ORDER BY CreatedDate ASC
--   Composite covers BOTH filter + sort in a single index seek.
-- ============================================================

CREATE TABLE [dbo].[WorkflowLog] (

    -- Primary Key
    [WorkflowLogId]     INT             PRIMARY KEY IDENTITY(1,1),

    -- Which system sent this? (QMS, AssetManagement, HR, etc.)
    -- Optional but useful for filtering/reporting per system.
    [SourceSystem]      NVARCHAR(100)   NOT NULL    DEFAULT 'General',

    -- Email content — fully owned by the caller, not hardcoded here
    [Subject]           NVARCHAR(500)   NOT NULL,
    [Body]              NVARCHAR(MAX)   NOT NULL,

    -- State machine:
    --   Pending     → waiting to be picked by the job
    --   Processing  → job claimed it (atomic lock prevents duplicates)
    --   Sent        → delivered successfully
    --   Failed      → Polly retries exhausted, needs ops attention
    [Status]            NVARCHAR(20)    NOT NULL    DEFAULT 'Pending',

    -- Failure observability
    [RetryCount]        INT             NOT NULL    DEFAULT 0,
    [ErrorMessage]      NVARCHAR(MAX)   NULL,

    -- Audit trail
    [CreatedDate]       DATETIME        NOT NULL    DEFAULT GETDATE(),
    [SentDate]          DATETIME        NULL,
    [LastAttemptDate]   DATETIME        NULL,

    -- Indexes
    INDEX [IX_WorkflowLog_Status]               ([Status]),
    INDEX [IX_WorkflowLog_CreatedDate]          ([CreatedDate]),
    INDEX [IX_WorkflowLog_Status_CreatedDate]   ([Status], [CreatedDate]),
    INDEX [IX_WorkflowLog_SourceSystem]         ([SourceSystem])  -- filter by system
);

PRINT 'Table created: WorkflowLog';
GO


-- ============================================================
-- SECTION 4: Create EmailRecipient (child table)
--
-- WHY a separate table instead of a comma-separated column?
--   "TO: a@x.com, b@x.com" in one column = 1NF violation
--     → impossible to query/index individual addresses
--     → impossible to add per-recipient metadata later
--   Separate table = query by address, filter by type, join cleanly
--
-- WHY RecipientType (TO/CC/BCC)?
--   Email protocols treat them differently:
--     TO  → primary audience, visible to all
--     CC  → informed parties, visible to all
--     BCC → hidden recipients (compliance, audit, management)
--   MailKit (C# library) needs them split to build correct headers.
--
-- WHY ON DELETE CASCADE?
--   If WorkflowLog row is deleted (cleanup job, manual ops),
--   its EmailRecipient rows go with it automatically.
--   Prevents orphaned recipient rows with no parent log.
-- ============================================================

CREATE TABLE [dbo].[EmailRecipient] (

    [EmailRecipientId]  INT             PRIMARY KEY IDENTITY(1,1),

    [WorkflowLogId]     INT             NOT NULL,
    [EmailAddress]      NVARCHAR(255)   NOT NULL,
    [RecipientType]     NVARCHAR(10)    NOT NULL    DEFAULT 'TO',  -- TO | CC | BCC

    [CreatedDate]       DATETIME        NOT NULL    DEFAULT GETDATE(),

    CONSTRAINT [FK_EmailRecipient_WorkflowLog]
        FOREIGN KEY ([WorkflowLogId])
        REFERENCES [dbo].[WorkflowLog] ([WorkflowLogId])
        ON DELETE CASCADE,

    INDEX [IX_EmailRecipient_WorkflowLogId] ([WorkflowLogId]),
    INDEX [IX_EmailRecipient_EmailAddress]  ([EmailAddress])
);

PRINT 'Table created: EmailRecipient';
GO


-- ============================================================
-- SECTION 5: Verify Structure
-- ============================================================

SELECT
    c.TABLE_NAME,
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.IS_NULLABLE,
    c.COLUMN_DEFAULT
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_NAME IN ('WorkflowLog', 'EmailRecipient')
ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION;
GO


-- ============================================================
-- SECTION 6: Insert Sample / Test Data
--
-- Three scenarios across two different source systems
-- to prove the service is genuinely generic:
--
--   QMS-001       QMS             Pending  → job should pick this up
--   ASSET-001     AssetManagement Pending  → different system, same service
--   QMS-002       QMS             Sent     → simulates already-processed
-- ============================================================

-- Scenario 1: QMS — Document Review Notification (Pending)
DECLARE @Id1 INT;

INSERT INTO [dbo].[WorkflowLog] ([SourceSystem], [Subject], [Body], [Status])
VALUES (
    'QMS',
    'QMS Notification: Document Review Required',
    N'<!DOCTYPE html>
<html>
<body style="font-family: Arial, sans-serif; padding: 20px;">
    <h2 style="color: #2c3e50;">QMS Notification</h2>
    <table style="border-collapse: collapse; width: 100%;">
        <tr><td style="padding: 8px; font-weight: bold;">System:</td><td style="padding: 8px;">Quality Management System</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">Action Required:</td><td style="padding: 8px;">Document Review</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">Reference #:</td><td style="padding: 8px;">QMS-DOC-001</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">Assigned To:</td><td style="padding: 8px;">John Doe</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">Due Date:</td><td style="padding: 8px;">2026-04-21</td></tr>
    </table>
    <p style="margin-top: 20px; color: #666; font-size: 12px;">This is an automated message from Email Notification Service</p>
</body>
</html>',
    'Pending'
);
SET @Id1 = SCOPE_IDENTITY();

INSERT INTO [dbo].[EmailRecipient] ([WorkflowLogId], [EmailAddress], [RecipientType])
VALUES
    (@Id1, 'john.doe@company.com',      'TO'),
    (@Id1, 'qms.manager@company.com',   'CC');

PRINT 'Inserted Scenario 1 (QMS, Pending) — WorkflowLogId = ' + CAST(@Id1 AS NVARCHAR);


-- Scenario 2: Asset Management — Transfer Request (Pending)
DECLARE @Id2 INT;

INSERT INTO [dbo].[WorkflowLog] ([SourceSystem], [Subject], [Body], [Status])
VALUES (
    'AssetManagement',
    'Asset Notification: Transfer Request Pending Approval',
    N'<!DOCTYPE html>
<html>
<body style="font-family: Arial, sans-serif; padding: 20px;">
    <h2 style="color: #2c3e50;">Asset Management Notification</h2>
    <table style="border-collapse: collapse; width: 100%;">
        <tr><td style="padding: 8px; font-weight: bold;">System:</td><td style="padding: 8px;">Asset Management</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">Action Required:</td><td style="padding: 8px;">Approve Transfer</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">Reference #:</td><td style="padding: 8px;">ASSET-TRN-001</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">Requested By:</td><td style="padding: 8px;">Jane Smith</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">From Location:</td><td style="padding: 8px;">Warehouse A</td></tr>
        <tr><td style="padding: 8px; font-weight: bold;">To Location:</td><td style="padding: 8px;">Warehouse B</td></tr>
    </table>
    <p style="margin-top: 20px; color: #666; font-size: 12px;">This is an automated message from Email Notification Service</p>
</body>
</html>',
    'Pending'
);
SET @Id2 = SCOPE_IDENTITY();

INSERT INTO [dbo].[EmailRecipient] ([WorkflowLogId], [EmailAddress], [RecipientType])
VALUES
    (@Id2, 'jane.smith@company.com',        'TO'),
    (@Id2, 'warehouse.manager@company.com', 'TO'),
    (@Id2, 'supervisor@company.com',        'CC');

PRINT 'Inserted Scenario 2 (AssetManagement, Pending) — WorkflowLogId = ' + CAST(@Id2 AS NVARCHAR);


-- Scenario 3: QMS — Audit Complete (already Sent, historical record)
DECLARE @Id3 INT;

INSERT INTO [dbo].[WorkflowLog] ([SourceSystem], [Subject], [Body], [Status], [SentDate], [RetryCount])
VALUES (
    'QMS',
    'QMS Notification: Audit Completed Successfully',
    N'<html><body><h2>Audit QMS-AUD-001 completed and signed off.</h2></body></html>',
    'Sent',
    GETDATE(),
    0
);
SET @Id3 = SCOPE_IDENTITY();

INSERT INTO [dbo].[EmailRecipient] ([WorkflowLogId], [EmailAddress], [RecipientType])
VALUES
    (@Id3, 'auditor@company.com', 'TO');

PRINT 'Inserted Scenario 3 (QMS, Sent) — WorkflowLogId = ' + CAST(@Id3 AS NVARCHAR);
GO


-- ============================================================
-- SECTION 7: Verify Inserted Data
-- ============================================================

SELECT
    WorkflowLogId,
    SourceSystem,
    LEFT(Subject, 55)       AS Subject,
    Status,
    RetryCount,
    CreatedDate,
    SentDate
FROM [dbo].[WorkflowLog]
ORDER BY WorkflowLogId;

SELECT
    r.EmailRecipientId,
    r.WorkflowLogId,
    w.SourceSystem,
    r.EmailAddress,
    r.RecipientType,
    w.Status
FROM [dbo].[EmailRecipient]    r
JOIN [dbo].[WorkflowLog]       w ON w.WorkflowLogId = r.WorkflowLogId
ORDER BY r.WorkflowLogId, r.RecipientType;
GO


-- ============================================================
-- SECTION 8: Operations / Monitoring Queries
-- ============================================================

-- What's queued? (all systems)
SELECT WorkflowLogId, SourceSystem, LEFT(Subject,55) AS Subject, Status, CreatedDate
FROM   dbo.WorkflowLog
WHERE  Status = 'Pending'
ORDER  BY CreatedDate ASC;

-- What's queued for a specific system?
SELECT WorkflowLogId, LEFT(Subject,55) AS Subject, Status, CreatedDate
FROM   dbo.WorkflowLog
WHERE  Status = 'Pending' AND SourceSystem = 'QMS'
ORDER  BY CreatedDate ASC;

-- What failed and why?
SELECT WorkflowLogId, SourceSystem, LEFT(Subject,55) AS Subject, ErrorMessage, RetryCount, LastAttemptDate
FROM   dbo.WorkflowLog
WHERE  Status = 'Failed'
ORDER  BY LastAttemptDate DESC;

-- What sent recently?
SELECT WorkflowLogId, SourceSystem, LEFT(Subject,55) AS Subject, CreatedDate, SentDate,
       DATEDIFF(SECOND, CreatedDate, SentDate) AS SecondsToDeliver
FROM   dbo.WorkflowLog
WHERE  Status = 'Sent'
ORDER  BY SentDate DESC;

-- Stuck in Processing >10 min? (service may have crashed)
SELECT WorkflowLogId, SourceSystem, LEFT(Subject,55) AS Subject, LastAttemptDate,
       DATEDIFF(MINUTE, LastAttemptDate, GETDATE()) AS MinutesStuck
FROM   dbo.WorkflowLog
WHERE  Status = 'Processing'
  AND  DATEDIFF(MINUTE, LastAttemptDate, GETDATE()) > 10;

-- Today's summary per system
SELECT
    SourceSystem,
    SUM(CASE WHEN Status = 'Pending'    THEN 1 ELSE 0 END) AS Pending,
    SUM(CASE WHEN Status = 'Processing' THEN 1 ELSE 0 END) AS Processing,
    SUM(CASE WHEN Status = 'Sent'       THEN 1 ELSE 0 END) AS Sent,
    SUM(CASE WHEN Status = 'Failed'     THEN 1 ELSE 0 END) AS Failed
FROM dbo.WorkflowLog
WHERE CreatedDate >= CAST(GETDATE() AS DATE)
GROUP BY SourceSystem;
GO

PRINT '=== Setup complete. EmailService database is ready. ===';
GO
