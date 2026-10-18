using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>shopify</c> (spec 2026-10-15 §9): la configuración de cada tienda (<c>stores</c>, §6.1), el espejo de pedidos
/// (<c>orders</c>, §5.4) y la cola de entregas de webhook (<c>webhook_deliveries</c>, §5.1). Proyecta <c>audit.entries</c> sin ser
/// su dueño (<c>ExcludeFromMigrations</c>, H19), como Integrations y Messaging. Sin FKs a otros esquemas: conexión, cliente,
/// pedido, asesor, empresa y clasificación son referencias blandas.
/// </summary>
public sealed class ShopifyDbContext(DbContextOptions<ShopifyDbContext> options) : DbContext(options)
{
    public const string Schema = "shopify";

    /// <summary>§5.4: una fila por pedido y conexión. <c>ShopifyUnitOfWork</c> discrimina por este nombre.</summary>
    public const string OrderConnectionIndex = "IX_shopify_orders_connection_order";

    /// <summary>§5.1: el blanco del <c>ON CONFLICT (webhook_id) DO NOTHING</c>; un reintento de Shopify no crea otra fila.</summary>
    public const string DeliveryWebhookIdIndex = "IX_webhook_deliveries_webhook_id";

    /// <summary>La PK de la tienda: dos primeros contactos a la vez chocan acá.</summary>
    public const string StorePrimaryKey = "PK_stores";

    public DbSet<ShopifyStore> Stores => Set<ShopifyStore>();

    public DbSet<ShopifyOrderMirror> Orders => Set<ShopifyOrderMirror>();

