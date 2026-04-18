using Microsoft.EntityFrameworkCore;
using Quartz;
using Serilog;
using EmailService.Configuration;
using EmailService.Data;
using EmailService.Jobs;
using EmailService.Services;
using EmailService.Services.Interfaces;


var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Services.AddSerilog();

// Windows Service support
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EmailService";
});

//  Bind configuration classes 
builder.Services.AddOptions<EmailSettings>()
    .Bind(builder.Configuration.GetSection(EmailSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SchedulingSettings>()
    .Bind(builder.Configuration.GetSection(SchedulingSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

//  Register DbContext (Entity Framework Core)

builder.Services.AddDbContext<EmailServiceDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(maxRetryCount: 5);
            sqlOptions.CommandTimeout(30);
        });
});

// Register application services
builder.Services.AddScoped<IEmailRepository, EmailRepository>();
builder.Services.AddScoped<IEmailService, EmailService.Services.EmailService>();

// STEP 6: Configure Quartz scheduler
var schedulingSettings = builder.Configuration
    .GetSection(SchedulingSettings.SectionName)
    .Get<SchedulingSettings>() ?? new SchedulingSettings();

builder.Services.AddQuartz(q =>
{
    q.UseSimpleTypeLoader();
    q.UseInMemoryStore();
    q.UseDefaultThreadPool(tp =>
    {
        tp.MaxConcurrency = 1;
    });

    // Only register the job if it's enabled in appsettings.json.
    if (schedulingSettings.WorkflowEmail.Enabled)
    {
        var jobKey = new JobKey("workflow-email-job");

        q.AddJob<WorkflowEmailJob>(opts => opts
            .WithIdentity(jobKey)
            .DisallowConcurrentExecution());

        q.AddTrigger(opts => opts
            .ForJob(jobKey)
            .WithIdentity("workflow-email-trigger")
            .WithCronSchedule(schedulingSettings.WorkflowEmail.CronExpression));
    }
});

builder.Services.AddQuartzHostedService(q =>
{
    q.WaitForJobsToComplete = true;
});

// Build and run the host
var host = builder.Build();

try
{
    Log.Information("EmailService starting...");
    await host.RunAsync();
}
catch (Exception ex)
{
    // If startup itself fails (bad config, DB unreachable etc.)
    // log it as Fatal so it's clearly visible before the process exits.
    Log.Fatal(ex, "EmailService failed to start");
}
finally
{
    // Always flush Serilog before exit — ensures no log lines are lost.
    await Log.CloseAndFlushAsync();
}