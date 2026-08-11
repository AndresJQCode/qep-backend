using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    internal DbSet<CatalogOutboxMessage> Outbox => Set<CatalogOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureProduct(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureProduct(ModelBuilder modelBuilder)
    {
        var product = modelBuilder.Entity<Product>();
        product.ToTable("products", "catalog");
        product.HasKey(value => value.Id);
        product.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new ProductId(value))
            .ValueGeneratedNever();
        product.Property(value => value.TenantId).HasColumnName("tenant_id");
        product.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(Product.NameMaxLength);
        product.Property(value => value.Code)
            .HasColumnName("code")
            .HasMaxLength(Product.CodeMaxLength);
        product.Property(value => value.IsActive).HasColumnName("is_active");
        product.Property(value => value.CreatedAt).HasColumnName("created_at");
        product.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        product.HasIndex(value => value.TenantId).HasDatabaseName("IX_products_tenant");
        // The uniqueness the product code promises. Named on purpose: the infrastructure
        // layer discriminates the unique violation by index name, not by SqlState alone,
        // because other unique indexes in this database would otherwise be reported with
        // the wrong domain code. That was the lesson of SDD-CT-06.
        product.HasIndex(value => new { value.TenantId, value.Code })
            .IsUnique()
            .HasDatabaseName("IX_products_tenant_code");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<CatalogOutboxMessage>();
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
