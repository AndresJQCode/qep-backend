using Microsoft.EntityFrameworkCore;
using Modules.ApiAccess.Domain;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;

namespace Modules.ApiAccess.Infrastructure.Persistence;

/// <summary>
/// Dueño del esquema <c>api_access</c>. Proyecta además <c>audit.entries</c> (auditoría atómica,
/// ADR 0019), que crea la migración del módulo Audit: aquí se mapea sin migrarla.
/// </summary>
public sealed class ApiAccessDbContext(DbContextOptions<ApiAccessDbContext> options)
    : DbContext(options)
{
    public const string Schema = "api_access";

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureApiKey(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureApiKey(ModelBuilder modelBuilder)
    {
        var key = modelBuilder.Entity<ApiKey>();
        key.ToTable("api_keys", Schema, table =>
        {
            table.HasCheckConstraint("CK_api_keys_scopes", "cardinality(scopes) > 0");
            table.HasCheckConstraint(
                "CK_api_keys_connections",
                "connection_ids IS NULL OR cardinality(connection_ids) > 0");
            table.HasCheckConstraint(
                "CK_api_keys_revocation",
                "(revoked_at IS NULL) = (revoked_by_member_id IS NULL)");
        });
        key.HasKey(value => value.Id);
        key.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new ApiKeyId(value))
            .ValueGeneratedNever();
        key.Property(value => value.TenantId).HasColumnName("tenant_id");
        key.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(ApiKey.NameMaxLength);
        key.Property(value => value.KeyPrefix)
            .HasColumnName("key_prefix")
            .HasMaxLength(16);
        key.Property(value => value.SecretHash)
            .HasColumnName("secret_hash")
            .HasColumnType("bytea");
        key.Property(value => value.Scopes)
            .HasColumnName("scopes")
            .HasColumnType("text[]");
        key.Property(value => value.ConnectionIds)
            .HasColumnName("connection_ids")
            .HasColumnType("uuid[]");
        key.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        key.Property(value => value.LastUsedAt).HasColumnName("last_used_at");
        key.Property(value => value.CreatedAt).HasColumnName("created_at");
        key.Property(value => value.CreatedByMemberId).HasColumnName("created_by_member_id");
        key.Property(value => value.RevokedAt).HasColumnName("revoked_at");
        key.Property(value => value.RevokedByMemberId).HasColumnName("revoked_by_member_id");
        key.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        key.HasIndex(value => value.KeyPrefix)
            .IsUnique()
            .HasDatabaseName("IX_api_keys_key_prefix");

        // Parcial: una clave revocada libera su nombre.
        key.HasIndex(value => new { value.TenantId, value.Name })
            .IsUnique()
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("IX_api_keys_tenant_name_active");

        key.HasIndex(value => new { value.TenantId, value.CreatedAt })
            .HasDatabaseName("IX_api_keys_tenant_created");
    }
}
