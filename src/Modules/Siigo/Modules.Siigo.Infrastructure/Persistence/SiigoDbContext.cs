using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>siigo</c> (spec 2026-11-18 §5.1). Todavía sin tablas propias: la configuración por conexión, los vínculos y las facturas
/// llegan con la migración <c>InitialSiigo</c>. Proyecta <c>audit.entries</c> y <c>platform.outbox_messages</c> sin ser su dueño
/// (<c>ExcludeFromMigrations</c>, H19), como Shopify. Sin FKs a otros esquemas: conexión, pedido, cliente, producto y empresa son
/// referencias blandas.
/// </summary>
public sealed class SiigoDbContext(DbContextOptions<SiigoDbContext> options) : DbContext(options)
{
    public const string Schema = "siigo";

    /// <summary>Spec 2026-11-18 §5.2: una empresa de QEP factura con una sola conexión de Siigo por tenant. <c>SiigoUnitOfWork</c> lo
    /// traduce por nombre a <c>409 siigo.settings.company_taken</c>.</summary>
    public const string SettingsTenantCompanyIndex = "IX_settings_tenant_company";

    /// <summary>Spec 2026-11-18 §5.6: una factura vigente por pedido y tipo (todo menos <c>Rejected</c>, <c>Failed</c> y
    /// <c>Annulled</c>). El candado del pedido da la respuesta amable; este índice cierra la carrera. <c>SiigoUnitOfWork</c> lo traduce
    /// por nombre a <c>409 siigo.invoice.already_exists</c>.</summary>
    public const string InvoicesTenantOrderKindIndex = "IX_invoices_tenant_order_kind";

    /// <summary>Spec 2026-11-24 §1: un pedido está a lo sumo en un ítem sin terminar (<c>Pending</c> o <c>Sending</c>). El handler
    /// consulta antes para decir cuáles chocan; este índice cierra la carrera. <c>SiigoUnitOfWork</c> lo traduce por nombre a
    /// <c>409 siigo.invoice_batch.order_in_progress</c>.</summary>
    internal const string InvoiceBatchItemsTenantOrderOpenIndex = "IX_invoice_batch_items_tenant_order_open";

    internal DbSet<SiigoSettings> Settings => Set<SiigoSettings>();

    internal DbSet<SiigoInvoice> Invoices => Set<SiigoInvoice>();

    internal DbSet<SiigoInvoiceBatch> InvoiceBatches => Set<SiigoInvoiceBatch>();

    internal DbSet<SiigoInvoiceBatchItem> InvoiceBatchItems => Set<SiigoInvoiceBatchItem>();

    internal DbSet<SiigoVariantLink> VariantLinks => Set<SiigoVariantLink>();

    internal DbSet<SiigoTaxLink> TaxLinks => Set<SiigoTaxLink>();

    internal DbSet<SiigoWithholdingLink> WithholdingLinks => Set<SiigoWithholdingLink>();

    internal DbSet<SiigoPaymentLink> PaymentLinks => Set<SiigoPaymentLink>();

    internal DbSet<SiigoCallFailure> CallFailures => Set<SiigoCallFailure>();

