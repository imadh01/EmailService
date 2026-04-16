# 📧 Email Notification Service

A scalable and production-ready background email processing service built with **.NET**, designed to handle email workflows across multiple systems (QMS, Asset Management, HR, etc.).

---

## 🚀 Overview

This service processes email requests stored in a database and sends them using SMTP. It is designed as a **generic, reusable infrastructure service** that can be used by multiple applications.

---

## 🧱 Architecture

```text
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
* ✅ Background job scheduling using Quartz
* ✅ Retry mechanism with Polly (exponential backoff)
* ✅ HTML email support
* ✅ Clean architecture (Repository + Service pattern)
* ✅ Configurable via `appsettings.json`
* ✅ Logging with Serilog
* ✅ Idempotent processing using `Status` field
* ✅ Batch processing support

---

## 🗄️ Database Design

### **WorkflowLog**

Stores email requests.

| Column        | Description                          |
| ------------- | ------------------------------------ |
| WorkflowLogId | Primary Key                          |
| SourceSystem  | System name (QMS, Asset, etc.)       |
| Subject       | Email subject                        |
| Body          | Email content (HTML supported)       |
| Status        | Pending / Processing / Sent / Failed |
| RetryCount    | Number of retries                    |
| ErrorMessage  | Failure details                      |
| CreatedDate   | Created timestamp                    |
| SentDate      | Sent timestamp                       |

---

### **EmailRecipient**

Stores recipients.

| Column           | Description       |
| ---------------- | ----------------- |
| EmailRecipientId | Primary Key       |
| WorkflowLogId    | FK to WorkflowLog |
| EmailAddress     | Recipient email   |
| RecipientType    | TO / CC / BCC     |

---

## ⚙️ Configuration

Defined in `appsettings.json`:

```json
"EmailSettings": {
  "EmailHost": "smtp.gmail.com",
  "EmailPort": 587,
  "EmailUserName": "your-email@gmail.com",
  "EmailPassword": "your-password",
  "EmailFrom": "your-email@gmail.com",
  "EnableSsl": true,
  "DefaultCredentials": false,
  "EnableEmails": true,
  "MaxRetryAttempts": 3,
  "RetryDelayMilliseconds": 100
}
```

---

## 🧠 Core Concepts

### 🔹 Status-Based Processing

* `Pending` → Ready to process
* `Processing` → Locked by job
* `Sent` → Completed
* `Failed` → Needs attention

👉 Prevents duplicate email sending.

---

### 🔹 Retry Mechanism (Polly)

* Handles transient failures (SMTP/network)
* Uses exponential backoff
* Logs retry attempts

---

### 🔹 HTML Email Support

Emails are stored as HTML and rendered by email clients for better formatting.

---

## 📦 Technologies Used

* .NET 8
* Entity Framework Core
* MailKit (SMTP email sending)
* Quartz.NET (job scheduling)
* Polly (resilience & retry)
* Serilog (logging)

---

## 🛠️ Getting Started

### 1. Clone the repository

```bash
git clone <your-repo-url>
```

### 2. Update configuration

Modify `appsettings.json` with your SMTP details.

### 3. Run migrations

```bash
dotnet ef database update
```

### 4. Run the application

```bash
dotnet run
```

---

## 🧪 Testing

* Set `"EnableEmails": false` for dry-run mode
* Insert test records into `WorkflowLog`
* Verify logs and processing

---

## 🔮 Future Enhancements

* Email templates with placeholders (`{{Name}}`)
* API for manual email triggering
* Dashboard for monitoring
* Dead-letter queue for failed emails
* Multi-tenant support

---

## 📌 Design Philosophy

* Separation of concerns (Repository, Service, Job)
* Configuration-driven behavior
* Scalable and reusable across systems
* Production-ready patterns

---

## 👨‍💻 Author

Built as a learning + production-grade backend system to demonstrate:

* Clean architecture
* Background processing
* Real-world system design

---

## 📄 License

This project is for learning and demonstration purposes.
