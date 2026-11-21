using Microsoft.EntityFrameworkCore;
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

    /// <summary>Spec 2026-11-18 §5.6: una factura vigente por pedido y tipo (todo menos <c>Rejected</c> y <c>Failed</c>). El candado del
    /// pedido da la respuesta amable; este índice cierra la carrera. <c>SiigoUnitOfWork</c> lo traduce por nombre a
    /// <c>409 siigo.invoice.already_exists</c>.</summary>
    public const string InvoicesTenantOrderKindIndex = "IX_invoices_tenant_order_kind";

    internal DbSet<SiigoSettings> Settings => Set<SiigoSettings>();

    internal DbSet<SiigoInvoice> Invoices => Set<SiigoInvoice>();

    internal DbSet<SiigoVariantLink> VariantLinks => Set<SiigoVariantLink>();

    internal DbSet<SiigoTaxLink> TaxLinks => Set<SiigoTaxLink>();

    internal DbSet<SiigoWithholdingLink> WithholdingLinks => Set<SiigoWithholdingLink>();

    internal DbSet<SiigoPaymentLink> PaymentLinks => Set<SiigoPaymentLink>();

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
        ConfigureWithholdingLink(modelBuilder);
        ConfigurePaymentLink(modelBuilder);
        ConfigureInvoice(modelBuilder);
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
        invoice.Property(value => value.CreatedBy).HasColumnName("created_by");
        invoice.Property(value => value.CreatedAt).HasColumnName("created_at");
        invoice.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // Una vigente por pedido y tipo. Parcial por el texto del estado: Rejected y Failed liberan el pedido.
        invoice.HasIndex(value => new { value.TenantId, value.OrderId, value.Kind })
            .IsUnique()
            .HasDatabaseName(InvoicesTenantOrderKindIndex)
            .HasFilter("status NOT IN ('Rejected', 'Failed')");
        // La consulta de «¿hay una abierta?» de la marca manual (Pending y Unknown), por pedido.
        invoice.HasIndex(value => new { value.TenantId, value.OrderId }).HasDatabaseName("IX_invoices_tenant_order");
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
