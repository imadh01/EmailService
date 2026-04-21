# 📚 Concepts Used in EmailService

Every concept encountered while building this project — explained clearly.

---

## 1. .NET Worker Service

A Worker Service is a .NET project template designed for **long-running background processes**.
Unlike a Web API (which listens for HTTP requests), a Worker Service just runs continuously in the background.

```
Web API      → waits for HTTP requests → responds
Worker Service → runs forever → does background work on a schedule
```

Our EmailService is a Worker Service because it never needs to respond to HTTP — it just polls the database every 5 minutes and sends emails.

---

## 2. Dependency Injection (DI)

DI is a design pattern where a class **receives its dependencies from outside** instead of creating them internally.

```csharp
// ❌ Without DI — class creates its own dependency
public class EmailRepository
{
    private readonly EmailServiceDbContext _context = new EmailServiceDbContext(); // hardcoded
}

// ✅ With DI — dependency is injected from outside
public class EmailRepository
{
    private readonly EmailServiceDbContext _context;

    public EmailRepository(EmailServiceDbContext context) // injected
    {
        _context = context;
    }
}
```

**Why DI?**
- Classes don't need to know HOW to create their dependencies
- Swap implementations without changing the class (e.g. real DB vs fake in tests)
- The DI container manages object lifetimes automatically

**Three lifetimes in .NET DI:**

| Lifetime | Created | Disposed | Used for |
|---|---|---|---|
| Singleton | Once at startup | App shutdown | Stateless services, loggers |
| Scoped | Once per scope | Scope ends | DbContext, Repository |
| Transient | Every time requested | After use | Lightweight, stateless |

---

## 3. The .NET Host

The Host is the **engine that keeps your app alive** and manages everything inside it.

```csharp
var builder = Host.CreateApplicationBuilder(args);
// register services...
var app = builder.Build();
await app.RunAsync();
```

Responsibilities:
- Starts and stops the app cleanly
- Manages the DI container (`builder.Services`)
- Reads configuration (`appsettings.json`)
- Controls service lifetimes (what starts first, what shuts down last)

---

## 4. Options Pattern

A way to bind `appsettings.json` sections to strongly typed C# classes.

```json
// appsettings.json
"EmailSettings": {
  "EmailHost": "smtp.gmail.com",
  "EmailPort": 587
}
```

```csharp
// EmailSettings.cs
public class EmailSettings
{
    public string EmailHost { get; set; } = string.Empty;
    public int EmailPort { get; set; }
}

// Program.cs
builder.Services.AddOptions<EmailSettings>()
    .Bind(builder.Configuration.GetSection("EmailSettings"));

// Any class
public class EmailService
{
    public EmailService(IOptions<EmailSettings> options)
    {
        var host = options.Value.EmailHost; // "smtp.gmail.com"
    }
}
```

**Why not just read `IConfiguration["EmailSettings:EmailHost"]` directly?**
- Magic strings → typo = null at runtime, no compile error
- No IntelliSense
- No validation on startup
- Options pattern gives you all three benefits

---

## 5. Interface / Contract Pattern

An interface defines WHAT a class must do — not HOW.

```csharp
// Contract — WHAT
public interface IEmailRepository
{
    Task<IEnumerable<PendingEmail>> GetAndLockPendingEmailsAsync(int batchSize, ...);
    Task<bool> UpdateEmailStatusSentAsync(int logId, ...);
}

// Implementation — HOW
public class EmailRepository : IEmailRepository
{
    public async Task<IEnumerable<PendingEmail>> GetAndLockPendingEmailsAsync(...) { ... }
    public async Task<bool> UpdateEmailStatusSentAsync(...) { ... }
}
```

**Why?**
- Job depends on `IEmailRepository` — not `EmailRepository`
- Swap SQL Server for InMemory in tests → change one line in Program.cs
- Multiple implementations possible (SQL Server, Oracle, Cosmos DB)

