using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>wms</c> (spec WMS 2026-10-31 §11): bodegas, ubicaciones, dueños, unidades de medida y perfiles de variante.
/// Proyecta <c>audit.entries</c> y <c>platform.outbox_messages</c> sin ser su dueño (ExcludeFromMigrations). Sin FKs a otros
/// esquemas: variantes, ciudades y proveedores son referencias blandas.
/// </summary>
public sealed class WmsDbContext(DbContextOptions<WmsDbContext> options) : DbContext(options)
{
    public const string Schema = "wms";

    internal DbSet<WmsOutboxMessage> Outbox => Set<WmsOutboxMessage>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureOutboxProjection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<WmsOutboxMessage>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
    }
}

/// <summary>Proyección del outbox de plataforma: el módulo publica sus eventos en la misma transacción que el cambio.</summary>
internal sealed class WmsOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}
