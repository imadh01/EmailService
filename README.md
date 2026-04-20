# 📧 Email Notification Service

A scalable and production-ready background email processing service built with **.NET 8**, designed to handle email workflows across multiple systems (QMS, Asset Management, HR, etc.).

---

## 🚀 Overview

This service processes email requests stored in a database and sends them using SMTP. It is designed as a **generic, reusable infrastructure service** that can be used by multiple applications.

```
Multiple Systems (QMS, Asset, HR)
            │
            ▼
   WorkflowLog (Database)
            │
            ▼
   Background Job (Quartz)
            │
            ▼
     EmailService (MailKit + Polly)
            │
            ▼
        SMTP Server
```

---

## ✨ Features

* ✅ Generic email processing (multi-system support via `SourceSystem`)
* ✅ Background job scheduling using Quartz (every 5 minutes)
* ✅ Retry mechanism with Polly (exponential backoff)
* ✅ HTML email support
* ✅ Clean architecture (Repository + Service + Interface pattern)
* ✅ Configurable via `appsettings.json`
* ✅ Structured logging with Serilog (Console + rolling file)
* ✅ Idempotent processing using `Status` state machine
* ✅ Batch processing support
* ✅ Race condition protection via atomic DB transaction
* ✅ Windows Service deployment ready

---

## 🗄️ Database Design

### WorkflowLog

Stores email requests. One row = one email to send.

| Column | Type | Description |
| --- | --- | --- |
| WorkflowLogId | INT (PK) | Unique identifier |
| SourceSystem | NVARCHAR(100) | Caller system (QMS, Asset, HR...) |
| Subject | NVARCHAR(500) | Email subject |
| Body | NVARCHAR(MAX) | Email content (HTML supported) |
| Status | NVARCHAR(20) | Pending / Processing / Sent / Failed |
| RetryCount | INT | Number of Polly retries |
| ErrorMessage | NVARCHAR(MAX) | Failure details (visible in SSMS) |
| CreatedDate | DATETIME | When row was inserted |
| SentDate | DATETIME | When email was delivered |
| LastAttemptDate | DATETIME | When last attempt was made |

### EmailRecipient

Stores recipients. One workflow can have many recipients.

| Column | Type | Description |
| --- | --- | --- |
| EmailRecipientId | INT (PK) | Unique identifier |
| WorkflowLogId | INT (FK) | Links to WorkflowLog |
| EmailAddress | NVARCHAR(255) | Recipient email address |
| RecipientType | NVARCHAR(10) | TO / CC / BCC |

---

## 🧠 Core Concepts

### 🔹 Status-Based Processing

```
Pending     → Ready to be picked up by the job
Processing  → Locked by job (prevents duplicate sends on crash)
Sent        → Delivered successfully
Failed      → Polly retries exhausted — needs ops attention
```

**Why Status string over IsSent bit?**
If the job sends an email and crashes before updating the DB,
`IsSent = 0` → same email sent again next cycle → **duplicate**.
`Status = 'Processing'` → row stays locked → skipped next cycle → **safe**.

---

### 🔹 Atomic Batch Lock

Before sending, the job marks rows `Processing` in a single DB transaction:

```
SELECT WHERE Status = 'Pending'  ┐
UPDATE SET Status = 'Processing' ┘ one atomic operation

→ No two workers can claim the same row
→ Crash leaves row as 'Processing' — no duplicate send
```

---

### 🔹 Retry Mechanism (Polly)

* Handles transient SMTP / network failures
* Uses exponential backoff:

```
Attempt 1 fails → wait 200ms
Attempt 2 fails → wait 400ms
Attempt 3 fails → wait 800ms
Attempt 4 fails → Status = 'Failed', ErrorMessage stored in DB
```

---

### 🔹 HTML Email Support

Emails are stored as HTML in the `Body` column and rendered by the
recipient's email client for rich formatting.

---

## ⚙️ Configuration

