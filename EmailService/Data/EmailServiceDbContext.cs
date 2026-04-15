using Microsoft.EntityFrameworkCore;
using EmailService.Models;

namespace EmailService.Data;
public class EmailServiceDbContext : DbContext
{
    public EmailServiceDbContext(DbContextOptions<EmailServiceDbContext> options)
        : base(options)
    {
    }

    public DbSet<WorkflowLog> WorkflowLogs { get; set; }
    public DbSet<EmailRecipient> EmailRecipients { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── WorkflowLog configuration ─────────────────────────────
        modelBuilder.Entity<WorkflowLog>(entity =>
        {
            entity.ToTable("WorkflowLog");
            entity.HasKey(w => w.WorkflowLogId);
            entity.Property(w => w.Status)
                  .HasDefaultValue(EmailStatus.Pending.ToString());
            entity.Property(w => w.SourceSystem)
                  .HasDefaultValue("General");
            entity.Property(w => w.CreatedDate)
                  .ValueGeneratedOnAdd();
        });

        // ── EmailRecipient configuration ─────────────────────────
        modelBuilder.Entity<EmailRecipient>(entity =>
        {
            entity.ToTable("EmailRecipient");
            entity.HasKey(r => r.EmailRecipientId);
            entity.Property(r => r.RecipientType)
                  .HasDefaultValue("TO");
            entity.Property(r => r.CreatedDate)
                  .ValueGeneratedOnAdd();
        });

        // ── Relationship: WorkflowLog → EmailRecipient ────────────

        modelBuilder.Entity<WorkflowLog>()
            .HasMany(w => w.Recipients)
            .WithOne(r => r.WorkflowLog)
            .HasForeignKey(r => r.WorkflowLogId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}