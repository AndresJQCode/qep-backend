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
    /// Las variantes se leen por el agregado <see cref="Product"/>; este set existe para las
    /// consultas que buscan por un dato de la variante (código, código de barras, id) y para
    /// los adaptadores que la leen sin cargar el producto entero.
    /// </summary>
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<OptionDefinition> OptionDefinitions => Set<OptionDefinition>();

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
        ConfigureGalleryImage(modelBuilder);
        ConfigureProductVariant(modelBuilder);
        ConfigureOptionDefinition(modelBuilder);
        ConfigureOptionValue(modelBuilder);
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
        product.Property(value => value.IsActive).HasColumnName("is_active");
        // CAT-04. Nullable: hay productos ya cargados y una columna NOT NULL sin default los
        // rompe.
        product.Property(value => value.Description)
            .HasColumnName("description")
            .HasMaxLength(ProductDetails.DescriptionMaxLength);
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
        product.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        product.Property(value => value.CreatedAt).HasColumnName("created_at");
        product.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        product.HasIndex(value => value.TenantId).HasDatabaseName("IX_products_tenant");

        // GIN trigram: ProductRepository.SearchAsync filtra `Name` con `ILIKE '%termino%'`
        // (comodín a ambos lados) — mismo razonamiento que `IX_customers_name_trgm`. El de Code
        // se mudó con la columna a IX_product_variants_code_trgm.
        product.HasIndex(value => value.Name)
            .HasDatabaseName("IX_products_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        // Variantes (spec 2026-10-20): código, precios, escalas, empaques e imagen viven en la
        // variante o en la galería. Los accesos transitorios de Product que los leen no se mapean.
        product.Ignore(value => value.Code);
        product.Ignore(value => value.Prices);
        product.Ignore(value => value.PriceScales);
        product.Ignore(value => value.PackagingUnits);
        product.Ignore(value => value.ImageFileId);
        product.Ignore(value => value.OptionIds);

        // Tabla propia con FK a option_definitions, RESTRICT: una opción que algún producto usa no
        // se puede borrar ni por SQL (spec §3.1: se desactiva). Clave (producto, posición): las
        // filas se reemplazan en el lugar (Product.ReplaceOptions).
        product.OwnsMany<ProductOption>("_options", option =>
        {
            option.ToTable("product_options", "catalog");
            option.WithOwner().HasForeignKey("ProductId");
            option.Property<ProductId>("ProductId")
                .HasColumnName("product_id")
                .HasConversion(id => id.Value, value => new ProductId(value));
            option.Property(value => value.Position).HasColumnName("position").ValueGeneratedNever();
            option.Property(value => value.OptionDefinitionId)
                .HasColumnName("option_definition_id")
                .HasConversion(id => id.Value, value => new OptionDefinitionId(value));
            option.HasKey("ProductId", nameof(ProductOption.Position));
            option.HasOne<OptionDefinition>()
                .WithMany()
                .HasForeignKey(value => value.OptionDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
            option.HasIndex(value => value.OptionDefinitionId)
                .HasDatabaseName("IX_product_options_option_definition");
        });

        product.HasMany(value => value.Variants)
            .WithOne()
            .HasForeignKey(variant => variant.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        product.Navigation(value => value.Variants)
            .HasField("_variants")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        product.HasMany(value => value.Gallery)
            .WithOne()
            .HasForeignKey("ProductId")
            .OnDelete(DeleteBehavior.Cascade);
        product.Navigation(value => value.Gallery)
            .HasField("_gallery")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureGalleryImage(ModelBuilder modelBuilder)
    {
        var image = modelBuilder.Entity<ProductGalleryImage>();
        image.ToTable("product_gallery_images", "catalog");
        image.HasKey(value => value.Id);
        image.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        image.Property<ProductId>("ProductId")
            .HasColumnName("product_id")
            .HasConversion(id => id.Value, value => new ProductId(value));
        // Sin FK: apunta a storage.file_resources, y catalog no referencia las tablas de otro
        // módulo. Es un Guid suelto, como lo era products.image_file_id.
        image.Property(value => value.FileId).HasColumnName("file_id");
        image.Property(value => value.Position).HasColumnName("position");
        // Sin índice único sobre (producto, posición) ni (producto, archivo): la galería se reordena
        // en el lugar y dos filas intercambian posición en el mismo guardado. La unicidad la
        // garantiza el agregado (catalog.product.gallery_duplicate) bajo la Version del producto.
        image.HasIndex("ProductId").HasDatabaseName("IX_product_gallery_images_product");
    }

    private static void ConfigureProductVariant(ModelBuilder modelBuilder)
    {
        var variant = modelBuilder.Entity<ProductVariant>();
        variant.ToTable("product_variants", "catalog");
        variant.HasKey(value => value.Id);
        variant.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new VariantId(value))
            .ValueGeneratedNever();
        variant.Property(value => value.ProductId)
            .HasColumnName("product_id")
            .HasConversion(id => id.Value, value => new ProductId(value));
        variant.Property(value => value.TenantId).HasColumnName("tenant_id");
        variant.Property(value => value.Code)
            .HasColumnName("code")
            .HasMaxLength(ProductVariant.CodeMaxLength);
        variant.Property(value => value.Barcode)
            .HasColumnName("barcode")
            .HasMaxLength(ProductVariant.BarcodeMaxLength);
        // integer[] de PostgreSQL y no una tabla hija: es un puñado de números sin identidad
        // propia, que se reemplaza entero con la variante y nunca se consulta por separado.
        variant.PrimitiveCollection(value => value.PackagingUnits)
            .HasColumnName("packaging_units")
            .IsRequired()
            .HasDefaultValueSql("'{}'");
        variant.Property(value => value.GalleryImageId).HasColumnName("gallery_image_id");
        variant.Property(value => value.IsActive).HasColumnName("is_active");
        variant.Property(value => value.Position).HasColumnName("position");
        variant.Property(value => value.CombinationKey)
            .HasColumnName("combination_key")
            .IsRequired();
        variant.Ignore(value => value.OptionValueIds);
        variant.Ignore(value => value.PricedCurrencies);

        // SET NULL y no CASCADE: quitar la foto de la galería no borra la variante, que vuelve a
        // la principal del producto. El agregado ya lo evita (image_not_in_gallery); la FK es la
        // red para el SQL a mano.
        variant.HasOne<ProductGalleryImage>()
            .WithMany()
            .HasForeignKey(value => value.GalleryImageId)
            .OnDelete(DeleteBehavior.SetNull);

        // Spec D3. Owned y no entidad propia: un precio no tiene identidad fuera de su variante y
        // se carga siempre con ella. Las columnas heredadas price_base_usd/price_base_cop siguen en
        // products, sin mapear, hasta DropLegacyProductPriceColumns (deploy N+1).
        variant.OwnsMany(value => value.Prices, price =>
        {
            price.ToTable("product_prices", "catalog", table =>
                table.HasCheckConstraint("CK_product_prices_amount_not_negative", "amount >= 0"));
            price.WithOwner().HasForeignKey("VariantId");
            price.Property<VariantId>("VariantId")
                .HasColumnName("variant_id")
                .HasConversion(id => id.Value, value => new VariantId(value));
            price.Property(value => value.Currency)
                .HasColumnName("currency")
                .HasColumnType("character(3)")
                .HasMaxLength(3);
            price.Property(value => value.Amount).HasColumnName("amount").HasPrecision(18, 2);
            price.HasKey("VariantId", nameof(ProductPrice.Currency));
        });
        variant.Navigation(value => value.Prices).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Mismo criterio que product_options: FK RESTRICT a option_values, para que un valor en
        // uso no se borre ni por SQL; y el índice por valor sirve la pregunta «¿está en uso?».
        variant.OwnsMany<ProductVariantOptionValue>("_optionValues", value =>
        {
            value.ToTable("product_variant_option_values", "catalog");
            value.WithOwner().HasForeignKey("VariantId");
            value.Property<VariantId>("VariantId")
                .HasColumnName("variant_id")
                .HasConversion(id => id.Value, id => new VariantId(id));
            value.Property(row => row.Position).HasColumnName("position").ValueGeneratedNever();
            value.Property(row => row.OptionValueId).HasColumnName("option_value_id");
            value.HasKey("VariantId", nameof(ProductVariantOptionValue.Position));
            value.HasOne<OptionValue>()
                .WithMany()
                .HasForeignKey(row => row.OptionValueId)
                .OnDelete(DeleteBehavior.Restrict);
            value.HasIndex(row => row.OptionValueId)
                .HasDatabaseName("IX_product_variant_option_values_option_value");
        });

        variant.HasMany(value => value.PriceScales)
            .WithOne()
            .HasForeignKey(scale => scale.VariantId)
            .OnDelete(DeleteBehavior.Cascade);
        variant.Navigation(value => value.PriceScales)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // La unicidad que promete el código (spec §3.3): por tenant entre **todas** las variantes,
        // sin filtro parcial —desactivar no libera el código, como antes con
        // IX_products_tenant_code—, y con mayúsculas, igual que el índice de antes y que la
        // comparación ordinal del agregado. Nombrado a propósito: CatalogUnitOfWork traduce la
        // violación por nombre de índice (lección de SDD-CT-06).
        variant.HasIndex(value => new { value.TenantId, value.Code })
            .IsUnique()
            .HasDatabaseName("IX_product_variants_tenant_code");
        // Opcional y único cuando existe: parcial, así las variantes sin código de barras no chocan.
        variant.HasIndex(value => new { value.TenantId, value.Barcode })
            .IsUnique()
            .HasFilter("barcode IS NOT NULL")
            .HasDatabaseName("IX_product_variants_tenant_barcode");
        // **No** único: la combinación es única sólo entre variantes activas (§3.3.1), y aun así
        // un índice único parcial chocaría cuando un PUT intercambia combinaciones entre dos
        // variantes (PostgreSQL revisa fila por fila). La unicidad la hace cumplir el agregado
        // (catalog.variant.combination_duplicate) bajo la Version del producto; este índice sirve
        // para buscar la variante de un producto por su combinación.
        variant.HasIndex(value => new { value.ProductId, value.CombinationKey })
            .HasDatabaseName("IX_product_variants_product_combination");
        // El buscador filtra Code con ILIKE '%término%'; mismo motivo que IX_products_name_trgm.
        variant.HasIndex(value => value.Code)
            .HasDatabaseName("IX_product_variants_code_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }

    private static void ConfigureOptionDefinition(ModelBuilder modelBuilder)
    {
        var option = modelBuilder.Entity<OptionDefinition>();
        option.ToTable("option_definitions", "catalog");
        option.HasKey(value => value.Id);
        option.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new OptionDefinitionId(value))
            .ValueGeneratedNever();
        option.Property(value => value.TenantId).HasColumnName("tenant_id");
        option.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(OptionDefinition.NameMaxLength);
        option.Property(value => value.IsActive).HasColumnName("is_active");
        option.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        option.Property(value => value.CreatedAt).HasColumnName("created_at");
        option.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // El único por (tenant, lower(name)) lo crea AddProductVariants con SQL a mano
        // (IX_option_definitions_tenant_name), como IX_tags_tenant_name: EF no modela índices
        // sobre expresiones. Éste es el de listar la biblioteca de un tenant.
        option.HasIndex(value => value.TenantId).HasDatabaseName("IX_option_definitions_tenant");

        // Entidad y no owned: product_variant_option_values la referencia con FK, y un tipo owned
        // no puede ser principal de una relación con otro.
        option.HasMany(value => value.Values)
            .WithOne()
            .HasForeignKey("OptionDefinitionId")
            .OnDelete(DeleteBehavior.Cascade);
        option.Navigation(value => value.Values)
            .HasField("_values")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureOptionValue(ModelBuilder modelBuilder)
    {
        var value = modelBuilder.Entity<OptionValue>();
        value.ToTable("option_values", "catalog");
        value.HasKey(row => row.Id);
        value.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        value.Property<OptionDefinitionId>("OptionDefinitionId")
            .HasColumnName("option_definition_id")
            .HasConversion(id => id.Value, id => new OptionDefinitionId(id));
        value.Property(row => row.Value)
            .HasColumnName("value")
            .HasMaxLength(OptionDefinition.ValueMaxLength);
        value.Property(row => row.Position).HasColumnName("position");
        value.Property(row => row.IsActive).HasColumnName("is_active");
        // Sin índice único sobre el valor: un PUT puede intercambiar dos nombres en el mismo
        // guardado. La unicidad sin mayúsculas la hace cumplir OptionDefinition bajo su Version.
        value.HasIndex("OptionDefinitionId").HasDatabaseName("IX_option_values_option_definition");
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
        // De la variante desde AddProductVariants (antes product_id); la relación, con CASCADE,
        // la declara ConfigureProductVariant.
        scale.Property(value => value.VariantId)
            .HasColumnName("variant_id")
            .HasConversion(id => id.Value, value => new VariantId(value));
        scale.Property(value => value.TenantId).HasColumnName("tenant_id");
        scale.Property(value => value.FromUnit).HasColumnName("from_unit");
        scale.Property(value => value.ToUnit).HasColumnName("to_unit");
        scale.Property(value => value.Discount).HasColumnName("discount").HasPrecision(5, 2);
        // Como texto y no como el entero por defecto de EF: una fila legible a simple vista en
        // sql vale más que los cuatro bytes que ahorra un smallint acá. Nullable desde
        // MakePriceScaleRestrictionNullable: la copia de escalas deja tramos sin restricción.
        scale.Property(value => value.Restriction)
            .HasColumnName("restriction")
            .HasConversion<string>()
            .HasMaxLength(20);
        scale.Property(value => value.Multiple).HasColumnName("multiple");
        scale.Property(value => value.AllowGrouping).HasColumnName("allow_grouping");
        // final_usd/final_cop stay in the table, unmapped, until DropLegacyProductPriceColumns.
        scale.HasIndex(value => value.VariantId).HasDatabaseName("IX_product_price_scales_variant");
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
        // Spec §3.5: el cambio es de una variante. product_id se queda para que el reporte agrupe
        // por producto sin juntar product_variants.
        change.Property(value => value.VariantId)
            .HasColumnName("variant_id")
            .HasConversion(id => id.Value, value => new VariantId(value));
        // Como texto, mismo criterio que PriceScale.Restriction: una fila del histórico se lee a
        // mano cuando alguien discute un precio, y "ScaleDiscount" se entiende donde un 2 no.
        change.Property(value => value.Field)
            .HasColumnName("field")
            .HasConversion<string>()
            .HasMaxLength(20);
        // Spec D5: which currency a PriceBase row talks about; null for ScaleDiscount.
        change.Property(value => value.Currency)
            .HasColumnName("currency")
            .HasColumnType("character(3)")
            .HasMaxLength(3);
        // Nullable de verdad: sólo las filas de ScaleDiscount los llenan. Ver
        // ProductPriceChange.ScaleFromUnit.
        change.Property(value => value.ScaleFromUnit).HasColumnName("scale_from_unit");
        change.Property(value => value.ScaleToUnit).HasColumnName("scale_to_unit");
        // The same precision as product_prices.amount: the history has to hold any value the
        // source column accepted, or it would round the evidence.
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
        // CASCADE por el mismo motivo: borrar una variante sin uso (spec §3.3) se lleva su historia.
        change.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(value => value.VariantId)
            .OnDelete(DeleteBehavior.Cascade);
        change.HasIndex(value => value.VariantId)
            .HasDatabaseName("IX_product_price_changes_variant");

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