---

## 6. Repository Pattern

A design pattern that **abstracts all database access** behind a class.
No other layer writes SQL or EF Core queries — only the Repository.

```
Job → IEmailRepository → EmailRepository → DbContext → SQL Server
```

**Why?**
- Business logic (Job) never knows which database you're using
- Change from SQL Server to PostgreSQL → only Repository changes
- Testable — mock the interface in unit tests

---

## 7. Entity Framework Core (EF Core)

An ORM (Object-Relational Mapper) — translates C# code into SQL automatically.

```csharp
// You write C#:
var pending = await _context.WorkflowLogs
    .Where(w => w.Status == "Pending")
    .OrderBy(w => w.CreatedDate)
    .Take(50)
    .ToListAsync();

// EF Core sends SQL:
// SELECT TOP 50 * FROM WorkflowLog
// WHERE Status = 'Pending'
// ORDER BY CreatedDate ASC
```

**Key EF Core concepts:**

**DbContext** — the bridge between C# and the database. One instance per scope.

**DbSet\<T\>** — a queryable gateway into a table.
```csharp
public DbSet<WorkflowLog> WorkflowLogs { get; set; }
```

**Change Tracking** — EF watches entities you load. When you modify a property and call `SaveChangesAsync()`, EF automatically generates the UPDATE SQL.

**FindAsync** — checks EF's in-memory cache first, hits DB only if not found. Best when you know the primary key.

**Navigation Properties** — relationships between entities.
```csharp
public virtual ICollection<EmailRecipient> Recipients { get; set; }
```

---

## 8. Entity vs DTO

**Entity** — a C# class that maps to a database table. Lives in the Data layer.

**DTO (Data Transfer Object)** — a simple class that carries only the data needed between layers. No EF tracking, no navigation properties, no overhead.

```
Database → Entity (WorkflowLog) → Repository converts → DTO (PendingEmail) → Job
```

**Why not just pass the Entity everywhere?**
- Entity carries EF change tracking overhead
- Job would be coupled to EF Core (wrong layer)
- Entity has more data than the Job needs
- DTO is a clean contract: "here is exactly what you need"

---

## 9. Enum

A set of named constants. Prevents magic strings.

```csharp
public enum EmailStatus
{
    Pending    = 0,
    Processing = 1,
    Sent       = 2,
    Failed     = 3
}

// Usage:
row.Status = EmailStatus.Sent.ToString(); // → "Sent"

// vs magic string:
row.Status = "Sent"; // typo "sent" → wrong case → query misses it
```

**Why store as string in DB and not int?**
`"Sent"` is readable by anyone in SSMS. `2` tells you nothing without looking up the code.

---

## 10. async / await

A way to perform I/O operations (DB queries, HTTP calls, file reads) **without blocking the thread**.

```csharp
// Synchronous — thread blocked while DB query runs:
var result = _context.WorkflowLogs.ToList();
// Thread sits doing nothing for 200ms waiting for SQL Server

// Asynchronous — thread released while DB query runs:
var result = await _context.WorkflowLogs.ToListAsync();
// Thread returned to pool during the 200ms wait
// When DB responds, a thread picks up and continues
```

**Rules:**
- `await` can only be used inside an `async` method
- `async` method must return `Task`, `Task<T>`, or `void` (avoid void)
- `async`/`await` propagates up the call chain

---

## 11. Task\<T\>

A promise — "this method will eventually give you a T."

```csharp
Task<List<string>>  → await it → List<string>
Task<bool>          → await it → bool
Task                → await it → nothing (void equivalent)
```

---

## 12. using (Resource Management)

Two completely different uses:

**Namespace import:**
```csharp
using EmailService.Models; // shortcut — skip typing full namespace
```

**Resource disposal:**
```csharp
using var transaction = await _context.Database.BeginTransactionAsync();
// Guaranteed: transaction.Dispose() called when scope ends
// Even if an exception is thrown
```

