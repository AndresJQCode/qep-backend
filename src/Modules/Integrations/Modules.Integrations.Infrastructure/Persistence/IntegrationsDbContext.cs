using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>integrations</c> (spec 2026-10-08, «Conexión»): <c>connections</c> y su tabla hija
/// <c>connection_secrets</c> (D9). Proyecta además <c>audit.entries</c> (auditoría atómica, ADR 0019,
/// como Identity) y <c>platform.outbox_messages</c> (como Pos), las dos <c>ExcludeFromMigrations</c>.
/// </summary>
public sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options) : DbContext(options)
{
    public const string Schema = "integrations";

    /// <summary>El único por (tenant_id, provider_key, lower(name)). Va en la migración con SQL —EF no
    /// modela índices por expresión— y IntegrationsUnitOfWork lo traduce por este nombre.</summary>
    public const string ConnectionNameIndex = "IX_connections_tenant_provider_name";

    private static readonly JsonSerializerOptions FieldsJson = new(JsonSerializerDefaults.General);

    // fields es jsonb con sólo los campos públicos; el dominio lo expone como diccionario de sólo lectura.
    private static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> FieldsConverter = new(
        fields => JsonSerializer.Serialize(fields, FieldsJson),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, FieldsJson) ?? new Dictionary<string, string>());

    // Igualdad por contenido y hash independiente del orden: dos diccionarios iguales enumerados en
    // otro orden no cuentan como cambio.
    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> FieldsComparer = new(
        (left, right) => left!.Count == right!.Count
            && left.All(pair => right.ContainsKey(pair.Key) && right[pair.Key] == pair.Value),
        fields => fields.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        fields => new Dictionary<string, string>(fields));

    public DbSet<IntegrationConnection> Connections => Set<IntegrationConnection>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<IntegrationsOutboxMessage> Outbox => Set<IntegrationsOutboxMessage>();

    /// <summary>D10: el CHECK sale del catálogo, así que un proveedor nuevo cambia el modelo y pide su
    /// migración (EF avisa de cambios pendientes).</summary>
    internal static string ProviderKeyCheck() =>
        "provider_key IN (" + string.Join(",", IntegrationProviders.All.Select(provider => $"'{provider.Key}'")) + ")";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConnection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureConnection(ModelBuilder modelBuilder)
    {
        var connection = modelBuilder.Entity<IntegrationConnection>();
        connection.ToTable("connections", Schema, table =>
        {
            table.HasCheckConstraint("CK_connections_provider_key", ProviderKeyCheck());
            table.HasCheckConstraint("CK_connections_status", "status IN ('Active','Paused','NeedsAttention')");
        });
        connection.HasKey(value => value.Id);
        connection.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        connection.Property(value => value.TenantId).HasColumnName("tenant_id");
        connection.Property(value => value.ProviderKey).HasColumnName("provider_key").HasMaxLength(IntegrationProvider.KeyMaxLength);
        connection.Property(value => value.Name).HasColumnName("name").HasMaxLength(IntegrationConnection.NameMaxLength);
        connection.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        connection.Property(value => value.Fields)
            .HasColumnName("fields")
            .HasColumnType("jsonb")
            .HasConversion(FieldsConverter, FieldsComparer);
        connection.Property(value => value.LastVerifiedAt).HasColumnName("last_verified_at");
        connection.Property(value => value.LastFailureAt).HasColumnName("last_failure_at");
        connection.Property(value => value.LastFailureCode)
            .HasColumnName("last_failure_code")
            .HasMaxLength(IntegrationConnection.FailureCodeMaxLength);
        connection.Property(value => value.CreatedAt).HasColumnName("created_at");
        // tenancy.memberships(id), sin FK: otro módulo.
        connection.Property(value => value.CreatedBy).HasColumnName("created_by");
        connection.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        connection.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        connection.HasIndex(value => value.TenantId).HasDatabaseName("IX_connections_tenant");

        connection.OwnsMany(value => value.Secrets, secret =>
        {
            secret.ToTable("connection_secrets", Schema);
            secret.WithOwner().HasForeignKey("ConnectionId");
            secret.Property<Guid>("ConnectionId").HasColumnName("connection_id");
            secret.HasKey("ConnectionId", nameof(ConnectionSecret.FieldKey));
            secret.Property(value => value.FieldKey).HasColumnName("field_key").HasMaxLength(FieldDefinition.KeyMaxLength);
            secret.Property(value => value.KeyId).HasColumnName("key_id").HasMaxLength(ProtectedSecret.KeyIdMaxLength);
            secret.Property<byte[]>("_ciphertext").HasColumnName("ciphertext").IsRequired();
            secret.Property(value => value.UpdatedAt).HasColumnName("updated_at");
            secret.Ignore(value => value.Protected);
            // El worker de rotación recorre por key_id (D9).
            secret.HasIndex(value => value.KeyId).HasDatabaseName("IX_connection_secrets_key_id");
        });
        connection.Navigation(value => value.Secrets).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<IntegrationsOutboxMessage>();
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
