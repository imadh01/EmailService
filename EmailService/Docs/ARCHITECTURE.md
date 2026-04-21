# EmailService — Architecture Document

## Overview

EmailService is a **generic, system-agnostic email notification Windows Service**.
Any internal system (QMS, Asset Management, HR, Finance etc.) can trigger an
automated email by inserting a row into the database. The service polls every
5 minutes, picks up pending rows, sends the emails, and updates the status.

No code change is required in EmailService when a new system wants to use it.
The caller just inserts a row.

---

## System Context

```
┌─────────────────────────────────────────────────────────┐
│                    Caller Systems                        │
│                                                         │
│   ┌──────────┐   ┌──────────────┐   ┌──────────────┐   │
│   │   QMS    │   │    Asset     │   │     HR       │   │
│   │          │   │  Management  │   │   System     │   │
│   └────┬─────┘   └──────┬───────┘   └──────┬───────┘   │
│        │                │                  │            │
└────────┼────────────────┼──────────────────┼────────────┘
         │                │                  │
         ▼                ▼                  ▼
┌─────────────────────────────────────────────────────────┐
│                  SQL Server Database                     │
│                                                         │
│   WorkflowLog       ←── INSERT (Status = 'Pending')     │
│   EmailRecipient    ←── INSERT                          │
└──────────────────────────┬──────────────────────────────┘
                           │ polls every 5 min
                           ▼
┌─────────────────────────────────────────────────────────┐
│                    EmailService                          │
│                                                         │
│   Quartz Scheduler → WorkflowEmailJob                   │
│       → EmailRepository (reads/writes DB)               │
│       → EmailService (sends via SMTP)                   │
│                                                         │
└──────────────────────────┬──────────────────────────────┘
                           │
                           ▼
                    SMTP Server
                 (smtp.gmail.com:587)
                           │
                           ▼
                  Recipient's Inbox
```

---

## Internal Architecture

### Layer Diagram

```
┌──────────────────────────────────────────┐
│              Jobs Layer                  │
│         WorkflowEmailJob                 │  ← orchestrates the pipeline
│   depends on interfaces, not concrete    │
└────────────┬──────────────┬─────────────┘
             │              │
             ▼              ▼
┌────────────────┐  ┌───────────────────┐
│   IEmail       │  │   IEmail          │
│   Repository   │  │   Service         │
└────────┬───────┘  └────────┬──────────┘
         │                   │
         ▼                   ▼
┌────────────────┐  ┌───────────────────┐
│  Email         │  │   EmailService    │
│  Repository    │  │   (MailKit+Polly) │
│  (EF Core)     │  │                  │
└────────┬───────┘  └────────┬──────────┘
         │                   │
         ▼                   ▼
   SQL Server            SMTP Server
```

### Dependency Rule
```
Jobs/       → depends on → Services/Interfaces/
Data/       → implements → Services/Interfaces/
Services/   → implements → Services/Interfaces/

Jobs/ never imports from Data/ directly.
Data/ never imports from Jobs/ directly.
Dependencies only flow INWARD toward interfaces.
```

---

## Request Flow — Step by Step

```
1. TRIGGER
   Quartz fires WorkflowEmailJob every 5 minutes (cron: 0 */5 * * * ?)

2. SCOPE CREATION
   Job creates a DI scope → resolves fresh DbContext, Repository, EmailService

3. ATOMIC LOCK
   Repository opens a DB transaction
   SELECT TOP (50) WHERE Status = 'Pending' ORDER BY CreatedDate ASC
   UPDATE those rows SET Status = 'Processing', LastAttemptDate = NOW
   COMMIT → rows are locked, no other worker can claim them

4. FOR EACH EMAIL IN BATCH
   a. GET RECIPIENTS
      SELECT EmailAddress FROM EmailRecipient WHERE WorkflowLogId = @id

   b. SEND EMAIL (with Polly retry)
      Connect to SMTP → Authenticate → Send
      If fails → wait exponential backoff → retry up to 3 times

   c. UPDATE STATUS
      Success → UPDATE WorkflowLog SET Status='Sent', SentDate=NOW
      Failure → UPDATE WorkflowLog SET Status='Failed',
                ErrorMessage=ex.Message, RetryCount=N

5. LOG SUMMARY
   [INF] WorkflowEmailJob done — Sent=48 Failed=2 Duration=1423ms

6. SCOPE DISPOSED
   DbContext disposed → DB connection returned to pool
```

---

## Status State Machine

```
                    ┌─────────┐
                    │ Pending │  ← set by caller on INSERT
                    └────┬────┘
                         │ job picks up (atomic UPDATE)
                         ▼
                  ┌─────────────┐
                  │ Processing  │  ← locked, no other worker can claim
                  └──────┬──────┘
                         │
               ┌─────────┴──────────┐
               │                    │
    SMTP succeeds              Polly retries exhausted
               │                    │
               ▼                    ▼
           ┌──────┐           ┌────────┐
           │ Sent │           │ Failed │
           └──────┘           └────────┘
        + SentDate          + ErrorMessage
                            + RetryCount
                            + LastAttemptDate
```

### Why Status string over IsSent bit

