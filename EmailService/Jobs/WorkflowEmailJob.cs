using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using EmailService.Configuration;
using EmailService.Services.Interfaces;
using System.Diagnostics;

namespace EmailService.Jobs;

// ─────────────────────────────────────────────────────────────────────
// [DisallowConcurrentExecution]
//
// This attribute tells Quartz: if this job is still running when the
// next trigger fires, DON'T start a second instance — skip that cycle.
//   With it:
//     Cycle 1 starts at 00:00 → still running at 00:05
//     Cycle 2 trigger fires   → Quartz sees Cycle 1 still running → skips
//     Cycle 1 finishes        → Cycle 3 starts normally at 00:10
//

[DisallowConcurrentExecution]
public class WorkflowEmailJob : IJob
{
    private readonly ILogger<WorkflowEmailJob> _logger;
    private readonly IServiceProvider _serviceProvider;

    public WorkflowEmailJob(
        ILogger<WorkflowEmailJob> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEmailRepository>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var settings = scope.ServiceProvider
                               .GetRequiredService<IOptions<SchedulingSettings>>().Value;

        var sw = Stopwatch.StartNew();
        int processed = 0;
        int failed = 0;

        try
        {
            _logger.LogInformation(
                "WorkflowEmailJob started — {Time}",
                DateTimeOffset.Now);

            var pending = await repository.GetAndLockPendingEmailsAsync(
                settings.WorkflowEmail.BatchSize,
                context.CancellationToken);

            var batch = pending.ToList();

            if (batch.Count == 0)
            {
                _logger.LogDebug("No pending emails — job exiting early");
                return;
            }

            _logger.LogInformation(
                "Batch locked — processing {Count} email(s)",
                batch.Count);

            // ─────────────────────────────────────────────────────
            // STEP 2: Process each email in the batch
            // ─────────────────────────────────────────────────────
            foreach (var email in batch)
            {
                // Check cancellation before each email.
                // If the Windows Service is stopping (services.msc
                // stop, server restart etc.), CancellationToken is
                // signalled. We break cleanly instead of being killed
                // mid-send. Rows stay as Processing — ops can reset.
                if (context.CancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Cancellation requested — stopping batch at {Processed}/{Total}",
                        processed, batch.Count);
                    break;
                }

                // Per-email try-catch — ONE bad email must not stop
                // the rest of the batch. We catch, mark Failed, move on.
                try
                {
                    _logger.LogDebug(
                        "Processing LogId {LogId} — [{SourceSystem}] {Subject}",
                        email.LogId, email.SourceSystem, email.Subject);

                    // ─────────────────────────────────────────────
                    // STEP 2a: Get recipients for this email
                    // ─────────────────────────────────────────────
                    var recipients = await repository.GetRecipientsAsync(
                        email.LogId,
                        context.CancellationToken);

                    var recipientList = recipients.ToList();

                    // No recipients → mark Failed immediately, skip send
                    if (recipientList.Count == 0)
                    {
                        _logger.LogWarning(
                            "LogId {LogId} has no recipients — marking Failed",
                            email.LogId);

                        await repository.UpdateEmailStatusFailedAsync(
                            email.LogId,
                            errorMessage: "No recipients found in EmailRecipient table",
                            retryCount: 0,
                            context.CancellationToken);

                        failed++;
                        continue; // move to next email in batch
                    }

                    // ─────────────────────────────────────────────
                    // STEP 2b: Send the email via EmailService
                    //
                    // Subject and Body come from the DTO (from DB).
                    // The job doesn't build or modify them —
                    // it just passes them straight through.
                    //
                    // Tuple unpacking: var (success, retryCount)
                    // pulls both values out of the returned tuple
                    // in one clean line.
                    // ─────────────────────────────────────────────
                    var (success, retryCount) = await emailService.SendEmailAsync(
                        to: recipientList.ToArray(),
                        subject: email.Subject,
                        body: email.Body,
                        isHtml: true,
                        cancellationToken: context.CancellationToken);

                    // ─────────────────────────────────────────────
                    // STEP 2c: Update status based on result
                    // ─────────────────────────────────────────────
                    if (success)
                    {
                        await repository.UpdateEmailStatusSentAsync(
                            email.LogId,
                            context.CancellationToken);

                        processed++;

                        _logger.LogInformation(
                            "LogId {LogId} sent — {RecipientCount} recipient(s) — {Retries} retry/retries",
                            email.LogId, recipientList.Count, retryCount);
                    }
                    else
                    {
                        await repository.UpdateEmailStatusFailedAsync(
                            email.LogId,
                            errorMessage: $"SMTP failed after {retryCount} retry/retries",
                            retryCount: retryCount,
                            context.CancellationToken);

                        failed++;

                        _logger.LogError(
                            "LogId {LogId} failed — {Retries} retry/retries exhausted",
                            email.LogId, retryCount);
                    }
                }
                catch (Exception ex)
                {
                    // Unexpected exception for this specific email.
                    // Log it, mark Failed, increment counter, continue.
                    failed++;

                    _logger.LogError(
                        ex,
                        "Unexpected error processing LogId {LogId}",
                        email.LogId);

                    await repository.UpdateEmailStatusFailedAsync(
                        email.LogId,
                        errorMessage: ex.Message,
                        retryCount: 0,
                        context.CancellationToken);
                }
            }

            // ─────────────────────────────────────────────────────
            // STEP 3: Log the summary
            //
            // This one log line tells you everything about the cycle:
            //   [INF] Job done — Sent=48 Failed=2 Duration=1423ms
            //
            // If Failed > 0 → query WorkflowLog WHERE Status='Failed'
            // to see exactly which ones and why.
            // ─────────────────────────────────────────────────────
            sw.Stop();
            _logger.LogInformation(
                "WorkflowEmailJob done — Sent={Processed} Failed={Failed} Duration={Duration}ms",
                processed, failed, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Only reaches here if something outside the per-email
            // loop blew up (e.g. GetAndLockPendingEmailsAsync threw
            // after the repository's own catch couldn't handle it).
            // Rethrow so Quartz logs it as a job execution failure.
            _logger.LogError(ex, "Critical failure in WorkflowEmailJob");
            throw;
        }
    }
}