    // Proyecciones de escritura (ExcludeFromMigrations): Audit es dueño de audit.entries y Tenancy de platform.outbox_messages.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<SiigoOutboxMessage> Outbox => Set<SiigoOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureOutboxProjection(modelBuilder);
        ConfigureSettings(modelBuilder);
        ConfigureVariantLink(modelBuilder);
        ConfigureTaxLink(modelBuilder);
        ConfigureSellerLink(modelBuilder);
        ConfigureWithholdingLink(modelBuilder);
        ConfigurePaymentLink(modelBuilder);
        ConfigureInvoice(modelBuilder);
        ConfigureCallFailure(modelBuilder);
        ConfigureInvoiceBatch(modelBuilder);
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<SiigoSettings>();
        settings.ToTable("settings", Schema, table => table.HasCheckConstraint(
            "CK_settings_credit_days", $"credit_days IS NULL OR credit_days BETWEEN 1 AND {SiigoSettings.CreditDaysMax}"));
        settings.HasKey(value => value.ConnectionId).HasName("PK_settings");
        settings.Property(value => value.ConnectionId).HasColumnName("connection_id").ValueGeneratedNever();
        settings.Property(value => value.TenantId).HasColumnName("tenant_id");
        settings.Property(value => value.CompanyId).HasColumnName("company_id");
        settings.Property(value => value.DocumentTypeId).HasColumnName("document_type_id").HasMaxLength(SiigoSettings.IdMaxLength);
        settings.Property(value => value.CostCenterId).HasColumnName("cost_center_id").HasMaxLength(SiigoSettings.IdMaxLength);
        settings.Property(value => value.WarehouseId).HasColumnName("warehouse_id").HasMaxLength(SiigoSettings.IdMaxLength);
        // §5.2: un false explícito en el modelo y en la columna. Sin cuenta de pruebas, una factura emitida no se borra.
        settings.Property(value => value.SendToDian).HasColumnName("send_to_dian").HasDefaultValue(false);
        settings.Property(value => value.SendMail).HasColumnName("send_mail").HasDefaultValue(false);
        settings.Property(value => value.FreightSeparateInvoice).HasColumnName("freight_separate_invoice").HasDefaultValue(false);
        settings.Property(value => value.PriceMode)
            .HasColumnName("price_mode")
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired()
            .HasDefaultValue(SiigoPriceMode.BaseWithDiscount);
        settings.Property(value => value.FreightProductCode)
            .HasColumnName("freight_product_code")
            .HasMaxLength(SiigoSettings.FreightProductCodeMaxLength);
        settings.Property(value => value.CreditPaymentTypeId)
            .HasColumnName("credit_payment_type_id")
            .HasMaxLength(SiigoSettings.IdMaxLength);
        settings.Property(value => value.CreditDays).HasColumnName("credit_days");
        settings.Property(value => value.CashOnDeliveryPaymentTypeId)
            .HasColumnName("cash_on_delivery_payment_type_id")
            .HasMaxLength(SiigoSettings.IdMaxLength);
        settings.Property(value => value.DefaultPaymentTypeId)
            .HasColumnName("default_payment_type_id")
            .HasMaxLength(SiigoSettings.IdMaxLength);
        settings.Property(value => value.Version).HasColumnName("version").HasDefaultValue(0L).IsConcurrencyToken();
        // Una empresa, una conexión por tenant. Parcial: las conexiones que todavía no eligieron empresa no chocan entre sí.
        settings.HasIndex(value => new { value.TenantId, value.CompanyId })
            .IsUnique()
            .HasDatabaseName(SettingsTenantCompanyIndex)
            .HasFilter("company_id IS NOT NULL");

