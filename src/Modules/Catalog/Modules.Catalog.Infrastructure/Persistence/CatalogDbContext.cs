using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<TaxRate> TaxRates => Set<TaxRate>();

    internal DbSet<PriceScale> PriceScales => Set<PriceScale>();

    /// <summary>
    /// Público, a diferencia de <see cref="PriceScales"/>: el histórico de precios no se lee a
    /// través del agregado <c>Product</c> —no es parte de él— y el reporte que lo consume vive
    /// fuera de este módulo, así que necesita un adaptador que pueda consultarlo.
    /// </summary>
    public DbSet<ProductPriceChange> ProductPriceChanges => Set<ProductPriceChange>();

    internal DbSet<CatalogOutboxMessage> Outbox => Set<CatalogOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureProduct(modelBuilder);
        ConfigureTaxRate(modelBuilder);
        ConfigurePriceScale(modelBuilder);
        ConfigureProductPriceChange(modelBuilder);
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
        // CAT-09. Independientes del viejo Price: no lo reemplazan.
        product.Property(value => value.PriceBaseUsd)
            .HasColumnName("price_base_usd")
            .HasPrecision(18, 2);
        product.Property(value => value.PriceBaseCop)
            .HasColumnName("price_base_cop")
            .HasPrecision(18, 2);
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

        // GIN trigram: ProductRepository.SearchAsync filtra `Name`/`Code` con
        // `ILIKE '%termino%'` (comodin a ambos lados) — mismo razonamiento que
        // `IX_customers_name_trgm`. El indice unico de arriba ya cubre la igualdad exacta de
        // Code, pero no el "contains" que hace el buscador del listado.
        product.HasIndex(value => value.Name)
            .HasDatabaseName("IX_products_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
        product.HasIndex(value => value.Code)
            .HasDatabaseName("IX_products_code_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
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

    private static void ConfigureProductPriceChange(ModelBuilder modelBuilder)
    {
        var change = modelBuilder.Entity<ProductPriceChange>();
        change.ToTable("product_price_changes", "catalog");
        change.HasKey(value => value.Id);
        change.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new ProductPriceChangeId(value))
            .ValueGeneratedNever();
        change.Property(value => value.TenantId).HasColumnName("tenant_id");
        change.Property(value => value.ProductId)
            .HasColumnName("product_id")
            .HasConversion(id => id.Value, value => new ProductId(value));
        // Como texto, mismo criterio que PriceScale.Restriction: una fila del histórico se lee a
        // mano cuando alguien discute un precio, y "ScaleDiscount" se entiende donde un 2 no.
        change.Property(value => value.Field)
            .HasColumnName("field")
            .HasConversion<string>()
            .HasMaxLength(20);
        // Nullable de verdad: sólo las filas de ScaleDiscount los llenan. Ver
        // ProductPriceChange.ScaleFromUnit.
        change.Property(value => value.ScaleFromUnit).HasColumnName("scale_from_unit");
        change.Property(value => value.ScaleToUnit).HasColumnName("scale_to_unit");
        // La misma precisión que price_base_usd/cop: el histórico tiene que poder guardar
        // cualquier valor que la columna de origen aceptó, o redondearía la evidencia.
        change.Property(value => value.PreviousValue)
            .HasColumnName("previous_value")
            .HasPrecision(18, 2);
        change.Property(value => value.NewValue)
            .HasColumnName("new_value")
            .HasPrecision(18, 2);
        change.Property(value => value.ChangedBy).HasColumnName("changed_by");
        change.Property(value => value.ChangedAt).HasColumnName("changed_at");

        // CASCADE, igual que product_price_scales y a diferencia de la FK de TaxRate: el
        // histórico de precios de un producto que ya no existe no tiene a quién describir, y
        // dejarlo con RESTRICT haría que ningún producto se pueda borrar nunca.
        change.HasOne<Product>()
            .WithMany()
            .HasForeignKey(value => value.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        change.HasIndex(value => value.TenantId)
            .HasDatabaseName("IX_product_price_changes_tenant");
        // El reporte pregunta "qué cambió en este tenant entre estas dos fechas", y ése es un
        // recorrido de rango sobre changed_at dentro de un tenant. Con sólo el índice de arriba
        // la consulta trae todo el histórico del tenant y filtra la fecha en memoria.
        change.HasIndex(value => new { value.TenantId, value.ChangedAt })
            .HasDatabaseName("IX_product_price_changes_tenant_changed_at");
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