Any object with a `Dispose()` method should be wrapped in `using`.
Without it → resources (DB connections, file handles, sockets) leak.

---

## 13. try-catch-finally

```csharp
try
{
    // code that might fail
}
catch (Exception ex)
{
    // runs ONLY if try threw an exception
    _logger.LogError(ex, "Something failed");
}
finally
{
    // runs ALWAYS — success or failure
    if (client.IsConnected)
        await client.DisconnectAsync();
}
```

**Pattern in our batch job:**
```
Outer try-catch → wraps entire job cycle
Inner try-catch → wraps each individual email

Inner catch: one bad email → log, mark Failed, continue batch ✅
Outer catch: catastrophic failure → log, rethrow to Quartz ✅
```

---

## 14. Structured Logging (Serilog)

```csharp
// ❌ String concatenation — bad:
_logger.LogInformation("Locked " + count + " emails");
// String built even if logging disabled
// Log tools can't extract the value of count

// ✅ Structured logging — good:
_logger.LogInformation("Locked {Count} emails", count);
// {Count} stored as a named property
// Can query: "show all logs where Count > 100"
```

**Four log levels:**

| Level | When to use |
|---|---|
| LogDebug | Verbose detail — dev only |
| LogInformation | Normal milestones — job started, email sent |
| LogWarning | Unexpected but not fatal — no recipients found |
| LogError | Something broke — pass the exception as first arg |

---

## 15. Quartz.NET (Job Scheduling)

A .NET library for scheduling background jobs using cron expressions.

```csharp
// Register job + trigger in Program.cs
q.AddJob<WorkflowEmailJob>(opts => opts.WithIdentity("workflow-email-job"));
q.AddTrigger(opts => opts
    .ForJob("workflow-email-job")
    .WithCronSchedule("0 */5 * * * ?"));  // every 5 minutes
```

**Cron expression — 6 fields (Quartz uses seconds):**
```
Seconds  Minutes  Hours  DayOfMonth  Month  DayOfWeek
  0       */5      *         *          *        ?
```

**[DisallowConcurrentExecution]** — prevents two instances of the same job running simultaneously.

---

## 16. Polly (Resilience & Retry)

A .NET library for handling transient failures with retry policies.

```csharp
_retryPolicy = Policy<(bool, int)>
    .Handle<SmtpCommandException>()
    .Or<SocketException>()
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: attempt =>
            TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)));
```

**Exponential backoff:**
```
Attempt 1 fails → wait 200ms
Attempt 2 fails → wait 400ms
Attempt 3 fails → wait 800ms
Give up → return (false, retryCount)
```

**Why exponential and not flat?**
Flat retry hammers a struggling server and makes it worse.
Exponential gives the server time to recover between attempts.

---

## 17. MailKit

The industry standard .NET library for sending emails via SMTP.
Replaces the outdated `System.Net.Mail`.

```csharp
using var message = new MimeMessage();
message.From.Add(new MailboxAddress("EmailService", "noreply@company.com"));
message.To.Add(MailboxAddress.Parse("recipient@company.com"));
message.Subject = "Hello";
message.Body = new TextPart(TextFormat.Html) { Text = "<h1>Hello</h1>" };

using var client = new SmtpClient();
await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls);
await client.AuthenticateAsync("user", "password");
await client.SendAsync(message);
await client.DisconnectAsync(true);
```

---

## 18. Windows Service

A process that runs in the background on Windows with no user logged in.
Starts automatically on boot. Managed via `services.msc` or PowerShell.

```powershell
New-Service -Name "EmailService" -BinaryPathName "C:\Services\EmailService.exe"
Start-Service -Name "EmailService"
```

In .NET 8:
```csharp
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EmailService";
});
```

---

## 19. Atomic Database Transaction

A group of SQL operations that succeed or fail together.
Either ALL happen, or NONE happen. No partial state.