```
IsSent (bit) scenario — BROKEN:
  Job sends email → crashes before UPDATE
  Next cycle: IsSent = 0 → picks same email → DUPLICATE SEND ❌

Status scenario — SAFE:
  Job sets 'Processing' BEFORE sending (atomic)
  Crash: row stays 'Processing' → skipped next cycle ✅
  No duplicate. No data loss.
```

---

## Concurrency Safety — Two Layers

### Layer 1: [DisallowConcurrentExecution]
```
Cycle 1 starts 00:00 → still running at 00:05
Cycle 2 trigger fires → Quartz sees Cycle 1 running → SKIPS
Cycle 1 finishes → Cycle 3 starts at 00:10
```

### Layer 2: Atomic DB Transaction
```
Worker A: BEGIN TRAN → SELECT + lock rows → UPDATE Processing → COMMIT
Worker B: BEGIN TRAN → tries to SELECT same rows → BLOCKED
Worker A commits → Worker B sees 0 Pending rows → exits cleanly
```

Together: each email is processed **exactly once**, even under failure.

---

## Retry Strategy (Polly)

```
MaxRetryAttempts = 3
RetryDelayMilliseconds = 100 (base)

Attempt 1 → SMTP fails → wait 200ms  (100 * 2^1)
Attempt 2 → SMTP fails → wait 400ms  (100 * 2^2)
Attempt 3 → SMTP fails → wait 800ms  (100 * 2^3)
Attempt 4 → SMTP fails → give up
  → Status = 'Failed'
  → ErrorMessage = last exception message
  → RetryCount = 3
```

Exponential backoff gives the SMTP server time to recover between
attempts instead of hammering it repeatedly.

---

## Database Schema

### WorkflowLog

```sql
WorkflowLogId     INT           PK, IDENTITY
SourceSystem      NVARCHAR(100) e.g. 'QMS', 'AssetManagement'
Subject           NVARCHAR(500) email subject
Body              NVARCHAR(MAX) email body (HTML or plain text)
Status            NVARCHAR(20)  Pending / Processing / Sent / Failed
RetryCount        INT           Polly retry count
ErrorMessage      NVARCHAR(MAX) last exception message if Failed
CreatedDate       DATETIME      when row was inserted
SentDate          DATETIME NULL when email was delivered
LastAttemptDate   DATETIME NULL when last attempt was made

Indexes:
  IX_WorkflowLog_Status
  IX_WorkflowLog_CreatedDate
  IX_WorkflowLog_Status_CreatedDate  ← composite, covers the job query
  IX_WorkflowLog_SourceSystem
```

### EmailRecipient

```sql
EmailRecipientId  INT           PK, IDENTITY
WorkflowLogId     INT           FK → WorkflowLog (CASCADE DELETE)
EmailAddress      NVARCHAR(255)
RecipientType     NVARCHAR(10)  TO / CC / BCC
CreatedDate       DATETIME

Indexes:
  IX_EmailRecipient_WorkflowLogId
  IX_EmailRecipient_EmailAddress
```

---

## Key Design Decisions

### 1. Interface-first design
Every component depends on an interface, not a concrete class.
`WorkflowEmailJob` depends on `IEmailRepository` and `IEmailService`.
Swapping MailKit for SendGrid = create `SendGridEmailService : IEmailService`,
change one line in `Program.cs`. Nothing else changes.

### 2. Entity vs DTO boundary
`WorkflowLog` entity never leaves the `Data/` layer.
`PendingEmail` DTO crosses into `Jobs/` layer.
Job never imports EF Core. Clean separation of concerns.

### 3. IServiceProvider in Job
Quartz creates `WorkflowEmailJob` as a singleton.
`DbContext` is scoped. Injecting scoped into singleton = captive dependency.
Fix: inject `IServiceProvider`, create a scope manually per execution.
Scope disposed at end of `Execute()` → `DbContext` disposed cleanly.

### 4. Subject + Body stored in DB
Content is NOT hardcoded in C#. Any caller inserts its own content.
Different systems send completely different emails with zero code changes.

### 5. SourceSystem column
Makes the service genuinely generic and queryable per system.
Ops can ask: "How many QMS emails failed this week?"

---

## Configuration

### appsettings.json sections

```
ConnectionStrings   → SQL Server connection
EmailSettings       → SMTP server credentials and behaviour
SchedulingSettings  → Quartz cron expression and batch size
Serilog             → log level and output sinks
```

See `appsettings.example.json` for a full template.

---

## Deployment

### Development
```bash
dotnet run
# EnableEmails: false → dry-run, no real emails sent
```

### Production (Windows Service)
```powershell
dotnet publish -c Release -o C:\Services\EmailService

New-Service -Name "EmailService" `
    -BinaryPathName "C:\Services\EmailService\EmailService.exe" `
    -StartupType Automatic

Start-Service -Name "EmailService"
```

---

## Future Improvements

```
⬜ Unit tests (xUnit + Moq)
⬜ Integration tests (real DB)
⬜ Docker support
⬜ GitHub Actions CI/CD pipeline
⬜ Email template table (store HTML templates separately)
⬜ Dead-letter queue (retry Failed emails after N hours)
⬜ Metrics endpoint (Prometheus / health checks)
⬜ SendGrid / SMTP provider swap example
```