    internal DbSet<ShopifyWebhookDeliveryRecord> Deliveries => Set<ShopifyWebhookDeliveryRecord>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal static string ImportStateCheck() =>
        "import_state IN (" + string.Join(",", Enum.GetNames<ShopifyImportState>().Select(name => $"'{name}'")) + ")";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureStore(modelBuilder);
        ConfigureOrder(modelBuilder);
        ConfigureDelivery(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureStore(ModelBuilder modelBuilder)
    {
        var store = modelBuilder.Entity<ShopifyStore>();
        store.ToTable("stores", Schema);
        store.HasKey(value => value.ConnectionId).HasName(StorePrimaryKey);
        store.Property(value => value.ConnectionId).HasColumnName("connection_id").ValueGeneratedNever();
        store.Property(value => value.TenantId).HasColumnName("tenant_id");
        store.Property(value => value.AdvisorMembershipId).HasColumnName("advisor_membership_id");
        store.OwnsOne(value => value.Billing, billing =>
        {
            billing.Property(value => value.CompanyId).HasColumnName("billing_company_id");
            billing.Property(value => value.BankName).HasColumnName("billing_bank_name").HasMaxLength(ShopifyBillingAccount.BankNameMaxLength);
            billing.Property(value => value.AccountNumber)
                .HasColumnName("billing_account_number")
                .HasMaxLength(ShopifyBillingAccount.AccountNumberMaxLength);
            billing.Property(value => value.Currency)
                .HasColumnName("billing_currency")
                .HasMaxLength(ShopifyBillingAccount.CurrencyLength)
                .IsFixedLength();
        });
        store.Property(value => value.DefaultClassificationId).HasColumnName("default_classification_id");
        store.Property(value => value.DocumentAttributeKey)
            .HasColumnName("document_attribute_key")
            .HasMaxLength(ShopifyStore.AttributeKeyMaxLength);
        store.Property(value => value.DocumentTypeAttributeKey)
            .HasColumnName("document_type_attribute_key")
            .HasMaxLength(ShopifyStore.AttributeKeyMaxLength);
        store.Property(value => value.DefaultIdentificationType)
            .HasColumnName("default_identification_type")
            .HasMaxLength(ShopifyStore.IdentificationTypeMaxLength);
        store.Property(value => value.LastReconciledAt).HasColumnName("last_reconciled_at");
        store.Property(value => value.ReconcileLeaseUntil).HasColumnName("reconcile_lease_until");
        store.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        store.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        store.Property(value => value.UpdatedBy).HasColumnName("updated_by");
        store.Ignore(value => value.IsConfigured);
        store.Property(value => value.ShopDomain).HasColumnName("shop_domain").HasMaxLength(ShopifyStore.ShopDomainMaxLength);
        store.HasIndex(value => value.TenantId).HasDatabaseName("IX_stores_tenant");
        // Ruling S16: shop/redact busca la tienda por dominio, sin tenant y sin la ruta de Integrations.
        store.HasIndex(value => value.ShopDomain).HasDatabaseName("IX_shopify_stores_shop_domain");
    }

    private static void ConfigureOrder(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<ShopifyOrderMirror>();
        order.ToTable("orders", Schema, table =>
            table.HasCheckConstraint("CK_shopify_orders_import_state", ImportStateCheck()));
        order.HasKey(value => value.Id);
        order.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        order.Property(value => value.TenantId).HasColumnName("tenant_id");
        order.Property(value => value.ConnectionId).HasColumnName("connection_id");
        order.Property(value => value.ShopifyOrderId).HasColumnName("shopify_order_id");
        order.Property(value => value.OrderName).HasColumnName("order_name").HasMaxLength(ShopifyOrderMirror.OrderNameMaxLength);
        order.Property(value => value.ShopifyCustomerId).HasColumnName("shopify_customer_id");
        order.Property(value => value.ShopifyCreatedAt).HasColumnName("shopify_created_at");
        order.Property(value => value.ShopifyUpdatedAt).HasColumnName("shopify_updated_at");
        order.Property(value => value.CancelledAt).HasColumnName("cancelled_at");
        order.Property(value => value.FinancialStatus)
            .HasColumnName("financial_status")
            .HasMaxLength(ShopifyOrderMirror.FinancialStatusMaxLength);
        order.Property(value => value.Currency)
            .HasColumnName("currency")
            .HasMaxLength(ShopifyOrderMirror.CurrencyLength)
            .IsFixedLength();
        order.Property(value => value.TotalPrice).HasColumnName("total_price").HasPrecision(14, 2);
        order.Property(value => value.TaxesIncluded).HasColumnName("taxes_included");
        order.Property(value => value.Payload).HasColumnName("payload").HasColumnType("jsonb");
        order.Property(value => value.ImportState).HasColumnName("import_state").HasConversion<string>().HasMaxLength(16);
        order.Property(value => value.WaitReason).HasColumnName("wait_reason").HasMaxLength(ShopifyOrderMirror.WaitReasonMaxLength);
        order.Property(value => value.WaitDetail).HasColumnName("wait_detail").HasColumnType("jsonb");
        order.Property(value => value.Warnings).HasColumnName("warnings").HasColumnType("jsonb");
        order.Property(value => value.Attempts).HasColumnName("attempts");
        order.Property(value => value.LastAttemptAt).HasColumnName("last_attempt_at");
        order.Property(value => value.ImportedAt).HasColumnName("imported_at");
        order.Property(value => value.LastError).HasColumnName("last_error").HasMaxLength(ShopifyOrderMirror.LastErrorMaxLength);
        order.Property(value => value.WarningsReviewedAt).HasColumnName("warnings_reviewed_at");
        // Referencias blandas a customers.customers y quotations.orders: otro módulo, sin FK.
        order.Property(value => value.QepCustomerId).HasColumnName("qep_customer_id");
        order.Property(value => value.QepOrderId).HasColumnName("qep_order_id");
        order.Property(value => value.TotalDifference).HasColumnName("total_difference").HasPrecision(14, 2);
        // Webhook, conciliación y reintento manual escriben la misma fila; sin token, el último en guardar pisaría al otro.
        order.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();

        order.HasIndex(value => new { value.ConnectionId, value.ShopifyOrderId }).IsUnique().HasDatabaseName(OrderConnectionIndex);
        // La bandeja: por estado, los más recientes primero.
        order.HasIndex(value => new { value.TenantId, value.ImportState, value.ShopifyCreatedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_shopify_orders_tenant_state_created");
        order.HasIndex(value => new { value.TenantId, value.QepOrderId }).HasDatabaseName("IX_shopify_orders_tenant_qep_order");
        // §6.2.1: el comprador ya visto en esta tienda.
        order.HasIndex(value => new { value.ConnectionId, value.ShopifyCustomerId }).HasDatabaseName("IX_shopify_orders_connection_customer");
    }

    private static void ConfigureDelivery(ModelBuilder modelBuilder)
    {
        var delivery = modelBuilder.Entity<ShopifyWebhookDeliveryRecord>();
        delivery.ToTable("webhook_deliveries", Schema);
        delivery.HasKey(value => value.Id);
        delivery.Property(value => value.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        delivery.Property(value => value.WebhookId).HasColumnName("webhook_id").HasMaxLength(ShopifyWebhookDeliveryRecord.WebhookIdMaxLength);
        delivery.Property(value => value.Topic).HasColumnName("topic").HasMaxLength(ShopifyWebhookDeliveryRecord.TopicMaxLength);
        delivery.Property(value => value.ShopDomain).HasColumnName("shop_domain").HasMaxLength(ShopifyWebhookDeliveryRecord.ShopDomainMaxLength);
        delivery.Property(value => value.ApiVersion).HasColumnName("api_version").HasMaxLength(ShopifyWebhookDeliveryRecord.ApiVersionMaxLength);
        delivery.Property(value => value.Payload).HasColumnName("payload").HasColumnType("jsonb");
        delivery.Property(value => value.ReceivedAt).HasColumnName("received_at");
        delivery.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        delivery.Property(value => value.LeaseUntil).HasColumnName("lease_until");
        delivery.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        delivery.Property(value => value.IgnoredReason)
            .HasColumnName("ignored_reason")
            .HasMaxLength(ShopifyWebhookDeliveryRecord.IgnoredReasonMaxLength);
        delivery.Property(value => value.LastError).HasColumnName("last_error").HasMaxLength(ShopifyWebhookDeliveryRecord.LastErrorMaxLength);
        delivery.HasIndex(value => value.WebhookId).IsUnique().HasDatabaseName(DeliveryWebhookIdIndex);
        // El worker toma las pendientes en orden de llegada (§5.2), como el de Messaging.
        delivery.HasIndex(value => value.Id).HasDatabaseName("IX_webhook_deliveries_pending").HasFilter("processed_at IS NULL");
        // shop/redact borra las entregas de una tienda (§5.5).
        delivery.HasIndex(value => value.ShopDomain).HasDatabaseName("IX_webhook_deliveries_shop_domain");
    }
}
