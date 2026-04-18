namespace EmailService.Configuration;
public class SchedulingSettings
{
    public const string SectionName = "SchedulingSettings";
    public WorkflowEmailSettings WorkflowEmail { get; set; } = new();
}


// ─────────────────────────────────────────────────────────────────────
// WorkflowEmailSettings — settings specific to WorkflowEmailJob
// ─────────────────────────────────────────────────────────────────────
public class WorkflowEmailSettings
{
    public bool Enabled { get; set; } = true;
    public string CronExpression { get; set; } = "0 */5 * * * ?";
    public int BatchSize { get; set; } = 50;
}

// Toggle the job on/off without redeploying.
// false → Quartz does not register the trigger → job never runs.
// Useful for maintenance windows or disabling in non-prod environments.
// ─────────────────────────────────────────────────────────────
// CronExpression — controls WHEN the job fires
// "0 */5 * * * ?"
//   0     → at second 0 (top of the minute)
//   */5   → every 5 minutes
//   *     → every hour
//   *     → every day of month
//   *     → every month
//   ?     → any day of week (? = no specific value)