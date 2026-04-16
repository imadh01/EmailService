using EmailService.Configuration;
using EmailService.Services.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using Polly;
using System.Net.Sockets;

namespace EmailService.Services;

// Responsibility: build a MIME email and deliver it over SMTP.Two libraries working together here:
//   MailKit  → builds and sends the email (industry standard for .NET)
//   Polly    → retries the send if SMTP fails (resilience library)

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly EmailSettings _emailSettings;
    private readonly IAsyncPolicy<(bool Success, int RetryCount)> _retryPolicy;

    public EmailService(ILogger<EmailService> logger,IOptions<EmailSettings> emailSettings)
    {
        _logger = logger;
        _emailSettings = emailSettings.Value;

        // What Polly does here:
        //   If SendEmailAsync fails for any of these reasons:
        //     SmtpCommandException   → SMTP server rejected the command
        //     SmtpProtocolException  → SMTP conversation broke down
        //     SocketException        → network connection dropped
        //     IOException            → low-level I/O failure
        //   Then: wait and try again, up to MaxRetryAttempts times.
        //
        // WHY exponential backoff?
        //   Attempt 1 fails → wait 50ms  (50 * 2^1)
        //   Attempt 2 fails → wait 100ms (50 * 2^2)
        //   Attempt 3 fails → wait 200ms (50 * 2^3)
        //
        //   Flat retry (wait 50ms every time) hammers a struggling
        //   SMTP server and makes the problem worse.
        //   Exponential backoff gives the server time to recover
        //   before you try again — standard resilience pattern.
        //

        _retryPolicy = Policy<(bool Success, int RetryCount)>
            .Handle<SmtpCommandException>()
            .Or<SmtpProtocolException>()
            .Or<SocketException>()
            .Or<IOException>()
            .OrResult(r => !r.Success)
            .WaitAndRetryAsync(
                retryCount: _emailSettings.MaxRetryAttempts,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromMilliseconds(
                        _emailSettings.RetryDelayMilliseconds * Math.Pow(2, attempt)),
                onRetry: (outcome, delay, retryNumber, context) =>
                {
                    _logger.LogWarning(
                        "SMTP retry {RetryNumber}/{MaxRetries} — waiting {Delay}ms. Reason: {Reason}",
                        retryNumber,
                        _emailSettings.MaxRetryAttempts,
                        delay.TotalMilliseconds,
                        outcome.Exception?.Message ?? "result indicated failure");
                });
    }

    public async Task<(bool Success, int RetryCount)> SendEmailAsync(
        string[] to,
        string subject,
        string body,
        bool isHtml = true,
        string[]? cc = null,
        string[]? bcc = null,
        CancellationToken cancellationToken = default)
    {
        if (!_emailSettings.EnableEmails)
        {
            _logger.LogInformation(
                "[DRY RUN] Email skipped — To: {Recipients} Subject: {Subject}",
                string.Join(", ", to),
                subject);
            return (true, 0);
        }

        int attemptCount = 0;

        try
        {
            var result = await _retryPolicy.ExecuteAsync(async () =>
            {
                attemptCount++;
                using var message = new MimeMessage();

                message.From.Add(new MailboxAddress("Email Notification Service", _emailSettings.EmailFrom));

                // TO recipients — filter blanks defensively
                foreach (var recipient in to.Where(r => !string.IsNullOrWhiteSpace(r)))
                    message.To.Add(MailboxAddress.Parse(recipient.Trim()));

                // CC — optional, only add if provided
                if (cc?.Any() == true)
                    foreach (var c in cc.Where(c => !string.IsNullOrWhiteSpace(c)))
                        message.Cc.Add(MailboxAddress.Parse(c.Trim()));

                // BCC — optional, hidden from other recipients
                if (bcc?.Any() == true)
                    foreach (var b in bcc.Where(b => !string.IsNullOrWhiteSpace(b)))
                        message.Bcc.Add(MailboxAddress.Parse(b.Trim()));

                message.Subject = subject;

                // TextFormat.Html → Content-Type: text/html
                // TextFormat.Plain → Content-Type: text/plain
                message.Body = new TextPart(isHtml ? TextFormat.Html : TextFormat.Plain)
                {
                    Text = body
                };

                // ─────────────────────────────────────────────────
                // SMTP CLIENT — connect, authenticate, send
                //
                // using var client → SmtpClient holds a TCP socket.
                // Must be disposed to close the connection cleanly.
                // The finally block handles disconnect explicitly
                // because DisconnectAsync needs to be awaited —
                // Dispose() is synchronous and can't await it.
                // ─────────────────────────────────────────────────
                using var client = new SmtpClient();

                try
                {
                    // Connect to the SMTP server
                    // SecureSocketOptions.StartTls → upgrades to TLS
                    // on port 587 after initial plain-text handshake.
                    // SecureSocketOptions.None → plain, no encryption
                    // (only for internal/company SMTP servers).
                    await client.ConnectAsync(
                        _emailSettings.EmailHost,
                        _emailSettings.EmailPort,
                        _emailSettings.EnableSsl
                            ? SecureSocketOptions.StartTls
                            : SecureSocketOptions.None,
                        cancellationToken);

                    // Authenticate — skip if using Windows credentials
                    // (DefaultCredentials = true for company SMTP servers
                    //  that trust the machine account).
                    if (!_emailSettings.DefaultCredentials)
                    {
                        await client.AuthenticateAsync(
                            _emailSettings.EmailUserName,
                            _emailSettings.EmailPassword,
                            cancellationToken);
                    }

                    // Send — this is the actual SMTP DATA command
                    await client.SendAsync(message, cancellationToken);

                    _logger.LogInformation(
                        "Email sent — To: {RecipientCount} recipient(s) — Subject: {Subject} — Attempt: {Attempt}",
                        to.Length,
                        subject,
                        attemptCount);

                    // Return success with how many attempts it took
                    // attemptCount - 1 = retries (first attempt is not a retry)
                    return (true, attemptCount - 1);
                }
                finally
                {
                    // Always disconnect cleanly — even if send threw.
                    // finally runs whether the try succeeded or failed.
                    if (client.IsConnected)
                        await client.DisconnectAsync(quit: true, cancellationToken);
                }
            });

            return result;
        }
        catch (Exception ex)
        {
            // All Polly retries exhausted — log and return failure.
            // The job will call UpdateEmailStatusFailedAsync with
            // the attemptCount so it's stored in the DB.
            _logger.LogError(
                ex,
                "Email failed after {Attempts} attempt(s) — To: {Recipients} — Subject: {Subject}",
                attemptCount,
                string.Join(", ", to),
                subject);

            return (false, attemptCount - 1);
        }
    }
}