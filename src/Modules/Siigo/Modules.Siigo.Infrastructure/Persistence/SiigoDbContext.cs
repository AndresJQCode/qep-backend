using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>siigo</c> (spec 2026-11-18 §5.1). Todavía sin tablas propias: la configuración por conexión, los vínculos y las facturas
/// llegan con la migración <c>InitialSiigo</c>. Proyecta <c>audit.entries</c> y <c>platform.outbox_messages</c> sin ser su dueño
/// (<c>ExcludeFromMigrations</c>, H19), como Shopify. Sin FKs a otros esquemas: conexión, pedido, cliente, producto y empresa son
/// referencias blandas.
/// </summary>
public sealed class SiigoDbContext(DbContextOptions<SiigoDbContext> options) : DbContext(options)
{
    public const string Schema = "siigo";

    // Proyecciones de escritura (ExcludeFromMigrations): Audit es dueño de audit.entries y Tenancy de platform.outbox_messages.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<SiigoOutboxMessage> Outbox => Set<SiigoOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<SiigoOutboxMessage>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
        outbox.Property(value => value.LastError).HasColumnName("last_error");
    }
}

/// <summary>
/// Proyección de escritura del outbox de plataforma, propiedad de Tenancy. Siigo inserta acá sus eventos en la misma transacción que
/// el cambio que los origina.
/// </summary>
internal sealed class SiigoOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}
