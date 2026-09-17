using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

public sealed class QuotationsDbContext(DbContextOptions<QuotationsDbContext> options)
    : DbContext(options)
{
    public DbSet<Quotation> Quotations => Set<Quotation>();

    public DbSet<QuotationHistoryEntry> QuotationHistoryEntries => Set<QuotationHistoryEntry>();

    internal DbSet<QuotationItem> QuotationItems => Set<QuotationItem>();

    internal DbSet<QuotationParty> QuotationParties => Set<QuotationParty>();

    internal DbSet<QuotationNumberCounter> QuotationNumberCounters => Set<QuotationNumberCounter>();

    public DbSet<Order> Orders => Set<Order>();

    internal DbSet<OrderPaymentProof> OrderPaymentProofs => Set<OrderPaymentProof>();

    internal DbSet<OrderNumberCounter> OrderNumberCounters => Set<OrderNumberCounter>();

    internal DbSet<QuotationPdf> QuotationPdfs => Set<QuotationPdf>();

    internal DbSet<QuotationsOutboxMessage> Outbox => Set<QuotationsOutboxMessage>();

    internal DbSet<ExportJob> ExportJobs => Set<ExportJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureQuotation(modelBuilder);
        ConfigureQuotationItem(modelBuilder);
        ConfigureQuotationParty(modelBuilder);
        ConfigureQuotationHistoryEntry(modelBuilder);
        ConfigureQuotationPdf(modelBuilder);
        ConfigureQuotationNumberCounter(modelBuilder);
        ConfigureOrder(modelBuilder);
        ConfigureOrderPaymentProof(modelBuilder);
        ConfigureOrderNumberCounter(modelBuilder);
        ConfigureExportJob(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureQuotation(ModelBuilder modelBuilder)
    {
        var quotation = modelBuilder.Entity<Quotation>();
        quotation.ToTable("quotations", "quotations");
        quotation.HasKey(value => value.Id);
        quotation.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new QuotationId(value))
            .ValueGeneratedNever();
        quotation.Property(value => value.TenantId).HasColumnName("tenant_id");
        quotation.Property(value => value.QuotationNumber)
            .HasColumnName("quotation_number")
            .HasMaxLength(Quotation.QuotationNumberMaxLength);
        quotation.Property(value => value.ClientId).HasColumnName("client_id");
        quotation.Property(value => value.AdvisorId)
            .HasColumnName("advisor_id")
            .HasConversion(id => id.Value, value => new MemberId(value));
        // Texto y no el entero por defecto de EF: una fila legible a simple vista en sql vale
        // mas que los bytes que ahorra un enum entero, mismo criterio que PriceScale.Restriction.
        quotation.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        quotation.Property(value => value.CreatedAt).HasColumnName("created_at");
        quotation.Property(value => value.ValidUntil).HasColumnName("valid_until");
        // El codigo ISO y no el nombre del miembro del enum: la columna dice COP/USD, que es lo
        // que dice la cuenta bancaria de la empresa de la que sale y lo que espera cualquiera que
        // lea la tabla a mano. Texto y no entero, mismo criterio que Status.
        quotation.Property(value => value.Currency)
            .HasColumnName("currency")
            .HasConversion(
                currency => currency.ToCode(),
                code => QuotationCurrencies.FromCode(code))
            .HasMaxLength(3);
        quotation.Property(value => value.PaymentMethod)
            .HasColumnName("payment_method")
            .HasMaxLength(Quotation.PaymentMethodMaxLength);
        quotation.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(14, 2);
        quotation.Property(value => value.TaxPercentage)
            .HasColumnName("tax_percentage")
            .HasPrecision(5, 2);
        quotation.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        quotation.Property(value => value.DiscountAmount)
            .HasColumnName("discount_amount")
            .HasPrecision(14, 2);
        quotation.Property(value => value.Total).HasColumnName("total").HasPrecision(14, 2);
        quotation.Property(value => value.BillingUsesBusinessName)
            .HasColumnName("billing_uses_business_name");
        quotation.Property(value => value.IsStorePickup)
            .HasColumnName("is_store_pickup");
        quotation.Property(value => value.BillsToFinalConsumer)
            .HasColumnName("bills_to_final_consumer");
        quotation.Property(value => value.CustomerWithRetention)
            .HasColumnName("customer_with_retention");
        quotation.Property(value => value.CustomerVatSurplus)
            .HasColumnName("customer_vat_surplus");
        quotation.Property(value => value.PartyWithRetention)
            .HasColumnName("party_with_retention");
        quotation.Property(value => value.PartyVatSurplus)
            .HasColumnName("party_vat_surplus");
        quotation.Property(value => value.RetentionAmount)
            .HasColumnName("retention_amount")
            .HasPrecision(14, 2);
        quotation.Property(value => value.NetTotal).HasColumnName("net_total").HasPrecision(14, 2);
        quotation.Property(value => value.Notes).HasColumnName("notes").HasColumnType("text");
        quotation.Property(value => value.CreatedBy)
            .HasColumnName("created_by")
            .HasConversion(id => id.Value, value => new MemberId(value));
        quotation.Property(value => value.UpdatedBy)
            .HasColumnName("updated_by")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new MemberId(value.Value) : null);
        quotation.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        quotation.Property(value => value.SentAt).HasColumnName("sent_at");
        quotation.Property(value => value.PdfFileId).HasColumnName("pdf_file_id");
        quotation.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        // Owned y no tabla aparte: es a lo sumo una cuenta por cotizacion, y una tabla hija con
        // una fila como maximo solo agrega un JOIN. Las cuatro columnas viven en `quotations`.
        // IsRequired(false) en el owned entero: null es "todavia no eligio con que cuenta cobra",
        // el estado en que nace un borrador.
        quotation.OwnsOne(value => value.BillingAccount, billing =>
        {
            billing.Property(value => value.CompanyId).HasColumnName("billing_company_id");
            billing.Property(value => value.BankName)
                .HasColumnName("billing_bank_name")
                .HasMaxLength(QuotationBillingAccount.BankNameMaxLength);
            billing.Property(value => value.AccountNumber)
                .HasColumnName("billing_account_number")
                .HasMaxLength(QuotationBillingAccount.AccountNumberMaxLength);
            billing.Property(value => value.Currency)
                .HasColumnName("billing_account_currency")
                .HasMaxLength(QuotationBillingAccount.CurrencyLength);
        });
        quotation.Navigation(value => value.BillingAccount).IsRequired(false);

        quotation.Navigation(value => value.Items)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        quotation.Navigation(value => value.Parties)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        quotation.HasIndex(value => value.TenantId).HasDatabaseName("IX_quotations_tenant");
        quotation.HasIndex(value => value.ClientId).HasDatabaseName("IX_quotations_client");
        quotation.HasIndex(value => value.AdvisorId).HasDatabaseName("IX_quotations_advisor");
        quotation.HasIndex(value => value.Status).HasDatabaseName("IX_quotations_status");
        quotation.HasIndex(value => value.CreatedAt).HasDatabaseName("IX_quotations_created_at");
        // El keyset de la exportación (spec 2026-09-12, D8): tenant, fecha de alta y número
        // —único por tenant, el desempate—. Postgres recorre el btree en los dos sentidos, así que
        // sirve al ORDER BY descendente sin declararlo.
        quotation.HasIndex(value => new { value.TenantId, value.CreatedAt, value.QuotationNumber })
            .HasDatabaseName("IX_quotations_tenant_created_at_number");
        // La unicidad que promete el numero de cotizacion. Nombrado a proposito: la capa de
        // infraestructura discrimina la violacion de unicidad por nombre de indice, no solo por
        // SqlState -- la leccion de SDD-CT-06.
        quotation.HasIndex(value => new { value.TenantId, value.QuotationNumber })
            .IsUnique()
            .HasDatabaseName("IX_quotations_tenant_number");
    }

    private static void ConfigureQuotationItem(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<QuotationItem>();
        item.ToTable("quotation_items", "quotations");
        item.HasKey(value => value.Id);
        item.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new QuotationItemId(value))
            .ValueGeneratedNever();
        item.Property(value => value.QuotationId)
            .HasColumnName("quotation_id")
            .HasConversion(id => id.Value, value => new QuotationId(value));
        item.Property(value => value.ProductId).HasColumnName("product_id");
        item.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(10, 2);
        item.Property(value => value.UnitPrice).HasColumnName("unit_price").HasPrecision(14, 2);
        item.Property(value => value.DiscountPercentage)
            .HasColumnName("discount_percentage")
            .HasPrecision(5, 2);
        item.Property(value => value.DiscountAmount)
            .HasColumnName("discount_amount")
            .HasPrecision(14, 2);
        item.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(14, 2);
        item.Property(value => value.TaxPercentage).HasColumnName("tax_percentage");
        item.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        item.Property(value => value.Position).HasColumnName("position");
        item.Property(value => value.CreatedAt).HasColumnName("created_at");
        item.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        item.HasIndex(value => value.QuotationId).HasDatabaseName("IX_quotation_items_quotation");

        // CASCADE: una linea no tiene sentido sin su cotizacion -- no es catalogo compartido, es
        // parte del mismo agregado. Mismo criterio que PriceScale -> Product en Catalog.
        item.HasOne<Quotation>()
            .WithMany(quotation => quotation.Items)
            .HasForeignKey(value => value.QuotationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureQuotationParty(ModelBuilder modelBuilder)
    {
        var party = modelBuilder.Entity<QuotationParty>();
        party.ToTable("quotation_parties", "quotations");
        party.HasKey(value => value.Id);
        party.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new QuotationPartyId(value))
            .ValueGeneratedNever();
        party.Property(value => value.QuotationId)
            .HasColumnName("quotation_id")
            .HasConversion(id => id.Value, value => new QuotationId(value));
        party.Property(value => value.Role)
            .HasColumnName("role")
            .HasConversion<string>()
            .HasMaxLength(20);
        party.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(QuotationPartyDetails.NameMaxLength);
        party.Property(value => value.Phone)
            .HasColumnName("phone")
            .HasMaxLength(QuotationPartyDetails.PhoneMaxLength);
        party.Property(value => value.Email)
            .HasColumnName("email")
            .HasMaxLength(QuotationPartyDetails.EmailMaxLength);
        party.Property(value => value.Address)
            .HasColumnName("address")
            .HasMaxLength(QuotationPartyDetails.AddressMaxLength);
        party.Property(value => value.DepartmentId).HasColumnName("department_id");
        party.Property(value => value.CityId).HasColumnName("city_id");

        // Una parte por rol y por cotizacion: es lo que hace que "sin fila = usa los datos del
        // cliente" sea una regla y no una convencion. Nombrado, como el numero de cotizacion:
        // la infraestructura discrimina la violacion de unicidad por nombre de indice.
        party.HasIndex(value => new { value.QuotationId, value.Role })
            .IsUnique()
            .HasDatabaseName("IX_quotation_parties_quotation_role");

        // CASCADE: una parte no tiene sentido sin su cotizacion -- mismo criterio que la linea.
        party.HasOne<Quotation>()
            .WithMany(quotation => quotation.Parties)
            .HasForeignKey(value => value.QuotationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureQuotationHistoryEntry(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<QuotationHistoryEntry>();
        entry.ToTable("quotation_history", "quotations");
        entry.HasKey(value => value.Id);
        entry.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new QuotationHistoryEntryId(value))
            .ValueGeneratedNever();
        entry.Property(value => value.QuotationId)
            .HasColumnName("quotation_id")
            .HasConversion(id => id.Value, value => new QuotationId(value));
        entry.Property(value => value.EventType)
            .HasColumnName("event_type")
            .HasConversion<string>()
            .HasMaxLength(50);
        entry.Property(value => value.EventAt).HasColumnName("event_at");
        // Nullable: el evento Expired (US-19) lo dispara un job programado, no una persona.
        entry.Property(value => value.MemberId)
            .HasColumnName("member_id")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new MemberId(value.Value) : null);
        entry.Property(value => value.Details).HasColumnName("details").HasMaxLength(500);
        entry.Property(value => value.CreatedAt).HasColumnName("created_at");

        entry.HasIndex(value => new { value.QuotationId, value.EventAt })
            .HasDatabaseName("IX_quotation_history_quotation_event_at");

        // No hijo del agregado Quotation (ver comentario en el dominio): igual CASCADE, porque
        // el historial de una cotizacion borrada no tiene a que aferrarse.
        entry.HasOne<Quotation>()
            .WithMany()
            .HasForeignKey(value => value.QuotationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// El PDF generado de una cotizacion, una fila por cotizacion: la clave primaria es el
    /// propio `quotation_id`. Regenerar pisa la fila, y el objeto anterior queda huerfano en el
    /// bucket, donde lo limpia la regla de lifecycle.
    /// </summary>
    private static void ConfigureQuotationPdf(ModelBuilder modelBuilder)
    {
        var pdf = modelBuilder.Entity<QuotationPdf>();
        pdf.ToTable("quotation_pdfs", "quotations");
        pdf.HasKey(value => value.QuotationId);
        pdf.Property(value => value.QuotationId)
            .HasColumnName("quotation_id")
            .HasConversion(id => id.Value, value => new QuotationId(value))
            .ValueGeneratedNever();
        pdf.Property(value => value.TenantId).HasColumnName("tenant_id");
        pdf.Property(value => value.StorageKey)
            .HasColumnName("storage_key")
            .HasMaxLength(500)
            .IsRequired();
        pdf.Property(value => value.QuotationVersion).HasColumnName("quotation_version");
        pdf.Property(value => value.GeneratedAt).HasColumnName("generated_at");

        // El tenant no es parte de la clave --la cotizacion ya es unica-- pero toda consulta de
        // este modulo filtra por el, y sin indice la de exportar haria un scan.
        pdf.HasIndex(value => value.TenantId).HasDatabaseName("IX_quotation_pdfs_tenant");

        // CASCADE, igual que el historial: un PDF sin su cotizacion no le sirve a nadie.
        pdf.HasOne<Quotation>()
            .WithMany()
            .HasForeignKey(value => value.QuotationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// El consecutivo del numero de cotizacion, una fila por (tenant, año). Mismo criterio que
    /// CustomerCucCounter: una fila por clave evita que dos altas concurrentes lean el mismo
    /// maximo y choquen contra el indice unico con un 500 inaccionable.
    /// </summary>
    private static void ConfigureQuotationNumberCounter(ModelBuilder modelBuilder)
    {
        var counter = modelBuilder.Entity<QuotationNumberCounter>();
        counter.ToTable("quotation_number_counters", "quotations");
        counter.HasKey(value => new { value.TenantId, value.Year });
        counter.Property(value => value.TenantId).HasColumnName("tenant_id");
        counter.Property(value => value.Year).HasColumnName("year");
        counter.Property(value => value.NextValue).HasColumnName("next_value");
    }

    private static void ConfigureOrder(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<Order>();
        order.ToTable("orders", "quotations");
        order.HasKey(value => value.Id);
        order.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();
        order.Property(value => value.TenantId).HasColumnName("tenant_id");
        order.Property(value => value.OrderNumber)
            .HasColumnName("order_number")
            .HasMaxLength(Order.OrderNumberMaxLength);
        order.Property(value => value.QuotationId)
            .HasColumnName("quotation_id")
            .HasConversion(id => id.Value, value => new QuotationId(value));
        order.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        order.Property(value => value.PaymentStatus)
            .HasColumnName("payment_status")
            .HasConversion<string>()
            .HasMaxLength(30);
        order.Property(value => value.Notes).HasColumnName("notes").HasMaxLength(Order.NotesMaxLength);
        order.Property(value => value.ConvertedAt).HasColumnName("converted_at");
        order.Property(value => value.ConvertedBy)
            .HasColumnName("converted_by")
            .HasConversion(id => id.Value, value => new MemberId(value));
        order.Property(value => value.ApprovedAt).HasColumnName("approved_at");
        order.Property(value => value.ApprovedBy)
            .HasColumnName("approved_by")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new MemberId(value.Value) : null);
        // Spec 2026-09-16: quién, cuándo y por qué se anuló. Nullables, porque un pedido vivo no
        // tiene nada que guardar acá; misma conversión nullable que approved_by.
        order.Property(value => value.CancelledAt).HasColumnName("cancelled_at");
        order.Property(value => value.CancelledBy)
            .HasColumnName("cancelled_by")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new MemberId(value.Value) : null);
        order.Property(value => value.CancellationReason)
            .HasColumnName("cancellation_reason")
            .HasMaxLength(Order.CancellationReasonMaxLength);
        order.Property(value => value.RitualCollectionSyncId)
            .HasColumnName("ritual_collection_sync_id")
            .HasMaxLength(100);
        order.Property(value => value.CreatedAt).HasColumnName("created_at");
        order.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        order.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        order.Navigation(value => value.PaymentProofs)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        order.HasIndex(value => value.TenantId).HasDatabaseName("IX_orders_tenant");
        // 1:1 con la cotizacion de origen (modelo-datos-cotizaciones.md §2.4).
        order.HasIndex(value => value.QuotationId)
            .IsUnique()
            .HasDatabaseName("IX_orders_quotation");
        // La unicidad que promete el numero de pedido. Nombrado a proposito, misma leccion de
        // SDD-CT-06 que IX_quotations_tenant_number.
        order.HasIndex(value => new { value.TenantId, value.OrderNumber })
            .IsUnique()
            .HasDatabaseName("IX_orders_tenant_number");
        // El keyset de la exportación de pedidos (spec 2026-09-12, D8): el orden exacto del listado
        // —fecha de conversión y número como desempate, OrderRepository.SearchAsync— detrás del
        // tenant.
        order.HasIndex(value => new { value.TenantId, value.ConvertedAt, value.OrderNumber })
            .HasDatabaseName("IX_orders_tenant_converted_at_number");

        // RESTRICT, no CASCADE: el pedido es el registro que sobrevive -- borrar la cotizacion de
        // origen (si algun dia existiera un borrado duro) no deberia poder llevarse el pedido
        // consigo en silencio.
        order.HasOne<Quotation>()
            .WithMany()
            .HasForeignKey(value => value.QuotationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOrderPaymentProof(ModelBuilder modelBuilder)
    {
        var proof = modelBuilder.Entity<OrderPaymentProof>();
        proof.ToTable("order_payment_proofs", "quotations");
        proof.HasKey(value => value.Id);
        proof.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new OrderPaymentProofId(value))
            .ValueGeneratedNever();
        proof.Property(value => value.OrderId)
            .HasColumnName("order_id")
            .HasConversion(id => id.Value, value => new OrderId(value));
        proof.Property(value => value.FileId).HasColumnName("file_id");
        proof.Property(value => value.Amount).HasColumnName("amount").HasPrecision(14, 2);
        proof.Property(value => value.UploadedBy)
            .HasColumnName("uploaded_by")
            .HasConversion(id => id.Value, value => new MemberId(value));
        proof.Property(value => value.UploadedAt).HasColumnName("uploaded_at");
        // La clave de la copia pública (spec 2026-09-15, P5): nullable, porque los comprobantes
        // privados no tienen. La clave mide 51 caracteres (`payment-proofs/` + 32 hex + extensión);
        // 200 deja margen.
        proof.Property(value => value.PublicStorageKey)
            .HasColumnName("public_storage_key")
            .HasMaxLength(200);
        proof.HasIndex(value => value.OrderId).HasDatabaseName("IX_order_payment_proofs_order");
        // Spec 2026-09-16, D17: Storage pregunta por esta tabla en cada barrido. El de staging busca
        // por archivo (IFileReferenceProbe) y la reconciliación del bucket público por clave
        // (IPublicObjectReferenceProbe). Sin estos índices, cada pregunta recorre la tabla entera.
        proof.HasIndex(value => value.FileId).HasDatabaseName("IX_order_payment_proofs_file");
        proof.HasIndex(value => value.PublicStorageKey).HasDatabaseName("IX_order_payment_proofs_public_key");

        // CASCADE: un comprobante no tiene sentido sin su pedido -- mismo criterio que
        // QuotationItem -> Quotation.
        proof.HasOne<Order>()
            .WithMany(order => order.PaymentProofs)
            .HasForeignKey(value => value.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>El consecutivo del numero de pedido, una fila por (tenant, año). Mismo criterio
    /// que <see cref="ConfigureQuotationNumberCounter"/>.</summary>
    private static void ConfigureOrderNumberCounter(ModelBuilder modelBuilder)
    {
        var counter = modelBuilder.Entity<OrderNumberCounter>();
        counter.ToTable("order_number_counters", "quotations");
        counter.HasKey(value => new { value.TenantId, value.Year });
        counter.Property(value => value.TenantId).HasColumnName("tenant_id");
        counter.Property(value => value.Year).HasColumnName("year");
        counter.Property(value => value.NextValue).HasColumnName("next_value");
    }

    // Los dos índices sólo miran los jobs vivos: la toma y el límite de pendientes nunca buscan
    // uno terminado, y los terminados se acumulan hasta la purga de 30 días.
    private const string ActiveExportJobFilter = "status IN ('Pending', 'Processing')";

    /// <summary>
    /// La cola de exportaciones (spec 2026-09-12, D2). Los nombres de columna van a mano y no por
    /// convención porque ExportJobQueue los escribe en SQL crudo para la toma con SKIP LOCKED.
    /// </summary>
    private static void ConfigureExportJob(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<ExportJob>();
        job.ToTable("export_jobs", "quotations");
        job.HasKey(value => value.Id);
        job.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        job.Property(value => value.TenantId).HasColumnName("tenant_id");
        job.Property(value => value.RequestedBy).HasColumnName("requested_by");
        // Texto y no entero, mismo criterio que Quotation.Status: soporte lee la tabla a mano.
        job.Property(value => value.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(20);
        job.Property(value => value.Filters).HasColumnName("filters").HasColumnType("jsonb");
        job.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        // Token de concurrencia: la toma lo incrementa, así que un worker cuyo lease venció y otro
        // retomó no puede cerrar el job con los intentos viejos (el UPDATE no encuentra la fila).
        job.Property(value => value.Attempts).HasColumnName("attempts").IsConcurrencyToken();
        job.Property(value => value.NextAttemptAt).HasColumnName("next_attempt_at");
        job.Property(value => value.LockedUntil).HasColumnName("locked_until");
        job.Property(value => value.LastError).HasColumnName("last_error").HasColumnType("text");
        job.Property(value => value.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(ExportJob.FileNameMaxLength);
        job.Property(value => value.RowCount).HasColumnName("row_count");
        job.Property(value => value.RequestedAt).HasColumnName("requested_at");
        job.Property(value => value.CompletedAt).HasColumnName("completed_at");

        job.HasIndex(value => new { value.Status, value.NextAttemptAt })
            .HasDatabaseName("IX_export_jobs_claim")
            .HasFilter(ActiveExportJobFilter);
        job.HasIndex(value => new { value.TenantId, value.RequestedBy, value.Status })
            .HasDatabaseName("IX_export_jobs_requester")
            .HasFilter(ActiveExportJobFilter);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<QuotationsOutboxMessage>();
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
