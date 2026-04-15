using EmailService.Models;

namespace EmailService.Services.Interfaces;
public interface IEmailRepository
{
    Task<IEnumerable<PendingEmail>> GetAndLockPendingEmailsAsync(int batchSize, CancellationToken cancellationToken = default);

    Task<IEnumerable<string>> GetRecipientsAsync(int logId, CancellationToken cancellationToken = default);

    Task<bool> UpdateEmailStatusSentAsync(int logId,CancellationToken cancellationToken = default);

    Task<bool> UpdateEmailStatusFailedAsync(int logId,string errorMessage,int retryCount,CancellationToken cancellationToken = default);
}
