namespace EmailService.Services.Interfaces;

public interface IEmailService
{
    Task<(bool Success, int RetryCount)> SendEmailAsync(string[] to,string subject,string body,
        bool isHtml = true,string[]? cc = null,string[]? bcc = null, CancellationToken cancellationToken = default);
} 