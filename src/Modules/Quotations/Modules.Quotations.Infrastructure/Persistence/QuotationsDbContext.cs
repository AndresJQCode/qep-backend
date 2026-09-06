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

    public DbSet<Sale> Sales => Set<Sale>();

    internal DbSet<SalePaymentProof> SalePaymentProofs => Set<SalePaymentProof>();

    internal DbSet<SaleNumberCounter> SaleNumberCounters => Set<SaleNumberCounter>();

    internal DbSet<QuotationsOutboxMessage> Outbox => Set<QuotationsOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureQuotation(modelBuilder);
        ConfigureQuotationItem(modelBuilder);
        ConfigureQuotationParty(modelBuilder);
        ConfigureQuotationHistoryEntry(modelBuilder);
        ConfigureQuotationNumberCounter(modelBuilder);
        ConfigureSale(modelBuilder);
        ConfigureSalePaymentProof(modelBuilder);
        ConfigureSaleNumberCounter(modelBuilder);
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
        quotation.Property(value => value.CustomerWithRetention)
            .HasColumnName("customer_with_retention");
        quotation.Property(value => value.CustomerVatSurplus)
            .HasColumnName("customer_vat_surplus");
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

    private static void ConfigureSale(ModelBuilder modelBuilder)
    {
        var sale = modelBuilder.Entity<Sale>();
        sale.ToTable("sales", "quotations");
        sale.HasKey(value => value.Id);
        sale.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new SaleId(value))
            .ValueGeneratedNever();
        sale.Property(value => value.TenantId).HasColumnName("tenant_id");
        sale.Property(value => value.SaleNumber)
            .HasColumnName("sale_number")
            .HasMaxLength(Sale.SaleNumberMaxLength);
        sale.Property(value => value.QuotationId)
            .HasColumnName("quotation_id")
            .HasConversion(id => id.Value, value => new QuotationId(value));
        sale.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        sale.Property(value => value.PaymentStatus)
            .HasColumnName("payment_status")
            .HasConversion<string>()
            .HasMaxLength(30);
        sale.Property(value => value.Notes).HasColumnName("notes").HasMaxLength(Sale.NotesMaxLength);
        sale.Property(value => value.ConvertedAt).HasColumnName("converted_at");
        sale.Property(value => value.ConvertedBy)
            .HasColumnName("converted_by")
            .HasConversion(id => id.Value, value => new MemberId(value));
        sale.Property(value => value.ApprovedAt).HasColumnName("approved_at");
        sale.Property(value => value.ApprovedBy)
            .HasColumnName("approved_by")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new MemberId(value.Value) : null);
        sale.Property(value => value.RitualCollectionSyncId)
            .HasColumnName("ritual_collection_sync_id")
            .HasMaxLength(100);
        sale.Property(value => value.CreatedAt).HasColumnName("created_at");
        sale.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        sale.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        sale.Navigation(value => value.PaymentProofs)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        sale.HasIndex(value => value.TenantId).HasDatabaseName("IX_sales_tenant");
        // 1:1 con la cotizacion de origen (modelo-datos-cotizaciones.md §2.4).
        sale.HasIndex(value => value.QuotationId)
            .IsUnique()
            .HasDatabaseName("IX_sales_quotation");
        // La unicidad que promete el numero de venta. Nombrado a proposito, misma leccion de
        // SDD-CT-06 que IX_quotations_tenant_number.
        sale.HasIndex(value => new { value.TenantId, value.SaleNumber })
            .IsUnique()
            .HasDatabaseName("IX_sales_tenant_number");

        // RESTRICT, no CASCADE: la venta es el registro que sobrevive -- borrar la cotizacion de
        // origen (si algun dia existiera un borrado duro) no deberia poder llevarse la venta
        // consigo en silencio.
        sale.HasOne<Quotation>()
            .WithMany()
            .HasForeignKey(value => value.QuotationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSalePaymentProof(ModelBuilder modelBuilder)
    {
        var proof = modelBuilder.Entity<SalePaymentProof>();
        proof.ToTable("sale_payment_proofs", "quotations");
        proof.HasKey(value => value.Id);
        proof.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new SalePaymentProofId(value))
            .ValueGeneratedNever();
        proof.Property(value => value.SaleId)
            .HasColumnName("sale_id")
            .HasConversion(id => id.Value, value => new SaleId(value));
        proof.Property(value => value.FileId).HasColumnName("file_id");
        proof.Property(value => value.Amount).HasColumnName("amount").HasPrecision(14, 2);
        proof.Property(value => value.UploadedBy)
            .HasColumnName("uploaded_by")
            .HasConversion(id => id.Value, value => new MemberId(value));
        proof.Property(value => value.UploadedAt).HasColumnName("uploaded_at");
        proof.HasIndex(value => value.SaleId).HasDatabaseName("IX_sale_payment_proofs_sale");

        // CASCADE: un comprobante no tiene sentido sin su venta -- mismo criterio que
        // QuotationItem -> Quotation.
        proof.HasOne<Sale>()
            .WithMany(sale => sale.PaymentProofs)
            .HasForeignKey(value => value.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>El consecutivo del numero de venta, una fila por (tenant, año). Mismo criterio
    /// que <see cref="ConfigureQuotationNumberCounter"/>.</summary>
    private static void ConfigureSaleNumberCounter(ModelBuilder modelBuilder)
    {
        var counter = modelBuilder.Entity<SaleNumberCounter>();
        counter.ToTable("sale_number_counters", "quotations");
        counter.HasKey(value => new { value.TenantId, value.Year });
        counter.Property(value => value.TenantId).HasColumnName("tenant_id");
        counter.Property(value => value.Year).HasColumnName("year");
        counter.Property(value => value.NextValue).HasColumnName("next_value");
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
