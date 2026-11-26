using System.Text.Json;
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

    /// <summary>Spec 2026-11-13: una asociación por conexión y título. <c>ShopifyUnitOfWork</c> discrimina por este nombre y
    /// responde <c>shopify.custom_line.already_mapped</c> (409).</summary>
    public const string CustomLineMappingConnectionTitleIndex = "IX_custom_line_mappings_connection_title";

    /// <summary>Spec 2026-11-14 §5: un solo envío <c>Pending</c> por pedido. Dos correcciones a la vez sobre una fila sin pendiente
    /// chocan acá; <see cref="ShopifyUnitOfWork"/> lo traduce por nombre a <c>concurrency.conflict</c>.</summary>
    public const string OrderPushMirrorPendingIndex = "IX_order_pushes_mirror_pending";

    /// <summary>Spec 2026-11-17 §3: una variante de Shopify se vincula una vez por tienda. Frena el duplicado entre el webhook y la
    /// importación; <see cref="ShopifyUnitOfWork"/> lo traduce por nombre a <c>concurrency.conflict</c>.</summary>
    public const string ProductLinkShopifyVariantIndex = "IX_product_links_connection_shopify_variant";

    /// <summary>Spec 2026-11-17 §3: una variante de QEP tiene a lo sumo un vínculo por tienda.</summary>
    public const string ProductLinkVariantIndex = "IX_product_links_connection_variant";

    public const string ProductLinkShopifyProductIndex = "IX_product_links_connection_shopify_product";

    public const string ProductLinkProductIndex = "IX_product_links_connection_product";

    /// <summary>Spec 2026-11-17 §5.2: una importación viva por tienda; se traduce a <c>shopify.products.import_running</c>.</summary>
    public const string ProductImportLiveIndex = "IX_product_imports_connection_live";

    public const string ProductImportStatusLeaseIndex = "IX_product_imports_status_lease";

    public const string ProductImportConnectionCreatedIndex = "IX_product_imports_connection_created";

    public const string ProductImportIssueImportIndex = "IX_product_import_issues_import";

    // Los grupos de un envío como text[] por nombre, como edit_payment_statuses de la tienda.
    private static readonly ValueConverter<IReadOnlyList<ShopifyPushGroup>, string[]> PushGroupsConverter = new(
        groups => groups.Select(group => group.ToString()).ToArray(),
        names => names.Select(name => Enum.Parse<ShopifyPushGroup>(name)).ToArray());

    private static readonly ValueComparer<IReadOnlyList<ShopifyPushGroup>> PushGroupsComparer = new(
        (left, right) => left!.SequenceEqual(right!),
        groups => groups.Aggregate(0, (hash, group) => HashCode.Combine(hash, group)),
        groups => groups.ToArray());

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

    public DbSet<ShopifyCustomLineMapping> CustomLineMappings => Set<ShopifyCustomLineMapping>();

    public DbSet<ShopifyOrderEdit> OrderEdits => Set<ShopifyOrderEdit>();

    public DbSet<ShopifyOrderPush> OrderPushes => Set<ShopifyOrderPush>();

    public DbSet<ShopifyProductLink> ProductLinks => Set<ShopifyProductLink>();

    public DbSet<ShopifyProductImport> ProductImports => Set<ShopifyProductImport>();

    public DbSet<ShopifyProductImportIssue> ProductImportIssues => Set<ShopifyProductImportIssue>();

    internal DbSet<ShopifyInboxMessage> Inbox => Set<ShopifyInboxMessage>();

    internal DbSet<ShopifyOutboxRecord> Outbox => Set<ShopifyOutboxRecord>();

    internal DbSet<ShopifyWebhookDeliveryRecord> Deliveries => Set<ShopifyWebhookDeliveryRecord>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal static string ImportStateCheck() => NamesCheck<ShopifyImportState>("import_state");

    private static readonly JsonSerializerOptions SetupJson = new(JsonSerializerDefaults.Web);

    // Un record del asistente como jsonb. Nulo en la base es nulo en el modelo; EF no llama al conversor con null.
    private static ValueConverter<T?, string?> JsonConverterFor<T>()
        where T : class =>
        new(
            value => value == null ? null : JsonSerializer.Serialize(value, SetupJson),
            json => json == null ? null : JsonSerializer.Deserialize<T>(json, SetupJson));

    private static ValueComparer<T?> JsonComparerFor<T>()
        where T : class =>
        new(
            (left, right) => JsonSerializer.Serialize(left, SetupJson) == JsonSerializer.Serialize(right, SetupJson),
            value => value == null ? 0 : JsonSerializer.Serialize(value, SetupJson).GetHashCode(StringComparison.Ordinal),
            value => value == null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SetupJson), SetupJson));

    private static string NamesCheck<TEnum>(string column)
        where TEnum : struct, Enum =>
        column + " IN (" + string.Join(",", Enum.GetNames<TEnum>().Select(name => $"'{name}'")) + ")";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureStore(modelBuilder);
        ConfigureOrder(modelBuilder);
        ConfigureOrderEdit(modelBuilder);
        ConfigureOrderPush(modelBuilder);
        ConfigureDelivery(modelBuilder);
        ConfigureCityCorrection(modelBuilder);
        ConfigureCityCorrectionJob(modelBuilder);
        ConfigureCustomLineMapping(modelBuilder);
        ConfigureProductLink(modelBuilder);
        ConfigureProductImport(modelBuilder);
        ConfigureProductImportIssue(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    // Contrato con lulo-crm-migrator, que siembra esta tabla por SQL: los nombres y los tipos de las columnas no se cambian.
    private static void ConfigureCustomLineMapping(ModelBuilder modelBuilder)
    {
        var mapping = modelBuilder.Entity<ShopifyCustomLineMapping>();
        mapping.ToTable("custom_line_mappings", Schema);
        mapping.HasKey(value => value.Id);
        mapping.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        mapping.Property(value => value.TenantId).HasColumnName("tenant_id");
        mapping.Property(value => value.ConnectionId).HasColumnName("connection_id");
        mapping.Property(value => value.Title).HasColumnName("title").HasMaxLength(ShopifyCustomLineMapping.TitleMaxLength);
        // Referencia blanda a catalog.product_variants: otro esquema, sin FK.
        mapping.Property(value => value.VariantId).HasColumnName("variant_id");
        mapping.Property(value => value.CreatedAt).HasColumnName("created_at");
        mapping.Property(value => value.CreatedBy).HasColumnName("created_by");

        mapping.HasIndex(value => new { value.ConnectionId, value.Title })
            .IsUnique()
            .HasDatabaseName(CustomLineMappingConnectionTitleIndex);
    }

    // Spec 2026-11-17 §3. Producto y variante de QEP son referencias blandas a catalog (otro esquema, sin FK).
    private static void ConfigureProductLink(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<ShopifyProductLink>();
        link.ToTable("product_links", Schema, table =>
            table.HasCheckConstraint("CK_product_links_origin", NamesCheck<ShopifyProductLinkOrigin>("origin")));
        link.HasKey(value => value.Id);
        link.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        link.Property(value => value.TenantId).HasColumnName("tenant_id");
        link.Property(value => value.ConnectionId).HasColumnName("connection_id");
        link.Property(value => value.ShopifyProductId).HasColumnName("shopify_product_id");
        link.Property(value => value.ShopifyVariantId).HasColumnName("shopify_variant_id");
        link.Property(value => value.ProductId).HasColumnName("product_id");
        link.Property(value => value.VariantId).HasColumnName("variant_id");
        link.Property(value => value.Origin).HasColumnName("origin").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        link.Property(value => value.ShopifyUpdatedAt).HasColumnName("shopify_updated_at");
        link.Property(value => value.ShopifyImageId).HasColumnName("shopify_image_id");
        link.Property(value => value.ImageFileId).HasColumnName("image_file_id");
        link.Property(value => value.CreatedAt).HasColumnName("created_at");
        link.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        link.HasIndex(value => new { value.ConnectionId, value.ShopifyVariantId }).IsUnique().HasDatabaseName(ProductLinkShopifyVariantIndex);
        link.HasIndex(value => new { value.ConnectionId, value.VariantId }).IsUnique().HasDatabaseName(ProductLinkVariantIndex);
        link.HasIndex(value => new { value.ConnectionId, value.ShopifyProductId }).HasDatabaseName(ProductLinkShopifyProductIndex);
        link.HasIndex(value => new { value.ConnectionId, value.ProductId }).HasDatabaseName(ProductLinkProductIndex);
    }

    // Spec 2026-11-17 §5.2. Los nombres van a mano: el worker reclama y cierra por SQL crudo (status, lease_until, attempts).
    private static void ConfigureProductImport(ModelBuilder modelBuilder)
    {
        var import = modelBuilder.Entity<ShopifyProductImport>();
        import.ToTable("product_imports", Schema, table =>
        {
            table.HasCheckConstraint("CK_product_imports_mode", NamesCheck<ShopifyProductImportMode>("mode"));
            table.HasCheckConstraint("CK_product_imports_status", NamesCheck<ShopifyProductImportStatus>("status"));
        });
        import.HasKey(value => value.Id);
        import.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        import.Property(value => value.TenantId).HasColumnName("tenant_id");
        import.Property(value => value.ConnectionId).HasColumnName("connection_id");
        import.Property(value => value.Mode).HasColumnName("mode").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        import.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        import.Property(value => value.Total).HasColumnName("total");
        import.Property(value => value.Done).HasColumnName("done");
        import.Property(value => value.Created).HasColumnName("created");
        import.Property(value => value.Matched).HasColumnName("matched");
        import.Property(value => value.WithoutSku).HasColumnName("without_sku");
        import.Property(value => value.Skipped).HasColumnName("skipped");
        import.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(ShopifyProductCodes.CodeMaxLength);
        // Membresía y sujeto: referencias blandas a tenancy e identity.
        import.Property(value => value.RequestedBy).HasColumnName("requested_by");
        import.Property(value => value.RequestedBySubjectId).HasColumnName("requested_by_subject_id");
        import.Property(value => value.Cursor).HasColumnName("cursor").HasMaxLength(ShopifyProductImport.CursorMaxLength);
        import.Property(value => value.LeaseUntil).HasColumnName("lease_until");
        import.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        import.Property(value => value.CreatedAt).HasColumnName("created_at");
        import.Property(value => value.FinishedAt).HasColumnName("finished_at");
        import.Ignore(value => value.IsLive);

        import.HasIndex(value => value.ConnectionId)
            .IsUnique()
            .HasFilter("status IN ('Pending','Running')")
            .HasDatabaseName(ProductImportLiveIndex);
        import.HasIndex(value => new { value.Status, value.LeaseUntil }).HasDatabaseName(ProductImportStatusLeaseIndex);
        import.HasIndex(value => new { value.ConnectionId, value.CreatedAt }).HasDatabaseName(ProductImportConnectionCreatedIndex);
    }

    private static void ConfigureProductImportIssue(ModelBuilder modelBuilder)
    {
        var issue = modelBuilder.Entity<ShopifyProductImportIssue>();
        issue.ToTable("product_import_issues", Schema);
        issue.HasKey(value => value.Id);
        issue.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        issue.Property(value => value.ImportId).HasColumnName("import_id");
        issue.Property(value => value.ShopifyProductId).HasColumnName("shopify_product_id");
        issue.Property(value => value.Title).HasColumnName("title").HasMaxLength(ShopifyProductImportIssue.TitleMaxLength);
        issue.Property(value => value.Code).HasColumnName("code").HasMaxLength(ShopifyProductCodes.CodeMaxLength);
        issue.Property(value => value.CreatedAt).HasColumnName("created_at");

        // Borrar la importación se lleva sus issues.
        issue.HasOne<ShopifyProductImport>()
            .WithMany()
            .HasForeignKey(value => value.ImportId)
            .OnDelete(DeleteBehavior.Cascade);
        issue.HasIndex(value => new { value.ImportId, value.CreatedAt }).HasDatabaseName(ProductImportIssueImportIndex);
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
        correction.Property(value => value.PartyPostalCode)
            .HasColumnName("postal_code")
            .HasMaxLength(ShopifyCityCorrection.PartyPostalCodeMaxLength);
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
        // Spec 2026-10-21: el mismo rango que ShopifyStore.Configure; nulo es sin historial.
        store.ToTable("stores", Schema, table => table.HasCheckConstraint(
            "CK_stores_history_days",
            $"history_days IS NULL OR history_days BETWEEN 1 AND {ShopifyStore.HistoryDaysMax}"));
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
        store.Property(value => value.HistoryDays).HasColumnName("history_days");
        // Spec 2026-11-14: por nombre, como el resto de los enums del esquema. El default de las dos columnas es el de una tienda
        // nueva y llena las filas que existían antes de la migración; el sentinel fuera de rango hace que EF mande siempre el valor.
        store.Property(value => value.EditSync)
            .HasColumnName("edit_sync")
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(ShopifyEditSync.Manual)
            .HasSentinel((ShopifyEditSync)(-1));
        store.Property(value => value.EditPaymentStatuses)
            .HasColumnName("edit_payment_statuses")
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{FullPaymentReceived,PartialPaymentReceived,PaymentPending,PendingVerification}'::text[]")
            .IsRequired();
        store.Property(value => value.LastReconciledAt).HasColumnName("last_reconciled_at");
        store.Property(value => value.ReconcileLeaseUntil).HasColumnName("reconcile_lease_until");
        // Spec 2026-11-17 §5.2: nula en las tiendas de antes; los webhooks de productos se ignoran hasta la primera importación.
        store.Property(value => value.ProductsSyncedAt).HasColumnName("products_synced_at");
        // Spec 2026-11-17 (onboarding) §5 y §7 H7: el estado del asistente, nulo en las tiendas de antes. Los dos jsonb van en
        // camelCase (JsonSerializerDefaults.Web) y se comparan por su serialización: son records con listas adentro.
        store.Property(value => value.ProductsCoverage)
            .HasColumnName("products_coverage")
            .HasColumnType("jsonb")
            .HasConversion(JsonConverterFor<ShopifyProductsCoverage>(), JsonComparerFor<ShopifyProductsCoverage>());
        store.Property(value => value.ProductsCoverageAcceptedAt).HasColumnName("products_coverage_accepted_at");
        store.Property(value => value.SetupHistoryRanAt).HasColumnName("setup_history_ran_at");
        store.Property(value => value.SetupHistorySummary)
            .HasColumnName("setup_history_summary")
            .HasColumnType("jsonb")
            .HasConversion(JsonConverterFor<ShopifySetupHistorySummary>(), JsonComparerFor<ShopifySetupHistorySummary>());
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
        // Spec 2026-11-14 §2 y §5: la versión aplicada, la proyección y los atributos de QEP. Nulas en las filas de antes (sin backfill).
        order.Property(value => value.AppliedPayload).HasColumnName("applied_payload").HasColumnType("jsonb");
        order.Property(value => value.AppliedProjection).HasColumnName("applied_projection").HasColumnType("jsonb");
        order.Property(value => value.QepAttributes).HasColumnName("qep_attributes").HasColumnType("jsonb");
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

    // Spec 2026-11-14 §3 (el spec la llama order_changes; plan D1). Sin índice único: la concurrencia entre cambios la da la
    // versión de la fila del espejo, que crear o reemplazar un cambio siempre toca (D10).
    private static void ConfigureOrderEdit(ModelBuilder modelBuilder)
    {
        var edit = modelBuilder.Entity<ShopifyOrderEdit>();
        edit.ToTable("order_edits", Schema, table =>
            table.HasCheckConstraint("CK_order_edits_status", NamesCheck<ShopifyOrderEditStatus>("status")));
        edit.HasKey(value => value.Id);
        edit.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        edit.Property(value => value.TenantId).HasColumnName("tenant_id");
        edit.Property(value => value.ConnectionId).HasColumnName("connection_id");
        edit.Property(value => value.MirrorOrderId).HasColumnName("mirror_order_id");
        // Referencia blanda a quotations.orders: otro módulo, sin FK.
        edit.Property(value => value.QepOrderId).HasColumnName("qep_order_id");
        edit.Property(value => value.BaseUpdatedAt).HasColumnName("base_updated_at");
        edit.Property(value => value.SnapshotUpdatedAt).HasColumnName("snapshot_updated_at");
        edit.Property(value => value.Payload).HasColumnName("payload").HasColumnType("jsonb");
        edit.Property(value => value.Groups).HasColumnName("groups").HasColumnType("jsonb");
        edit.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        edit.Property(value => value.StatusReason).HasColumnName("status_reason").HasMaxLength(ShopifyOrderEdit.StatusReasonMaxLength);
        edit.Property(value => value.CreatedAt).HasColumnName("created_at");
        edit.Property(value => value.DecidedAt).HasColumnName("decided_at");
        edit.Property(value => value.DecidedBy).HasColumnName("decided_by");
        edit.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        edit.Ignore(value => value.IsOpen);

        // Borrar la fila del espejo (shop/redact, borrar la tienda) se lleva sus cambios.
        edit.HasOne<ShopifyOrderMirror>()
            .WithMany()
            .HasForeignKey(value => value.MirrorOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        edit.HasIndex(value => new { value.MirrorOrderId, value.Status }).HasDatabaseName("IX_order_edits_mirror_status");
        edit.HasIndex(value => new { value.TenantId, value.Status, value.CreatedAt }).HasDatabaseName("IX_order_edits_tenant_status_created");
    }

    // Spec 2026-11-14 §5. Los nombres van a mano: la cola del worker reclama y cierra por SQL crudo (attempts, lease_until,
    // updated_at, status, failure_code).
    private static void ConfigureOrderPush(ModelBuilder modelBuilder)
    {
        var push = modelBuilder.Entity<ShopifyOrderPush>();
        push.ToTable("order_pushes", Schema, table =>
            table.HasCheckConstraint("CK_order_pushes_status", NamesCheck<ShopifyOrderPushStatus>("status")));
        push.HasKey(value => value.Id);
        push.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        push.Property(value => value.TenantId).HasColumnName("tenant_id");
        push.Property(value => value.ConnectionId).HasColumnName("connection_id");
        push.Property(value => value.MirrorOrderId).HasColumnName("mirror_order_id");
        // Referencia blanda a quotations.orders: otro módulo, sin FK.
        push.Property(value => value.QepOrderId).HasColumnName("qep_order_id");
        push.Property(value => value.Groups)
            .HasColumnName("groups")
            .HasColumnType("text[]")
            .HasConversion(PushGroupsConverter, PushGroupsComparer)
            .IsRequired();
        push.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(EnumNameMaxLength);
        push.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        push.Property(value => value.LeaseUntil).HasColumnName("lease_until");
        push.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(ShopifyOrderPush.FailureCodeMaxLength);
        push.Property(value => value.CreatedAt).HasColumnName("created_at");
        push.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        push.Property(value => value.SentAt).HasColumnName("sent_at");
        push.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();

        // Borrar la fila del espejo (shop/redact, borrar la tienda) se lleva sus envíos.
        push.HasOne<ShopifyOrderMirror>()
            .WithMany()
            .HasForeignKey(value => value.MirrorOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        // Un solo envío abierto por pedido: pendiente o retenido (owner 2026-11-14). El nombre del índice se conserva porque
        // ShopifyUnitOfWork lo traduce por nombre.
        push.HasIndex(value => value.MirrorOrderId)
            .IsUnique()
            .HasFilter("status IN ('Pending','Held')")
            .HasDatabaseName(OrderPushMirrorPendingIndex);
        // El worker busca los pendientes con el lease vencido; el detalle del pedido lee el último envío de su fila.
        push.HasIndex(value => new { value.Status, value.LeaseUntil }).HasDatabaseName("IX_order_pushes_status_lease");
        push.HasIndex(value => new { value.MirrorOrderId, value.CreatedAt }).HasDatabaseName("IX_order_pushes_mirror_created");
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