Copy `appsettings.example.json` → rename to `appsettings.json` → fill in your values.

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=localhost;Database=EmailService;Trusted_Connection=true;TrustServerCertificate=true;"
},
"EmailSettings": {
  "EmailHost": "smtp.gmail.com",
  "EmailPort": 587,
  "EmailFrom": "your-email@gmail.com",
  "EmailUserName": "your-email@gmail.com",
  "EmailPassword": "your-16-char-app-password",
  "EnableSsl": true,
  "DefaultCredentials": false,
  "EnableEmails": false,
  "MaxRetryAttempts": 3,
  "RetryDelayMilliseconds": 100
},
"SchedulingSettings": {
  "WorkflowEmail": {
    "Enabled": true,
    "CronExpression": "0 */5 * * * ?",
    "BatchSize": 50
  }
}
```

### Configuration Reference

| Key | Example | Purpose |
|---|---|---|
| EmailHost | smtp.gmail.com | SMTP server |
| EmailPort | 587 | 587=TLS, 25=plain |
| EmailFrom | noreply@company.com | Address shown in inbox |
| EmailUserName | you@gmail.com | SMTP login credential |
| EmailPassword | app-password | SMTP password |
| EnableSsl | true | Use TLS encryption |
| EnableEmails | false | false=dry-run, true=real sends |
| MaxRetryAttempts | 3 | Polly retry count |
| RetryDelayMilliseconds | 100 | Base backoff delay (ms) |
| CronExpression | 0 */5 * * * ? | Every 5 minutes |
| BatchSize | 50 | Max emails per cycle |

> ⚠️ Never commit real credentials to Git.
> Use `appsettings.example.json` as a safe template.
> For Gmail, generate an **App Password** — not your regular Gmail password:
> `Google Account → Security → 2-Step Verification → App Passwords → Generate`

---

## 🛠️ Getting Started

### 1. Prerequisites

```
✅ .NET 8 SDK
✅ SQL Server 2019+ (Express, Developer, or Production)
✅ Visual Studio 2022 or VS Code
✅ Git
✅ Gmail account with App Password
```

### 2. Clone the repository

```bash
git clone <your-repo-url>
cd EmailService
```

### 3. Set up the database

Run `docs/sql/01_database_setup.sql` in SSMS.
This creates the `EmailService` database, both tables, indexes, and sample data.

### 4. Configure appsettings.json

```bash
cp appsettings.example.json appsettings.json
```

Fill in your SMTP credentials and connection string.

### 5. Build and run

```bash
dotnet build
dotnet run
```

---

## 📨 How to Trigger an Email

Any caller system queues an email by inserting two rows into the database.
No code change needed in EmailService — just insert and the service handles the rest.

```sql
-- Step 1: Insert the email
INSERT INTO WorkflowLog (SourceSystem, Subject, Body, Status)
VALUES (
    'QMS',
    'Document Review Required',
    '<h1>Please review document QMS-DOC-001</h1><p>Due: 2026-04-21</p>',
    'Pending'
);

-- Step 2: Insert recipients
DECLARE @Id INT = SCOPE_IDENTITY();

INSERT INTO EmailRecipient (WorkflowLogId, EmailAddress, RecipientType)
VALUES
    (@Id, 'reviewer@company.com', 'TO'),
    (@Id, 'manager@company.com',  'CC');
```

The service picks it up within 5 minutes and sends automatically.

---

## 🔍 Monitoring

```sql
-- What is queued?
SELECT WorkflowLogId, SourceSystem, Subject, Status, CreatedDate
FROM WorkflowLog WHERE Status = 'Pending'
ORDER BY CreatedDate ASC;

-- What failed and why?
SELECT WorkflowLogId, SourceSystem, ErrorMessage, RetryCount, LastAttemptDate
FROM WorkflowLog WHERE Status = 'Failed'
ORDER BY LastAttemptDate DESC;

-- Stuck in Processing > 10 minutes? (service may have crashed)
SELECT WorkflowLogId, SourceSystem, LastAttemptDate,
       DATEDIFF(MINUTE, LastAttemptDate, GETDATE()) AS MinutesStuck
FROM WorkflowLog
WHERE Status = 'Processing'
  AND DATEDIFF(MINUTE, LastAttemptDate, GETDATE()) > 10;

