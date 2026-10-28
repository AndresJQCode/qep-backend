using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>shopify</c> (spec 2026-10-15 §9): la configuración de cada tienda (<c>stores</c>, §6.1), el espejo de pedidos
/// (<c>orders</c>, §5.4), la cola de entregas de webhook (<c>webhook_deliveries</c>, §5.1) y las huellas de los sinónimos de
/// ciudad (<c>city_corrections</c>, spec 2026-10-26 §5) con sus trabajos de corrección (<c>city_correction_jobs</c>, D-C6) y el
/// inbox de sus consumidores (<c>inbox_messages</c>). Proyecta <c>audit.entries</c> y <c>platform.outbox_messages</c> sin ser
/// su dueño (<c>ExcludeFromMigrations</c>, H19), como Integrations, Messaging y Pipeline. Sin FKs a otros esquemas: conexión, cliente,
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

    /// <summary>Spec 2026-10-26: una huella por sinónimo y pedido o cliente. <c>ShopifyUnitOfWork</c> discrimina por este nombre.</summary>
    public const string CityCorrectionSynonymTargetIndex = "IX_city_corrections_synonym_target";

    /// <summary>Spec 2026-10-26 D-C6: un trabajo por mensaje, destino e id; el consumidor de eventos no siembra dos veces.</summary>
    public const string CityCorrectionJobMessageTargetIndex = "IX_city_correction_jobs_message_target";

    private const int EnumNameMaxLength = 16;

    // La lista de ciudades pedidas por un trabajo como uuid[] (el mismo par que Messaging usa para sus audiencias).
    private static readonly ValueConverter<IReadOnlyList<Guid>, Guid[]> GuidListConverter = new(list => list.ToArray(), array => array);

    private static readonly ValueComparer<IReadOnlyList<Guid>> GuidListComparer = new(
        (left, right) => left!.SequenceEqual(right!),
        list => list.Aggregate(0, (hash, id) => HashCode.Combine(hash, id)),
        list => list.ToArray());

    public DbSet<ShopifyStore> Stores => Set<ShopifyStore>();

    public DbSet<ShopifyOrderMirror> Orders => Set<ShopifyOrderMirror>();

    public DbSet<ShopifyCityCorrection> CityCorrections => Set<ShopifyCityCorrection>();

    public DbSet<ShopifyCityCorrectionJob> CityCorrectionJobs => Set<ShopifyCityCorrectionJob>();

    internal DbSet<ShopifyInboxMessage> Inbox => Set<ShopifyInboxMessage>();

    internal DbSet<ShopifyOutboxRecord> Outbox => Set<ShopifyOutboxRecord>();

    internal DbSet<ShopifyWebhookDeliveryRecord> Deliveries => Set<ShopifyWebhookDeliveryRecord>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal static string ImportStateCheck() => NamesCheck<ShopifyImportState>("import_state");

    private static string NamesCheck<TEnum>(string column)
        where TEnum : struct, Enum =>
        column + " IN (" + string.Join(",", Enum.GetNames<TEnum>().Select(name => $"'{name}'")) + ")";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureStore(modelBuilder);
        ConfigureOrder(modelBuilder);
        ConfigureDelivery(modelBuilder);
        ConfigureCityCorrection(modelBuilder);
        ConfigureCityCorrectionJob(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureCityCorrectionJob(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<ShopifyCityCorrectionJob>();
        job.ToTable("city_correction_jobs", Schema, table =>
        {
            table.HasCheckConstraint("CK_city_correction_jobs_kind", NamesCheck<ShopifyCityCorrectionJobKind>("kind"));
            table.HasCheckConstraint("CK_city_correction_jobs_target", NamesCheck<ShopifyCityCorrectionTarget>("target"));
            table.HasCheckConstraint("CK_city_correction_jobs_status", NamesCheck<ShopifyCityCorrectionJobStatus>("status"));
        });
        job.HasKey(value => value.Id);
        job.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        job.Property(value => value.TenantId).HasColumnName("tenant_id");
        // Referencias blandas: el sinónimo es de geography, el pedido de quotations, el cliente de customers y el mensaje del
        // outbox de plataforma. La huella sí es de este esquema, pero tampoco lleva FK: un trabajo terminado no frena nada.
        job.Property(value => value.SynonymId).HasColumnName("synonym_id");
        // Texto: lo nombran los CHECK y el lector de Geography (D-C3b), que cuenta pendientes y fallidos por nombre.
        job.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        job.Property(value => value.Target).HasColumnName("target").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        job.Property(value => value.TargetId).HasColumnName("target_id");
        job.Property(value => value.MirrorOrderId).HasColumnName("mirror_order_id");
        job.Property(value => value.CorrectionId).HasColumnName("correction_id");
        job.Property(value => value.OperatorSubjectId).HasColumnName("operator_subject_id");
        job.Property(value => value.SourceMessageId).HasColumnName("source_message_id");
        job.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        job.Property(value => value.SkipReason).HasColumnName("skip_reason").HasMaxLength(ShopifyCityCorrectionJob.SkipReasonMaxLength);
        // Los nombres van a mano: la cola del worker los escribe en SQL crudo.
        job.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        job.Property(value => value.LeaseUntil).HasColumnName("lease_until");
        job.Property(value => value.LastError).HasColumnName("last_error").HasMaxLength(ShopifyCityCorrectionJob.LastErrorMaxLength);
        job.Property(value => value.RequestedCityIds)
            .HasColumnName("requested_city_ids")
            .HasColumnType("uuid[]")
            .HasDefaultValueSql("'{}'::uuid[]")
            .HasConversion(GuidListConverter, GuidListComparer);
        job.Property(value => value.CreatedAt).HasColumnName("created_at");
        job.Property(value => value.ProcessedAt).HasColumnName("processed_at");

        job.HasIndex(value => new { value.SourceMessageId, value.Target, value.TargetId })
            .IsUnique()
            .HasFilter("source_message_id IS NOT NULL")
            .HasDatabaseName(CityCorrectionJobMessageTargetIndex);
        // El worker busca los pendientes con el lease vencido; la consola cuenta pendientes y fallidos por sinónimo.
        job.HasIndex(value => new { value.Status, value.LeaseUntil }).HasDatabaseName("IX_city_correction_jobs_status_lease");
        job.HasIndex(value => new { value.SynonymId, value.Status }).HasDatabaseName("IX_city_correction_jobs_synonym_status");
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<ShopifyInboxMessage>();
        inbox.ToTable("inbox_messages", Schema);
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        // Los nombres van a mano porque ShopifyInboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<ShopifyOutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
    }

    private static void ConfigureCityCorrection(ModelBuilder modelBuilder)
    {
        var correction = modelBuilder.Entity<ShopifyCityCorrection>();
        correction.ToTable("city_corrections", Schema, table =>
        {
            table.HasCheckConstraint("CK_city_corrections_origin", NamesCheck<ShopifyCityCorrectionOrigin>("origin"));
            table.HasCheckConstraint("CK_city_corrections_target", NamesCheck<ShopifyCityCorrectionTarget>("target"));
        });
        correction.HasKey(value => value.Id);
        correction.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        correction.Property(value => value.TenantId).HasColumnName("tenant_id");
        // Referencias blandas: el sinónimo es de geography, el pedido de quotations y el cliente de customers. Sin FK.
        correction.Property(value => value.SynonymId).HasColumnName("synonym_id");
        // Texto y no el entero por defecto: lo nombran los CHECK y el impacto de Geography, que lee esta tabla por nombre (D-C3b).
        correction.Property(value => value.Origin).HasColumnName("origin").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        correction.Property(value => value.Target).HasColumnName("target").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        correction.Property(value => value.TargetId).HasColumnName("target_id");
        correction.Property(value => value.MirrorOrderId).HasColumnName("mirror_order_id");
        correction.Property(value => value.DepartmentId).HasColumnName("department_id");
        correction.Property(value => value.CityId).HasColumnName("city_id");
        correction.Property(value => value.PreviousCityId).HasColumnName("previous_city_id");
        correction.Property(value => value.PartyName).HasColumnName("party_name").HasMaxLength(ShopifyCityCorrection.PartyNameMaxLength);
        correction.Property(value => value.PartyPhone).HasColumnName("party_phone").HasMaxLength(ShopifyCityCorrection.PartyPhoneMaxLength);
        correction.Property(value => value.PartyEmail).HasColumnName("party_email").HasMaxLength(ShopifyCityCorrection.PartyEmailMaxLength);
        correction.Property(value => value.PartyAddress)
            .HasColumnName("party_address")
            .HasMaxLength(ShopifyCityCorrection.PartyAddressMaxLength);
        correction.Property(value => value.CustomerVersion).HasColumnName("customer_version");
        correction.Property(value => value.AppliedAt).HasColumnName("applied_at");
        correction.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        correction.Ignore(value => value.Party);

        correction.HasIndex(value => new { value.SynonymId, value.Target, value.TargetId })
            .IsUnique()
            .HasDatabaseName(CityCorrectionSynonymTargetIndex);
        // Lo que corrigió un sinónimo en un pedido o cliente del tenant (impacto y auditoría).
        correction.HasIndex(value => new { value.TenantId, value.Target, value.TargetId })
            .HasDatabaseName("IX_city_corrections_tenant_target");
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
        // shop/redact busca la tienda por dominio, sin tenant y sin la ruta de Integrations.
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