```csharp
using var transaction = await _context.Database.BeginTransactionAsync();
try
{
    // SELECT pending rows
    // UPDATE rows to Processing
    await _context.SaveChangesAsync();
    await transaction.CommitAsync();  // both happen together ✅
}
catch
{
    await transaction.RollbackAsync(); // neither happens ✅
    throw;
}
```

**Why we use it:**
Without a transaction, two workers can SELECT the same Pending rows
between the SELECT and UPDATE → both send the same email → duplicate.
Transaction locks the rows so only one worker can claim them.

---

## 20. IServiceProvider + Manual Scope

Used in the Job to safely resolve Scoped services from a Singleton.

```csharp
// Quartz creates WorkflowEmailJob as Singleton
// DbContext is Scoped — can't inject directly (captive dependency)

// Solution: inject IServiceProvider, create scope manually
using var scope = _serviceProvider.CreateScope();
var repository = scope.ServiceProvider.GetRequiredService<IEmailRepository>();
// scope disposed at end of Execute() → DbContext disposed cleanly
```

**Captive Dependency Problem:**
```
Singleton holds Scoped → Scoped never disposed
DbContext never disposed → DB connection held open forever
Connection pool exhausted → app crashes under load
```

---

## 21. Value Tuple

Return multiple values from a method without creating a class.

```csharp
// Method returns two values:
Task<(bool Success, int RetryCount)> SendEmailAsync(...)

// Caller unpacks:
var (success, retryCount) = await emailService.SendEmailAsync(...);

if (success)
    await repository.UpdateEmailStatusSentAsync(logId);
else
    await repository.UpdateEmailStatusFailedAsync(logId, "Failed", retryCount);
```

---

## 22. Cascade Delete

When a parent row is deleted, all child rows are automatically deleted too.

```sql
CONSTRAINT FK_EmailRecipient_WorkflowLog
    FOREIGN KEY (WorkflowLogId)
    REFERENCES WorkflowLog(WorkflowLogId)
    ON DELETE CASCADE
```

```csharp
// EF Core Fluent API:
.HasMany(w => w.Recipients)
.WithOne(r => r.WorkflowLog)
.HasForeignKey(r => r.WorkflowLogId)
.OnDelete(DeleteBehavior.Cascade);
```

Delete a WorkflowLog → all its EmailRecipient rows deleted automatically.
Prevents orphaned recipient rows with no parent.

---

## 23. Fluent API (EF Core)

Configuration of EF Core mappings in `OnModelCreating` instead of data annotations.

```csharp
// Data Annotation (on the model — mixes DB concerns into domain):
[Table("WorkflowLog")]
[Required]
public string Subject { get; set; }

// Fluent API (in DbContext — keeps model clean):
modelBuilder.Entity<WorkflowLog>(entity =>
{
    entity.ToTable("WorkflowLog");
    entity.Property(w => w.Subject).IsRequired();
});
```

Fluent API is preferred in production — DB configuration lives in the Data layer, not scattered across model classes.

---

## 24. ICollection\<T\> vs List\<T\> vs IEnumerable\<T\>

| Type | Can Add/Remove | Count | Queryable | Best for |
|---|---|---|---|---|
| `IEnumerable<T>` | ❌ | ❌ | ✅ | Read-only iteration |
| `ICollection<T>` | ✅ | ✅ | ✅ | EF navigation properties |
| `List<T>` | ✅ | ✅ | ✅ | Concrete implementation |

Navigation properties use `ICollection<T>` — supports Add/Remove + Count,
EF Core can populate it, and it's not tied to one concrete implementation.

---

## 25. null! (Null Forgiving Operator)

```csharp
public virtual WorkflowLog WorkflowLog { get; set; } = null!;
```

Tells the compiler: "I know this looks null but trust me — EF Core will
always populate it before I use it."
Suppresses the CS8618 nullable warning without making the property nullable.
