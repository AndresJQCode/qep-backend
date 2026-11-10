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

    // Sombra que el dominio no conoce: el orden de las monedas.
    internal const string CurrencyPositionColumn = "position";

    public DbSet<OnlineStore> Stores => Set<OnlineStore>();

    public DbSet<StoreDomain> Domains => Set<StoreDomain>();

    public DbSet<StoreOrderConsent> OrderConsents => Set<StoreOrderConsent>();

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
        ConfigureOrderConsent(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    // La posición de cada moneda se deriva del agregado y no tiene propiedad en el dominio: se escribe acá, justo antes de guardar,
    // para que ningún llamador tenga que acordarse de ella.
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

        // Sin FK a tenancy: los módulos no se cruzan por clave foránea; Application valida que sea un miembro activo.
        store.Property(value => value.AdvisorMembershipId).HasColumnName("advisor_membership_id");

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

    private static void ConfigureOrderConsent(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<StoreOrderConsent>();
        entity.ToTable("order_consents", Schema);
        entity.HasKey(value => value.OrderId).HasName("PK_order_consents");
        entity.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        entity.Property(value => value.TenantId).HasColumnName("tenant_id");
        entity.HasIndex(value => value.TenantId).HasDatabaseName("IX_order_consents_tenant");

        entity.OwnsOne(value => value.Consent, consent =>
        {
            consent.Property(value => value.At).HasColumnName("accepted_at");
            consent.Property(value => value.Ip).HasColumnName("visitor_ip").HasMaxLength(45);
            consent.Property(value => value.PolicyUrl).HasColumnName("policy_url").HasMaxLength(500);
            consent.Property(value => value.PolicySetAt).HasColumnName("policy_set_at");
        });
        entity.Navigation(value => value.Consent).IsRequired();
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
