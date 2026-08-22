using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<TaxRate> TaxRates => Set<TaxRate>();

    internal DbSet<PriceScale> PriceScales => Set<PriceScale>();

    internal DbSet<CatalogOutboxMessage> Outbox => Set<CatalogOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureProduct(modelBuilder);
        ConfigureTaxRate(modelBuilder);
        ConfigurePriceScale(modelBuilder);
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
        // CAT-04. Nullable: hay productos ya cargados y una columna NOT NULL sin default los
        // rompe.
        product.Property(value => value.Description)
            .HasColumnName("description")
            .HasMaxLength(ProductDetails.DescriptionMaxLength);
        // Sin FK: apunta a storage.file_resources, y catalog no referencia las tablas de otro
        // módulo. Es un Guid suelto, como cualquier referencia entre módulos de este monolito.
        product.Property(value => value.ImageFileId).HasColumnName("image_file_id");
        product.Property(value => value.Currency)
            .HasColumnName("currency")
            .HasMaxLength(ProductDetails.CurrencyLength)
            .IsFixedLength();
        product.Property(value => value.TaxRateId)
            .HasColumnName("tax_rate_id")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new TaxRateId(value.Value) : null);
        // Acá sí hay FK: las dos tablas viven en el esquema catalog, del mismo módulo. RESTRICT y
        // no CASCADE — borrar una tasa no debe borrar productos.
        //
        // Pero la FK NO sabe de tenants: garantiza que la fila exista, no que sea del tenant del
        // producto. Eso lo verifica ProductTaxRateResolver en el handler, y lo cubre
        // CA-CAT-04-07.
        product.HasOne<TaxRate>()
            .WithMany()
            .HasForeignKey(value => value.TaxRateId)
            .OnDelete(DeleteBehavior.Restrict);
        // CAT-09. Independientes de Price/Currency: no los reemplazan.
        product.Property(value => value.PriceBaseUsd)
            .HasColumnName("price_base_usd")
            .HasPrecision(18, 2);
        product.Property(value => value.PriceBaseCop)
            .HasColumnName("price_base_cop")
            .HasPrecision(18, 2);
        product.Property(value => value.PriceFinalUsd)
            .HasColumnName("price_final_usd")
            .HasPrecision(18, 2);
        product.Property(value => value.PriceFinalCop)
            .HasColumnName("price_final_cop")
            .HasPrecision(18, 2);
        product.Property(value => value.Discount)
            .HasColumnName("discount")
            .HasPrecision(5, 2);
        product.Navigation(value => value.PriceScales)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
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

    private static void ConfigurePriceScale(ModelBuilder modelBuilder)
    {
        var scale = modelBuilder.Entity<PriceScale>();
        scale.ToTable("product_price_scales", "catalog");
        scale.HasKey(value => value.Id);
        scale.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PriceScaleId(value))
            .ValueGeneratedNever();
        scale.Property(value => value.ProductId)
            .HasColumnName("product_id")
            .HasConversion(id => id.Value, value => new ProductId(value));
        scale.Property(value => value.TenantId).HasColumnName("tenant_id");
        scale.Property(value => value.FromUnit).HasColumnName("from_unit");
        scale.Property(value => value.ToUnit).HasColumnName("to_unit");
        scale.Property(value => value.Discount).HasColumnName("discount").HasPrecision(5, 2);
        // Como texto y no como el entero por defecto de EF: una fila legible a simple vista en
        // sql vale más que los cuatro bytes que ahorra un smallint acá.
        scale.Property(value => value.Restriction)
            .HasColumnName("restriction")
            .HasConversion<string>()
            .HasMaxLength(20);
        scale.Property(value => value.Multiple).HasColumnName("multiple");
        scale.Property(value => value.PackagingUnit).HasColumnName("packaging_unit");
        scale.Property(value => value.FinalUsd).HasColumnName("final_usd").HasPrecision(18, 2);
        scale.Property(value => value.FinalCop).HasColumnName("final_cop").HasPrecision(18, 2);
        scale.HasIndex(value => value.ProductId).HasDatabaseName("IX_product_price_scales_product");

        // CASCADE y no RESTRICT, a diferencia de la FK de TaxRate: una escala no tiene sentido
        // sin su producto — no es una referencia a un catálogo compartido, es parte del mismo
        // agregado. Borrar el producto debe borrar sus escalas.
        scale.HasOne<Product>()
            .WithMany(product => product.PriceScales)
            .HasForeignKey(value => value.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
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
