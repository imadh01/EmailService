using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EmailService.Models;
public enum EmailStatus
{
    Pending = 0,   // Not yet picked by the job
    Processing = 1,   // Job claimed it — prevents duplicate sends on crash
    Sent = 2,   // Delivered successfully
    Failed = 3    // Polly retries exhausted — needs ops attention
}


[Table("WorkflowLog")]
public class WorkflowLog
{
    [Key]
    public int WorkflowLogId { get; set; }
    [Required]
    [MaxLength(100)]
    public string SourceSystem { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string Body { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = EmailStatus.Pending.ToString();

    public int RetryCount { get; set; } = 0;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedDate { get; set; }       
    public DateTime? SentDate { get; set; }         
    public DateTime? LastAttemptDate { get; set; }   

    // Navigation property — the related EmailRecipient rows.
    public virtual ICollection<EmailRecipient> Recipients { get; set; }
        = new List<EmailRecipient>();
}


[Table("EmailRecipient")]
public class EmailRecipient
{
    [Key]
    public int EmailRecipientId { get; set; }

    public int WorkflowLogId { get; set; }

    [Required]
    [MaxLength(255)]
    public string EmailAddress { get; set; } = string.Empty;

    [Required]
    [MaxLength(10)]
    public string RecipientType { get; set; } = "TO";

    public DateTime CreatedDate { get; set; }

    // Navigation property back to parent WorkflowLog.
    public virtual WorkflowLog WorkflowLog { get; set; } = null!;
}


// PendingEmail — DTO (Data Transfer Object)
public class PendingEmail
{
    public int LogId { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
}