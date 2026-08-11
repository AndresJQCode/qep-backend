using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;

namespace Modules.Audit.Infrastructure.Persistence;

// Dueño del almacén de auditoría (esquema "audit"): la tabla append-only audit.entries y
// el inbox operativo de este módulo. Los contextos productores (p. ej. TenancyDbContext)
// mapean la misma tabla audit.entries como proyección de escritura ExcludeFromMigrations,
// para que las auditorías críticas commiteen atómicas en la transacción del productor
// (ADR 0019). Mantener este mapeo de columnas sincronizado con esas proyecciones.
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

    // Forma compartida de la tabla audit.entries. `ownsTable` es true para el AuditDbContext
    // dueño (la tabla la crean las migraciones de este módulo) y false para las proyecciones
    // de escritura de los productores, que mapean la misma tabla física pero la excluyen de
    // sus propias migraciones.
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
        // Vista de sólo lectura sobre el Outbox de plataforma, propiedad del módulo productor.
        var outbox = modelBuilder.Entity<OutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
    }
}