        // Los hijos se borran con la configuración. Las colecciones se leen por su campo privado.
        settings.HasMany(value => value.VariantLinks).WithOne().HasForeignKey(link => link.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        settings.HasMany(value => value.TaxLinks).WithOne().HasForeignKey(link => link.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        settings.HasMany(value => value.WithholdingLinks).WithOne().HasForeignKey(link => link.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        settings.HasMany(value => value.PaymentLinks).WithOne().HasForeignKey(link => link.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        settings.HasMany(value => value.SellerLinks).WithOne().HasForeignKey(link => link.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        settings.Navigation(value => value.SellerLinks).UsePropertyAccessMode(PropertyAccessMode.Field);
        settings.Property(value => value.SellersAutoLinkedAt).HasColumnName("sellers_auto_linked_at");
        settings.Property(value => value.ProductsAutoLinkedAt).HasColumnName("products_auto_linked_at");
        settings.Navigation(value => value.VariantLinks).UsePropertyAccessMode(PropertyAccessMode.Field);
        settings.Navigation(value => value.TaxLinks).UsePropertyAccessMode(PropertyAccessMode.Field);
        settings.Navigation(value => value.WithholdingLinks).UsePropertyAccessMode(PropertyAccessMode.Field);
        settings.Navigation(value => value.PaymentLinks).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureVariantLink(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<SiigoVariantLink>();
        link.ToTable("variant_links", Schema);
        // La llave natural es la unicidad que pide §5.2: (connection_id, variant_id).
        link.HasKey(value => new { value.ConnectionId, value.VariantId }).HasName("PK_variant_links");
        link.Property(value => value.ConnectionId).HasColumnName("connection_id");
        link.Property(value => value.VariantId).HasColumnName("variant_id");
        link.Property(value => value.TenantId).HasColumnName("tenant_id");
        link.Property(value => value.SiigoProductCode).HasColumnName("siigo_product_code").HasMaxLength(SiigoVariantLink.CodeMaxLength);
        link.Property(value => value.SiigoProductId).HasColumnName("siigo_product_id").HasMaxLength(SiigoVariantLink.ProductIdMaxLength);
        link.Property(value => value.SiigoProductName).HasColumnName("siigo_product_name").HasMaxLength(SiigoVariantLink.NameMaxLength);
        link.Property(value => value.LinkedBy).HasColumnName("linked_by");
        link.Property(value => value.LinkedAt).HasColumnName("linked_at");
        link.Property(value => value.Origin).HasColumnName("origin").HasConversion<string>().HasMaxLength(16);
        link.HasIndex(value => new { value.TenantId, value.ConnectionId }).HasDatabaseName("IX_variant_links_tenant_connection");
    }

    private static void ConfigureSellerLink(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<SiigoSellerLink>();
        link.ToTable("seller_links", Schema);
        link.HasKey(value => new { value.ConnectionId, value.MembershipId }).HasName("PK_seller_links");
        link.Property(value => value.ConnectionId).HasColumnName("connection_id");
        link.Property(value => value.MembershipId).HasColumnName("membership_id");
        link.Property(value => value.TenantId).HasColumnName("tenant_id");
        link.Property(value => value.SiigoUserId).HasColumnName("siigo_user_id").HasMaxLength(SiigoSellerLink.UserIdMaxLength);
        link.Property(value => value.SiigoUserName).HasColumnName("siigo_user_name").HasMaxLength(SiigoSellerLink.UserNameMaxLength);
        link.HasIndex(value => new { value.TenantId, value.ConnectionId }).HasDatabaseName("IX_seller_links_tenant_connection");
    }

    private static void ConfigureTaxLink(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<SiigoTaxLink>();
        link.ToTable("tax_links", Schema);
        link.HasKey(value => new { value.ConnectionId, value.TaxPercentage }).HasName("PK_tax_links");
        link.Property(value => value.ConnectionId).HasColumnName("connection_id");
        link.Property(value => value.TaxPercentage).HasColumnName("tax_percentage");
        link.Property(value => value.TenantId).HasColumnName("tenant_id");
        link.Property(value => value.SiigoTaxId).HasColumnName("siigo_tax_id").HasMaxLength(SiigoTaxLink.TaxIdMaxLength);
    }

    private static void ConfigureWithholdingLink(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<SiigoWithholdingLink>();
        link.ToTable("withholding_links", Schema);
        link.HasKey(value => new { value.ConnectionId, value.TaxRateId }).HasName("PK_withholding_links");
        link.Property(value => value.ConnectionId).HasColumnName("connection_id");
        link.Property(value => value.TaxRateId).HasColumnName("tax_rate_id");
        link.Property(value => value.TenantId).HasColumnName("tenant_id");
        link.Property(value => value.SiigoTaxId).HasColumnName("siigo_tax_id").HasMaxLength(SiigoTaxLink.TaxIdMaxLength);
    }

    private static void ConfigurePaymentLink(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<SiigoPaymentLink>();
        link.ToTable("payment_links", Schema);
        link.HasKey(value => new { value.ConnectionId, value.BankAccountKey }).HasName("PK_payment_links");
        link.Property(value => value.ConnectionId).HasColumnName("connection_id");
        link.Property(value => value.BankAccountKey).HasColumnName("bank_account_key").HasMaxLength(BankAccountKey.MaxLength);
        link.Property(value => value.TenantId).HasColumnName("tenant_id");
        link.Property(value => value.SiigoPaymentTypeId).HasColumnName("siigo_payment_type_id").HasMaxLength(SiigoTaxLink.TaxIdMaxLength);
    }

    // Las tandas de facturación (spec 2026-11-21): una fila por tanda y una por pedido. Sin FKs a otros esquemas (pedido y usuario son
    // referencias blandas); los ítems se borran con su tanda.
    private static void ConfigureInvoiceBatch(ModelBuilder modelBuilder)
    {
        var batch = modelBuilder.Entity<SiigoInvoiceBatch>();
        batch.ToTable("invoice_batches", Schema);
        batch.HasKey(value => value.Id).HasName("PK_invoice_batches");
        batch.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        batch.Property(value => value.TenantId).HasColumnName("tenant_id");
        batch.Property(value => value.RequestedBy).HasColumnName("requested_by");
        batch.Property(value => value.CreatedAt).HasColumnName("created_at");
        batch.Property(value => value.FinishedAt).HasColumnName("finished_at");
        batch.Ignore(value => value.IsFinished);
        batch.HasIndex(value => new { value.TenantId, value.CreatedAt }).HasDatabaseName("IX_invoice_batches_tenant_created");
        batch.HasMany(value => value.Items).WithOne().HasForeignKey(item => item.BatchId).OnDelete(DeleteBehavior.Cascade);
        batch.Navigation(value => value.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        var item = modelBuilder.Entity<SiigoInvoiceBatchItem>();
        item.ToTable("invoice_batch_items", Schema);
        item.HasKey(value => value.Id).HasName("PK_invoice_batch_items");
        item.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        item.Property(value => value.BatchId).HasColumnName("batch_id");
        item.Property(value => value.TenantId).HasColumnName("tenant_id");
        item.Property(value => value.OrderId).HasColumnName("order_id");
        item.Property(value => value.Position).HasColumnName("position");
        item.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        item.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(SiigoInvoiceBatchItem.ErrorCodeMaxLength);
        item.Property(value => value.MissingCodes)
            .HasColumnName("missing_codes")
            .HasMaxLength(SiigoInvoiceBatchItem.MissingCodesMaxLength)
            .HasConversion(
                codes => string.Join(',', codes),
                text => text.Length == 0 ? Array.Empty<string>() : text.Split(',', StringSplitOptions.None),
                new ValueComparer<IReadOnlyList<string>>(
                    (left, right) => left!.SequenceEqual(right!),
                    codes => codes.Aggregate(0, (hash, code) => HashCode.Combine(hash, code)),
                    codes => codes.ToArray()));
        item.Property(value => value.InvoiceNumber).HasColumnName("invoice_number").HasMaxLength(SiigoInvoiceBatchItem.InvoiceNumberMaxLength);
        // Referencia blanda a siigo.invoices, como order_id: sin FK.
        item.Property(value => value.InvoiceId).HasColumnName("invoice_id");
        item.Property(value => value.PaymentsJson).HasColumnName("payments_json").HasColumnType("text");
        // Con valor por defecto en la columna: durante el rollout el código anterior inserta ítems sin nombrarla.
        item.Property(value => value.OrderMarkFailed).HasColumnName("order_mark_failed").HasDefaultValue(false);
        item.Property(value => value.Attempts).HasColumnName("attempts");
        item.Property(value => value.AvailableAt).HasColumnName("available_at");
        item.Property(value => value.LeaseUntil).HasColumnName("lease_until");
        item.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // Un pedido una sola vez por tanda.
        item.HasIndex(value => new { value.BatchId, value.OrderId }).IsUnique().HasDatabaseName("IX_invoice_batch_items_batch_order");
        // El reclamo del worker: lo pendiente por hora y lo reclamado por vencimiento del lease. Sólo lo no resuelto.
        item.HasIndex(value => new { value.Status, value.AvailableAt })
            .HasDatabaseName("IX_invoice_batch_items_claim")
            .HasFilter("status IN ('Pending', 'Sending')");
        // Un pedido en una sola tanda sin terminar.
        item.HasIndex(value => new { value.TenantId, value.OrderId })
            .IsUnique()
            .HasDatabaseName(InvoiceBatchItemsTenantOrderOpenIndex)
            .HasFilter("status IN ('Pending', 'Sending')");
        item.Ignore(value => value.IsSettled);
    }

    private static void ConfigureInvoice(ModelBuilder modelBuilder)
    {
        var invoice = modelBuilder.Entity<SiigoInvoice>();
        invoice.ToTable("invoices", Schema);
        invoice.HasKey(value => value.Id).HasName("PK_invoices");
        invoice.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        invoice.Property(value => value.TenantId).HasColumnName("tenant_id");
        invoice.Property(value => value.ConnectionId).HasColumnName("connection_id");
        invoice.Property(value => value.OrderId).HasColumnName("order_id");
        invoice.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        invoice.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        invoice.Property(value => value.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(SiigoInvoice.KeyMaxLength);
        // La petición entera, tal como salió: un reintento manda estos bytes. Texto y no jsonb: jsonb la normalizaría y cambiaría los bytes.
        invoice.Property(value => value.RequestJson).HasColumnName("request_json").HasColumnType("text");
        invoice.Property(value => value.SiigoInvoiceId).HasColumnName("siigo_invoice_id").HasMaxLength(SiigoInvoice.SiigoIdMaxLength);
        invoice.Property(value => value.Number).HasColumnName("number").HasMaxLength(SiigoInvoice.NumberMaxLength);
        invoice.Property(value => value.Cufe).HasColumnName("cufe").HasMaxLength(SiigoInvoice.CufeMaxLength);
        invoice.Property(value => value.SiigoTotal).HasColumnName("siigo_total").HasPrecision(18, 2);
        invoice.Property(value => value.ExpectedPaymentsTotal).HasColumnName("expected_payments_total").HasPrecision(18, 2);
        invoice.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(SiigoInvoice.ErrorCodeMaxLength);
        invoice.Property(value => value.ErrorDetail).HasColumnName("error_detail").HasMaxLength(SiigoInvoice.ErrorDetailMaxLength);
        invoice.Property(value => value.Attempts).HasColumnName("attempts");
        invoice.Property(value => value.OrderVersion).HasColumnName("order_version");
        invoice.Property(value => value.OrderChanged).HasColumnName("order_changed");
        invoice.Property(value => value.SendingSince).HasColumnName("sending_since");
        invoice.Property(value => value.UnansweredAt).HasColumnName("unanswered_at");
        invoice.Property(value => value.LastUnansweredAt).HasColumnName("last_unanswered_at");
        invoice.Property(value => value.CreatedBy).HasColumnName("created_by");
        invoice.Property(value => value.CreatedAt).HasColumnName("created_at");
        invoice.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // Una vigente por pedido y tipo. Parcial por el texto del estado: Rejected, Failed y Annulled liberan el pedido.
        invoice.HasIndex(value => new { value.TenantId, value.OrderId, value.Kind })
            .IsUnique()
            .HasDatabaseName(InvoicesTenantOrderKindIndex)
            .HasFilter("status NOT IN ('Rejected', 'Failed', 'Annulled')");
        // La consulta de «¿hay una abierta?» de la marca manual (Pending y Unknown), por pedido.
        invoice.HasIndex(value => new { value.TenantId, value.OrderId }).HasDatabaseName("IX_invoices_tenant_order");
    }

    // Spec 2026-11-21: las llamadas a Siigo que fallaron. Append-only; la expiración (7 días) la hace SiigoCallFailureLog al escribir.
    private static void ConfigureCallFailure(ModelBuilder modelBuilder)
    {
        var failure = modelBuilder.Entity<SiigoCallFailure>();
        failure.ToTable("call_failures", Schema);
        failure.HasKey(value => value.Id).HasName("PK_call_failures");
        failure.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        failure.Property(value => value.TenantId).HasColumnName("tenant_id");
        failure.Property(value => value.ConnectionId).HasColumnName("connection_id");
        failure.Property(value => value.Operation).HasColumnName("operation").HasConversion<string>().HasMaxLength(SiigoCallFailure.OperationMaxLength);
        failure.Property(value => value.Method).HasColumnName("method").HasMaxLength(SiigoCallFailure.MethodMaxLength);
        failure.Property(value => value.Path).HasColumnName("path").HasMaxLength(SiigoCallFailure.PathMaxLength);
        failure.Property(value => value.Status).HasColumnName("status");
        failure.Property(value => value.Reason).HasColumnName("reason").HasMaxLength(SiigoCallFailure.ReasonMaxLength);
        failure.Property(value => value.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(SiigoInvoice.KeyMaxLength);
        failure.Property(value => value.SiigoInvoiceId).HasColumnName("siigo_invoice_id").HasMaxLength(SiigoInvoice.SiigoIdMaxLength);
        failure.Property(value => value.RequestCurl).HasColumnName("request_curl").HasColumnType("text");
        failure.Property(value => value.ResponseBody).HasColumnName("response_body").HasColumnType("text");
        failure.Property(value => value.SubjectId).HasColumnName("subject_id");
        failure.Property(value => value.TraceId).HasColumnName("trace_id").HasMaxLength(SiigoCallFailure.TraceIdMaxLength);
        failure.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        // La lista del tenant, de la más nueva a la más vieja; y la expiración, que busca lo vencido de todos los tenants.
        failure.HasIndex(value => new { value.TenantId, value.OccurredAt }).IsDescending(false, true).HasDatabaseName("IX_call_failures_tenant_occurred");
        failure.HasIndex(value => value.OccurredAt).HasDatabaseName("IX_call_failures_occurred");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<SiigoOutboxMessage>();
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

/// <summary>
/// Proyección de escritura del outbox de plataforma, propiedad de Tenancy. Siigo inserta acá sus eventos en la misma transacción que
/// el cambio que los origina.
/// </summary>
internal sealed class SiigoOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}
