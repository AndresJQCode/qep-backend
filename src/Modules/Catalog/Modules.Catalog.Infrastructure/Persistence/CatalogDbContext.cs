using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<TaxRate> TaxRates => Set<TaxRate>();

    internal DbSet<CatalogOutboxMessage> Outbox => Set<CatalogOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureProduct(modelBuilder);
        ConfigureTaxRate(modelBuilder);
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
        product.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        product.Property(value => value.CreatedAt).HasColumnName("created_at");
        product.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        product.HasIndex(value => value.TenantId).HasDatabaseName("IX_products_tenant");
        // La unicidad que promete el código de producto. Nombrado a propósito: la capa de
        // infraestructura discrimina la violación de unicidad por nombre de índice, no sólo por
        // SqlState, porque si no otros índices únicos de esta base se reportarían con el código
        // de dominio equivocado. Esa fue la lección de SDD-CT-06.
        product.HasIndex(value => new { value.TenantId, value.Code })
            .IsUnique()
            .HasDatabaseName("IX_products_tenant_code");
    }

    private static void ConfigureTaxRate(ModelBuilder modelBuilder)
    {
        var taxRate = modelBuilder.Entity<TaxRate>();
        taxRate.ToTable("tax_rates", "catalog");
        taxRate.HasKey(value => value.Id);
        taxRate.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new TaxRateId(value))
            .ValueGeneratedNever();
        taxRate.Property(value => value.TenantId).HasColumnName("tenant_id");
        taxRate.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(TaxRate.NameMaxLength);
        taxRate.Property(value => value.Percentage).HasColumnName("percentage");
        taxRate.Property(value => value.IsActive).HasColumnName("is_active");
        taxRate.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        taxRate.Property(value => value.CreatedAt).HasColumnName("created_at");
        taxRate.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        taxRate.HasIndex(value => value.TenantId).HasDatabaseName("IX_tax_rates_tenant");
        // Mismo criterio que IX_products_tenant_code, y el mismo nombre explícito: la traducción
        // de la violación de unicidad discrimina por nombre de índice, no sólo por SqlState.
        // Con dos índices únicos en el mismo esquema, confundirlos manda al llamador a corregir
        // el campo equivocado — la lección de SDD-CT-06.
        taxRate.HasIndex(value => new { value.TenantId, value.Name })
            .IsUnique()
            .HasDatabaseName("IX_tax_rates_tenant_name");
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
