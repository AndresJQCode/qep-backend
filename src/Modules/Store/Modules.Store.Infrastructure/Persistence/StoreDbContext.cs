using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Store.Domain;

namespace Modules.Store.Infrastructure.Persistence;

public sealed class StoreDbContext(DbContextOptions<StoreDbContext> options)
    : DbContext(options)
{
    public const string Schema = "store";

    // Sombras que el dominio no conoce: el orden de las monedas y el "hay envío" que la bandeja consulta sin abrir el bloque.
    internal const string CurrencyPositionColumn = "position";

    internal const string HasShippingProperty = "HasShipping";

    public DbSet<OnlineStore> Stores => Set<OnlineStore>();

    public DbSet<StoreDomain> Domains => Set<StoreDomain>();

    public DbSet<StoreOrderRequest> OrderRequests => Set<StoreOrderRequest>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    // Proyección de escritura de platform.outbox_messages (ExcludeFromMigrations): Tenancy es dueño de la tabla.
    internal DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SyncDerivedColumns();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SyncDerivedColumns();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureStore(modelBuilder);
        ConfigureDomain(modelBuilder);
        ConfigureOrderRequest(modelBuilder);
        ConfigureRequestCounter(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    // Dos columnas se derivan del agregado y no tienen propiedad en el dominio: se escriben acá, justo antes de guardar, para que
    // ningún llamador tenga que acordarse de ellas.
    private void SyncDerivedColumns()
    {
        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries<OnlineStore>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var position = 0;
            foreach (var currency in entry.Entity.Currencies)
            {
                Entry(currency).Property<int>(CurrencyPositionColumn).CurrentValue = position++;
            }
        }

        foreach (var entry in ChangeTracker.Entries<StoreOrderRequest>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Property<bool>(HasShippingProperty).CurrentValue = entry.Entity.Shipping is not null;
        }
    }

    // Los enums van como texto de 16 y los `version` como bigint fijado a mano: un cambio futuro del tipo CLR no cambia el esquema.
    // Los CHECK repiten lo que el dominio ya valida, para que la base no dependa de quien escribe.
    private static void ConfigureStore(ModelBuilder modelBuilder)
    {
        var store = modelBuilder.Entity<OnlineStore>();
        store.ToTable("stores", Schema, table =>
        {
            table.HasCheckConstraint("CK_stores_pricing_mode", "pricing_mode IN ('RetailOnly','QuantityScales')");
            table.HasCheckConstraint(
                "CK_stores_colors",
                "(primary_color IS NULL OR primary_color ~ '^#[0-9A-F]{6}$')"
                + " AND (on_primary_color IS NULL OR on_primary_color ~ '^#[0-9A-F]{6}$')"
                + " AND (accent_text_color IS NULL OR accent_text_color ~ '^#[0-9A-F]{6}$')");
        });
        store.HasKey(value => value.TenantId);
        store.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        store.Property(value => value.Slug).HasColumnName("slug").HasMaxLength(40);
        store.Property(value => value.IsPublished).HasColumnName("is_published");
        store.Property(value => value.PublishedAt).HasColumnName("published_at");
        store.Property(value => value.Name).HasColumnName("name").HasMaxLength(80);
        store.Property(value => value.PrimaryColor).HasColumnName("primary_color").HasMaxLength(7);
        store.Property(value => value.OnPrimaryColor).HasColumnName("on_primary_color").HasMaxLength(7);
        store.Property(value => value.AccentTextColor).HasColumnName("accent_text_color").HasMaxLength(7);
        store.Property(value => value.BannerFileId).HasColumnName("banner_file_id");
        store.Property(value => value.BannerText).HasColumnName("banner_text").HasMaxLength(200);

        // Required: con todas las columnas en null EF devolvería un Contact nulo, y el dominio nunca lo es.
        store.OwnsOne(value => value.Contact, contact =>
        {
            contact.Property(value => value.WhatsApp).HasColumnName("whatsapp").HasMaxLength(32);
            contact.Property(value => value.Address).HasColumnName("address").HasMaxLength(200);
            contact.Property(value => value.Schedule).HasColumnName("schedule").HasMaxLength(200);
            contact.Property(value => value.InstagramUrl).HasColumnName("instagram_url").HasMaxLength(500);
            contact.Property(value => value.FacebookUrl).HasColumnName("facebook_url").HasMaxLength(500);
            contact.Property(value => value.TiktokUrl).HasColumnName("tiktok_url").HasMaxLength(500);
        });
        store.Navigation(value => value.Contact).IsRequired();

        store.Property(value => value.DataPolicyUrl).HasColumnName("data_policy_url").HasMaxLength(500);
        store.Property(value => value.DataPolicySetAt).HasColumnName("data_policy_set_at");
        store.Property(value => value.PricingMode)
            .HasColumnName("pricing_mode")
            .HasConversion<string>()
            .HasMaxLength(16);
        store.Property(value => value.PrimaryCurrency).HasColumnName("primary_currency").HasMaxLength(3);

        // Arreglos nativos de Postgres: el orden de la lista es el de la pantalla y un arreglo lo conserva sin columna extra.
        store.Property(value => value.SalesCountries)
            .HasColumnName("sales_countries")
            .HasColumnType("character varying(2)[]")
            .HasDefaultValueSql("'{}'")
            .HasField("_salesCountries");
        store.Property(value => value.FeaturedCategoryIds)
            .HasColumnName("featured_category_ids")
            .HasColumnType("uuid[]")
            .HasDefaultValueSql("'{}'")
            .HasField("_featuredCategoryIds");

        store.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        store.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        store.Property(value => value.UpdatedBy).HasColumnName("updated_by");

        // Único sólo entre los que tienen slug: una tienda sin configurar no lo ocupa.
        store.HasIndex(value => value.Slug)
            .IsUnique()
            .HasFilter("slug IS NOT NULL")
            .HasDatabaseName("IX_stores_slug");

        ConfigureCurrencies(store);
    }

    // Colección owned en tabla propia, como las cuentas bancarias de Suppliers: reemplazar la lista borra las filas que ya no están
    // y no deja huérfanas. La clave es (tenant, moneda), pero EF lee las monedas ordenadas por ella; el orden que importa es el
    // guardado en `position`, y el repositorio lo restituye al cargar.
    private static void ConfigureCurrencies(EntityTypeBuilder<OnlineStore> store)
    {
        store.OwnsMany(value => value.Currencies, currency =>
        {
            currency.ToTable("store_currencies", Schema, table =>
                table.HasCheckConstraint("CK_store_currencies_account_currency", "billing_currency = currency"));
            currency.WithOwner().HasForeignKey("tenant_id");
            currency.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
            currency.HasKey("tenant_id", nameof(StoreCurrency.Currency));
            currency.Property<int>(CurrencyPositionColumn).HasColumnName(CurrencyPositionColumn);

            currency.OwnsOne(value => value.BillingAccount, account =>
            {
                account.Property(value => value.CompanyId).HasColumnName("billing_company_id");
                account.Property(value => value.BankName).HasColumnName("billing_bank_name").HasMaxLength(120);
                account.Property(value => value.AccountNumber).HasColumnName("billing_account_number").HasMaxLength(32);
                account.Property(value => value.Currency).HasColumnName("billing_currency").HasMaxLength(3);
            });
            currency.Navigation(value => value.BillingAccount).IsRequired();
        });
        store.Navigation(value => value.Currencies).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureDomain(ModelBuilder modelBuilder)
    {
        var domain = modelBuilder.Entity<StoreDomain>();
        domain.ToTable("store_domains", Schema);
        domain.HasKey(value => value.Hostname);
        domain.Property(value => value.Hostname)
            .HasColumnName("hostname")
            .HasMaxLength(StoreDomain.HostnameMaxLength)
            .ValueGeneratedNever();
        domain.Property(value => value.TenantId).HasColumnName("tenant_id");
        domain.Property(value => value.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16);
        domain.Property(value => value.CreatedAt).HasColumnName("created_at");
        domain.HasIndex(value => value.TenantId).HasDatabaseName("IX_store_domains_tenant");
    }

    private static void ConfigureOrderRequest(ModelBuilder modelBuilder)
    {
        var request = modelBuilder.Entity<StoreOrderRequest>();
        request.ToTable("order_requests", Schema, table =>
        {
            table.HasCheckConstraint("CK_order_requests_status", "status IN ('New','Converted','Discarded')");
            table.HasCheckConstraint(
                "CK_order_requests_discard_reason",
                "discard_reason IS NULL OR discard_reason IN ('Spam','Duplicate','Other')");
            // Un envío es todo o nada: has_shipping es true exactamente cuando están las columnas que el dominio exige.
            table.HasCheckConstraint(
                "CK_order_requests_shipping",
                "has_shipping = (shipping_phone IS NOT NULL AND shipping_phone_e164 IS NOT NULL"
                + " AND shipping_address IS NOT NULL AND shipping_country IS NOT NULL AND shipping_city_name IS NOT NULL)");
            // Los dos carriles de ciudad (spec 4.2): Colombia lleva los ids de DIVIPOLA y cualquier otro país no.
            table.HasCheckConstraint(
                "CK_order_requests_billing_lane",
                "(billing_country = 'CO') = (billing_department_id IS NOT NULL AND billing_city_id IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_order_requests_shipping_lane",
                "shipping_country IS NULL"
                + " OR ((shipping_country = 'CO') = (shipping_department_id IS NOT NULL AND shipping_city_id IS NOT NULL))");
        });
        request.HasKey(value => value.Id);
        request.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        request.Property(value => value.TenantId).HasColumnName("tenant_id");
        request.Property(value => value.Number).HasColumnName("number").HasColumnType("bigint");
        request.Property(value => value.ClientRequestId).HasColumnName("client_request_id");
        request.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);

        // Billing y Shipping son complex y no owned: sus constructores validan y reciben el lugar como parámetro, y EF no enlaza
        // navegaciones owned a parámetros. Siguen siendo columnas planas de la misma tabla.
        request.ComplexProperty(value => value.Billing, billing =>
        {
            billing.Property(value => value.Name).HasColumnName("billing_name").HasMaxLength(150);
            billing.Property(value => value.IdentificationType).HasColumnName("billing_identification_type").HasMaxLength(16);
            billing.Property(value => value.IdentificationNumber).HasColumnName("billing_identification_number").HasMaxLength(32);
            billing.Property(value => value.Phone).HasColumnName("billing_phone").HasMaxLength(32);
            billing.Property(value => value.PhoneE164).HasColumnName("billing_phone_e164").HasMaxLength(20);
            billing.Property(value => value.Email).HasColumnName("billing_email").HasMaxLength(254);
            billing.Property(value => value.Address).HasColumnName("billing_address").HasMaxLength(200);
            billing.ComplexProperty(value => value.Place, place => ConfigurePlace(place, "billing"));
        });

        // Un bloque en columnas y no en JSON: la bandeja lo consulta. Nulo = "usar los mismos datos".
        request.ComplexProperty(value => value.Shipping, shipping =>
        {
            shipping.Property(value => value.RecipientName).HasColumnName("shipping_recipient_name").HasMaxLength(120);
            shipping.Property(value => value.Phone).HasColumnName("shipping_phone").HasMaxLength(32);
            shipping.Property(value => value.PhoneE164).HasColumnName("shipping_phone_e164").HasMaxLength(20);
            shipping.Property(value => value.Address).HasColumnName("shipping_address").HasMaxLength(200);
            shipping.ComplexProperty(value => value.Place, place => ConfigurePlace(place, "shipping"));
        });
        request.Property<bool>(HasShippingProperty).HasColumnName("has_shipping");

        request.Property(value => value.Note).HasColumnName("note").HasMaxLength(StoreOrderRequest.NoteMaxLength);
        request.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        request.Property(value => value.PricingMode)
            .HasColumnName("pricing_mode")
            .HasConversion<string>()
            .HasMaxLength(16);
        request.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(18, 2);
        request.Property(value => value.DiscountAmount).HasColumnName("discount_amount").HasPrecision(18, 2);
        request.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(18, 2);
        request.Property(value => value.Total).HasColumnName("total").HasPrecision(18, 2);

        request.OwnsOne(value => value.Consent, consent =>
        {
            consent.Property(value => value.At).HasColumnName("consent_at");
            consent.Property(value => value.Ip).HasColumnName("consent_ip").HasMaxLength(45);
            consent.Property(value => value.PolicyUrl).HasColumnName("consent_policy_url").HasMaxLength(500);
            consent.Property(value => value.PolicySetAt).HasColumnName("consent_policy_set_at");
        });
        request.Navigation(value => value.Consent).IsRequired();

        request.Property(value => value.ReceivedAt).HasColumnName("received_at");
        request.Property(value => value.LeaseMemberId).HasColumnName("lease_member_id");
        request.Property(value => value.LeaseUntil).HasColumnName("lease_until");
        request.Property(value => value.ConvertedBy).HasColumnName("converted_by");
        request.Property(value => value.ConvertedAt).HasColumnName("converted_at");
        request.Property(value => value.OrderId).HasColumnName("order_id");
        request.Property(value => value.CustomerId).HasColumnName("customer_id");
        request.Property(value => value.OrderNumber).HasColumnName("order_number").HasMaxLength(32);
        request.Property(value => value.OrderTotal).HasColumnName("order_total").HasPrecision(18, 2);
        request.Property(value => value.TotalDifference).HasColumnName("total_difference").HasPrecision(18, 2);
        request.Property(value => value.DiscardedBy).HasColumnName("discarded_by");
        request.Property(value => value.DiscardedAt).HasColumnName("discarded_at");
        request.Property(value => value.DiscardReason)
            .HasColumnName("discard_reason")
            .HasConversion<string>()
            .HasMaxLength(16);
        request.Property(value => value.DiscardNote)
            .HasColumnName("discard_note")
            .HasMaxLength(StoreOrderRequest.DiscardNoteMaxLength);
        request.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();

        // Calculadas desde otras columnas: no se mapean.
        request.Ignore(value => value.DisplayNumber);
        request.Ignore(value => value.HasScaleDiscount);

        request.HasIndex(value => new { value.TenantId, value.Number })
            .IsUnique()
            .HasDatabaseName("IX_order_requests_tenant_number");
        request.HasIndex(value => new { value.TenantId, value.ClientRequestId })
            .IsUnique()
            .HasDatabaseName("IX_order_requests_tenant_client_request");
        request.HasIndex(value => new { value.TenantId, value.Status, value.ReceivedAt })
            .HasDatabaseName("IX_order_requests_tenant_status_received");
        request.HasIndex(value => new { value.TenantId, value.ConvertedBy })
            .HasDatabaseName("IX_order_requests_tenant_converted_by");
        // IX_order_requests_tenant_billing_phone (tenant, billing_phone_e164) no se declara: EF no expresa un índice que cruce una columna
        // del dueño con una de un complex type, así que lo crea a mano InitialStore.
        // Un pedido sale de una sola solicitud; los que no vienen de la tienda no tienen fila acá.
        request.HasIndex(value => value.OrderId)
            .IsUnique()
            .HasFilter("order_id IS NOT NULL")
            .HasDatabaseName("IX_order_requests_order");

        ConfigureLines(request);
    }

    private static void ConfigurePlace(ComplexPropertyBuilder<StorePlace> place, string prefix)
    {
        place.Property(value => value.Country).HasColumnName($"{prefix}_country").HasMaxLength(2);
        place.Property(value => value.DepartmentId).HasColumnName($"{prefix}_department_id");
        place.Property(value => value.DepartmentName)
            .HasColumnName($"{prefix}_department_name")
            .HasMaxLength(StorePlace.NameMaxLength);
        place.Property(value => value.CityId).HasColumnName($"{prefix}_city_id");
        place.Property(value => value.CityName)
            .HasColumnName($"{prefix}_city_name")
            .HasMaxLength(StorePlace.NameMaxLength);
    }

    private static void ConfigureLines(EntityTypeBuilder<StoreOrderRequest> request)
    {
        request.OwnsMany(value => value.Lines, line =>
        {
            line.ToTable("order_request_lines", Schema);
            line.WithOwner().HasForeignKey("request_id");
            line.Property(value => value.Position).HasColumnName("position").ValueGeneratedNever();
            line.HasKey("request_id", nameof(StoreOrderRequestLine.Position));
            line.Property(value => value.VariantId).HasColumnName("variant_id");
            line.Property(value => value.ProductId).HasColumnName("product_id");
            line.Property(value => value.Name).HasColumnName("name").HasMaxLength(StoreOrderRequestLine.NameMaxLength);
            line.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(18, 2);
            line.Property(value => value.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 2);
            line.Property(value => value.DiscountPercentage).HasColumnName("discount_percentage").HasPrecision(5, 2);
            line.Property(value => value.DiscountOrigin)
                .HasColumnName("discount_origin")
                .HasMaxLength(StoreOrderRequestLine.DiscountOriginMaxLength);
            line.Property(value => value.DiscountedUnitPrice).HasColumnName("discounted_unit_price").HasPrecision(18, 2);
            line.Property(value => value.LineTotal).HasColumnName("line_total").HasPrecision(18, 2);
        });
        request.Navigation(value => value.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    // Una fila por tenant; el generador la crea con INSERT ... ON CONFLICT y la avanza con UPDATE ... RETURNING, así que el modelo
    // sólo la declara para que la migración cree la tabla.
    private static void ConfigureRequestCounter(ModelBuilder modelBuilder)
    {
        var counter = modelBuilder.Entity<StoreRequestCounter>();
        counter.ToTable("request_counters", Schema);
        counter.HasKey(value => value.TenantId);
        counter.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        counter.Property(value => value.NextValue).HasColumnName("next_value").HasColumnType("bigint");
    }

    // Copia de IdentityDbContext.ConfigureOutboxProjection: se escribe, pero la tabla no se migra desde acá. Las columnas NOT NULL
    // salen de InitialPlatform.
    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<OutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
    }
}

// Sólo existe para que EF cree la tabla; el contador se toca con SQL (StoreRequestNumberGenerator).
internal sealed class StoreRequestCounter
{
    public Guid TenantId { get; set; }

    public long NextValue { get; set; }
}
