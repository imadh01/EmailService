namespace EmailService.Configuration;

public class EmailSettings
{
    public string EmailHost { get; set; } = string.Empty;

    public int EmailPort { get; set; }

    public string EmailUserName { get; set; } = string.Empty;

    public string EmailPassword { get; set; } = string.Empty;

    public string EmailFrom { get; set; } = string.Empty;

    public bool EnableSsl { get; set; }

    public bool DefaultCredentials { get; set; }

    public bool EnableEmails { get; set; } = true;

    public int MaxRetryAttempts { get; set; } = 3;

    public int RetryDelayMilliseconds { get; set; } = 100;
}