-- Today's summary per system
SELECT
    SourceSystem,
    SUM(CASE WHEN Status = 'Sent'    THEN 1 ELSE 0 END) AS Sent,
    SUM(CASE WHEN Status = 'Failed'  THEN 1 ELSE 0 END) AS Failed,
    SUM(CASE WHEN Status = 'Pending' THEN 1 ELSE 0 END) AS Pending
FROM WorkflowLog
WHERE CreatedDate >= CAST(GETDATE() AS DATE)
GROUP BY SourceSystem;
```

---

## 🧪 Testing

* Set `"EnableEmails": false` for dry-run mode — no real emails sent
* Insert test records into `WorkflowLog` and `EmailRecipient`
* Watch the console logs — job fires every 5 minutes
* Verify `Status` updated to `Sent` in SSMS after the cycle

**Speed up testing — change cron to every 10 seconds:**
```json
"CronExpression": "0/10 * * * * ?"
```
Change back to `0 */5 * * * ?` after testing.

---

## 🖥️ Windows Service Deployment

```powershell
# 1. Publish
dotnet publish -c Release -o C:\Services\EmailService

# 2. Install (run PowerShell as Administrator)
New-Service -Name "EmailService" `
    -DisplayName "Email Notification Service" `
    -Description "Generic email notification service for QMS, Assets, HR" `
    -BinaryPathName "C:\Services\EmailService\EmailService.exe" `
    -StartupType Automatic

# 3. Start
Start-Service -Name "EmailService"

# 4. Verify
Get-Service -Name "EmailService"
```

To uninstall:
```powershell
Stop-Service -Name "EmailService"
Remove-Service -Name "EmailService"
```

---

## 📁 Project Structure

```
EmailService/
├── Configuration/
│   ├── EmailSettings.cs          # SMTP config class
│   └── SchedulingSettings.cs     # Quartz cron + batch config
├── Data/
│   ├── EmailServiceDbContext.cs  # EF Core DbContext
│   └── EmailRepository.cs       # DB queries implementation
├── Jobs/
│   └── WorkflowEmailJob.cs      # Quartz job — orchestrates the pipeline
├── Models/
│   └── EmailModels.cs           # Entities, enum, DTO
├── Services/
│   ├── Interfaces/
│   │   ├── IEmailRepository.cs  # Repository contract
│   │   └── IEmailService.cs     # Email sender contract
│   └── EmailService.cs          # MailKit + Polly implementation
├── docs/
│   ├── sql/
│   │   └── 01_database_setup.sql
│   └── ARCHITECTURE.md
├── Program.cs                   # DI wiring + host setup
├── appsettings.json             # Local config (not committed)
└── appsettings.example.json     # Safe template (committed)
```

---

## 📦 Technologies Used

| Technology | Version | Purpose |
|---|---|---|
| .NET 8 | 8.0 | Worker Service host |
| Entity Framework Core | 8.0 | SQL Server ORM |
| MailKit | 4.3 | SMTP email sending |
| Quartz.NET | 3.8 | Job scheduling |
| Polly | 8.3 | Resilience + retry |
| Serilog | 8.0 | Structured logging |

---

## 🔮 Future Enhancements

* Email templates with placeholders (`{{Name}}`, `{{DocumentId}}`)
* REST API for manual email triggering
* Dashboard for monitoring (Sent / Failed / Pending)
* Dead-letter queue — retry Failed emails after N hours
* Multi-tenant support
* Docker + docker-compose support
* GitHub Actions CI/CD pipeline
* Unit and integration tests

---

## 📌 Design Philosophy

* **Separation of concerns** — Repository, Service, Job each do one thing
* **Interface-first** — swap SMTP provider without touching business logic
* **Configuration-driven** — behaviour controlled via `appsettings.json`
* **Observable failures** — errors stored in DB, queryable without log files
* **Generic and reusable** — any system uses it by inserting a row

---

## 👨‍💻 Author

Built as a learning + production-grade backend system to demonstrate:

* Clean architecture
* Background job processing
* Real-world resilience patterns
* Generic infrastructure design

---

## 📄 License

This project is for learning and demonstration purposes.
