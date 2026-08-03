using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;

namespace Modules.Audit.Infrastructure.Persistence;

// Owns the audit store (schema "audit"): the append-only audit.entries table and this
// module's operational inbox. Producer contexts (e.g. TenancyDbContext) map the same
// audit.entries table as an ExcludeFromMigrations write projection so critical audits
// commit atomically inside the producer transaction (ADR 0019). Keep the audit.entries
// column mapping here in sync with those projections.
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    internal DbSet<AuditInboxMessage> Inbox => Set<AuditInboxMessage>();

    internal DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureEntry(modelBuilder, ownsTable: true);
        ConfigureInbox(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    // Shared shape of the audit.entries table. `ownsTable` is true for the owning
    // AuditDbContext (the table is created by this module's migrations) and false for
    // producer write projections, which map the same physical table but exclude it from
    // their own migrations.
    public static void ConfigureEntry(ModelBuilder modelBuilder, bool ownsTable)
    {
        var entry = modelBuilder.Entity<AuditEntry>();
        if (ownsTable)
        {
            entry.ToTable("entries", "audit");
        }
        else
        {
            entry.ToTable("entries", "audit", table => table.ExcludeFromMigrations());
        }

        entry.HasKey(value => value.Id);
        entry.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new AuditEntryId(value))
            .ValueGeneratedNever();
        entry.Property(value => value.TenantId).HasColumnName("tenant_id");
        entry.Property(value => value.ActorId).HasColumnName("actor_id");
        entry.Property(value => value.ActorType)
            .HasColumnName("actor_type")
            .HasConversion<string>()
            .HasMaxLength(20);
        entry.Property(value => value.Action).HasColumnName("action").HasMaxLength(150);
        entry.Property(value => value.ResourceType).HasColumnName("resource_type").HasMaxLength(100);
        entry.Property(value => value.ResourceId).HasColumnName("resource_id").HasMaxLength(150);
        entry.Property(value => value.Outcome).HasColumnName("outcome").HasMaxLength(30);
        entry.Property(value => value.ChangedFieldsJson)
            .HasColumnName("changed_fields")
            .HasColumnType("jsonb");
        entry.Property(value => value.Source).HasColumnName("source").HasMaxLength(100);
        entry.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        entry.HasIndex(value => new { value.TenantId, value.OccurredAt });
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<AuditInboxMessage>();
        inbox.ToTable("inbox_messages", "audit");
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        // Read-only view over the platform Outbox owned by the producing module.
        var outbox = modelBuilder.Entity<OutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
    }
}
