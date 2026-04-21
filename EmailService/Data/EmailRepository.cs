using EmailService.Models;
using EmailService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EmailService.Data;
public class EmailRepository : IEmailRepository
{
    private readonly EmailServiceDbContext _context;
    private readonly ILogger<EmailRepository> _logger;

    public EmailRepository(EmailServiceDbContext context, ILogger<EmailRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<PendingEmail>> GetAndLockPendingEmailsAsync(int batchSize,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // CreateExecutionStrategy() wraps our manual transaction
            // in a retriable unit — satisfies SqlServerRetryingExecutionStrategy
            var strategy = _context.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await _context.Database
                    .BeginTransactionAsync(cancellationToken);

                try
                {
                    var pending = await _context.WorkflowLogs
                        .Where(w => w.Status == EmailStatus.Pending.ToString())
                        .OrderBy(w => w.CreatedDate)
                        .Take(batchSize)
                        .ToListAsync(cancellationToken);

                    if (pending.Count == 0)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        _logger.LogDebug("No pending emails found");
                        return Enumerable.Empty<PendingEmail>();
                    }

                    foreach (var log in pending)
                    {
                        log.Status = EmailStatus.Processing.ToString();
                        log.LastAttemptDate = DateTime.UtcNow;
                    }

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    var result = pending.Select(p => new PendingEmail
                    {
                        LogId = p.WorkflowLogId,
                        SourceSystem = p.SourceSystem,
                        Subject = p.Subject,
                        Body = p.Body,
                        CreatedDate = p.CreatedDate
                    }).ToList();

                    _logger.LogInformation(
                        "Locked {Count} pending emails for processing",
                        result.Count);

                    return result;
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve and lock pending emails");
            return Enumerable.Empty<PendingEmail>();
        }
    }


    //   The job only needs strings to pass to MailKit.
    public async Task<IEnumerable<string>> GetRecipientsAsync(int logId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recipients = await _context.EmailRecipients
                .Where(r => r.WorkflowLogId == logId)
                .Select(r => r.EmailAddress)
                .ToListAsync(cancellationToken);

            _logger.LogDebug(
                "Retrieved {Count} recipients for LogId {LogId}",
                recipients.Count, logId);

            return recipients;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recipients for LogId {LogId}", logId);
            return Enumerable.Empty<string>();
        }
    }

    // Called by the job AFTER a successful send.
    public async Task<bool> UpdateEmailStatusSentAsync(int logId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var row = await _context.WorkflowLogs
                .FindAsync(new object[] { logId }, cancellationToken);

            if (row is null)
            {
                _logger.LogWarning(
                    "UpdateEmailStatusSentAsync: WorkflowLog {LogId} not found", logId);
                return false;
            }

            row.Status = EmailStatus.Sent.ToString();
            row.SentDate = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("LogId {LogId} marked as Sent", logId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark LogId {LogId} as Sent", logId);
            return false;
        }
    }

    // Called by the job when Polly retries are exhausted.
    // Sets Status = 'Failed' and records:
    //   ErrorMessage    — what went wrong (visible in SSMS)
    //   RetryCount      — how many times Polly tried
    //   LastAttemptDate — when we last attempted
    public async Task<bool> UpdateEmailStatusFailedAsync(int logId,string errorMessage,int retryCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var row = await _context.WorkflowLogs
                .FindAsync(new object[] { logId }, cancellationToken);

            if (row is null)
            {
                _logger.LogWarning(
                    "UpdateEmailStatusFailedAsync: WorkflowLog {LogId} not found", logId);
                return false;
            }

            row.Status = EmailStatus.Failed.ToString();
            row.ErrorMessage = errorMessage;
            row.RetryCount = retryCount;
            row.LastAttemptDate = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "LogId {LogId} marked as Failed — Error: {Error} — Retries: {RetryCount}",
                logId, errorMessage, retryCount);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark LogId {LogId} as Failed", logId);
            return false;
        }
    }
}