using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

public sealed class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    private static readonly ValueConverter<MemberId, Guid> MemberIdConverter =
        new(id => id.Value, value => new MemberId(value));

    public DbSet<CashSession> CashSessions => Set<CashSession>();

    public DbSet<PosSale> Sales => Set<PosSale>();

    internal DbSet<PosSaleNumberCounter> SaleNumberCounters => Set<PosSaleNumberCounter>();

    internal DbSet<PosOutboxMessage> Outbox => Set<PosOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCashSession(modelBuilder);
        ConfigureSale(modelBuilder);
        ConfigureSaleLine(modelBuilder);
        ConfigurePayment(modelBuilder);
        ConfigureCounter(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureCashSession(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<CashSession>();
        session.ToTable("cash_sessions", "pos", table =>
        {
            table.HasCheckConstraint("CK_cash_sessions_status", "status IN ('Open','Closed')");
            table.HasCheckConstraint("CK_cash_sessions_opening_float", "opening_float >= 0");
        });
        session.HasKey(value => value.Id);
        session.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CashSessionId(value))
            .ValueGeneratedNever();
        session.Property(value => value.TenantId).HasColumnName("tenant_id");
        // tenancy.memberships(id), sin FK: otro módulo.
        session.Property(value => value.CashierId).HasColumnName("cashier_id").HasConversion(MemberIdConverter);
        session.Property(value => value.CashierName).HasColumnName("cashier_name").HasMaxLength(PosLimits.CashierNameMaxLength);
        // FK real a companies.companies, agregada a mano en la migración (spec, decisión 8): EF no
        // modela relaciones entre DbContext de módulos distintos.
        session.Property(value => value.CompanyId).HasColumnName("company_id");
        session.Property(value => value.CompanyName).HasColumnName("company_name").HasMaxLength(160);
        session.Property(value => value.CompanyTaxId).HasColumnName("company_tax_id").HasMaxLength(32);
        session.Property(value => value.CompanyAddress).HasColumnName("company_address").HasMaxLength(200);
        session.Property(value => value.CompanyPhone).HasColumnName("company_phone").HasMaxLength(32);
        session.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10);
        session.Property(value => value.OpeningFloat).HasColumnName("opening_float").HasPrecision(14, 2);
        session.Property(value => value.SalesCount).HasColumnName("sales_count");
        session.Property(value => value.VoidedCount).HasColumnName("voided_count");
        session.Property(value => value.SalesTotal).HasColumnName("sales_total").HasPrecision(14, 2);
        session.Property(value => value.CashTotal).HasColumnName("cash_total").HasPrecision(14, 2);
        session.Property(value => value.CardTotal).HasColumnName("card_total").HasPrecision(14, 2);
        session.Property(value => value.TransferTotal).HasColumnName("transfer_total").HasPrecision(14, 2);
        session.Property(value => value.ExpectedCash).HasColumnName("expected_cash").HasPrecision(14, 2);
        session.Property(value => value.CountedCash).HasColumnName("counted_cash").HasPrecision(14, 2);
        session.Property(value => value.CashDifference).HasColumnName("cash_difference").HasPrecision(14, 2);
        session.Property(value => value.ClosingNote).HasColumnName("closing_note").HasMaxLength(PosLimits.NoteMaxLength);
        session.Property(value => value.OpenedAt).HasColumnName("opened_at");
        session.Property(value => value.ClosedAt).HasColumnName("closed_at");
        session.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        session.Property(value => value.CreatedAt).HasColumnName("created_at");
        session.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        session.Ignore(value => value.LiveExpectedCash);

        // Lo que de verdad impone "una caja abierta por cajero": dos aperturas simultáneas sólo las
        // frena la base. PosUnitOfWork lo traduce a pos.session.already_open por este nombre.
        session.HasIndex(value => new { value.TenantId, value.CashierId })
            .IsUnique()
            .HasFilter("status = 'Open'")
            .HasDatabaseName("IX_cash_sessions_one_open_per_cashier");
        session.HasIndex(value => new { value.TenantId, value.OpenedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_cash_sessions_tenant_opened");
        session.HasIndex(value => value.CompanyId).HasDatabaseName("IX_cash_sessions_company");
    }

    private static void ConfigureSale(ModelBuilder modelBuilder)
    {
        var sale = modelBuilder.Entity<PosSale>();
        sale.ToTable("sales", "pos", table =>
            table.HasCheckConstraint("CK_sales_status", "status IN ('Completed','Voided')"));
        // PK_sales: PosUnitOfWork traduce su 23505 a pos.sale.id_taken.
        sale.HasKey(value => value.Id);
        sale.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PosSaleId(value))
            .ValueGeneratedNever();
        sale.Property(value => value.TenantId).HasColumnName("tenant_id");
        sale.Property(value => value.CashSessionId)
            .HasColumnName("cash_session_id")
            .HasConversion(id => id.Value, value => new CashSessionId(value));
        sale.Property(value => value.CashierId).HasColumnName("cashier_id").HasConversion(MemberIdConverter);
        sale.Property(value => value.RequestFingerprint).HasColumnName("request_fingerprint").HasColumnType("character(64)");
        sale.Property(value => value.SaleNumber).HasColumnName("sale_number").HasMaxLength(20);
        // Siempre null en el MVP; sin FK (spec, decisión 35).
        sale.Property(value => value.CustomerId).HasColumnName("customer_id");
        sale.Property(value => value.CustomerName).HasColumnName("customer_name").HasMaxLength(160);
        sale.Property(value => value.CustomerIdentificationType).HasColumnName("customer_identification_type").HasMaxLength(10);
        sale.Property(value => value.CustomerIdentificationNumber).HasColumnName("customer_identification_number").HasMaxLength(32);
        sale.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(14, 2);
        sale.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        sale.Property(value => value.DiscountAmount).HasColumnName("discount_amount").HasPrecision(14, 2);
        sale.Property(value => value.Total).HasColumnName("total").HasPrecision(14, 2);
        sale.Property(value => value.ChangeAmount).HasColumnName("change_amount").HasPrecision(14, 2);
        sale.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10);
        sale.Property(value => value.VoidReason).HasColumnName("void_reason").HasMaxLength(PosLimits.VoidReasonMaxLength);
        sale.Property(value => value.VoidedAt).HasColumnName("voided_at");
        sale.Property(value => value.VoidedBy).HasColumnName("voided_by").HasConversion(MemberIdConverter);
        sale.Property(value => value.CreatedAt).HasColumnName("created_at");

        sale.HasOne<CashSession>()
            .WithMany()
            .HasForeignKey(value => value.CashSessionId)
            .OnDelete(DeleteBehavior.Restrict);
        sale.HasMany(value => value.Lines)
            .WithOne()
            .HasForeignKey(line => line.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
        sale.HasMany(value => value.Payments)
            .WithOne()
            .HasForeignKey(payment => payment.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
        // Una venta sin sus líneas o sus pagos no se puede mostrar ni anular: siempre viajan juntas.
        sale.Navigation(value => value.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        sale.Navigation(value => value.Payments).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        // El contador es atómico: un 23505 aquí es un bug y se relanza sin traducir (500).
        sale.HasIndex(value => new { value.TenantId, value.SaleNumber })
            .IsUnique()
            .HasDatabaseName("IX_sales_tenant_number");
        sale.HasIndex(value => new { value.CashSessionId, value.CreatedAt }).HasDatabaseName("IX_sales_session");
        sale.HasIndex(value => new { value.TenantId, value.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_sales_tenant_created");
    }

    private static void ConfigureSaleLine(ModelBuilder modelBuilder)
    {
        var line = modelBuilder.Entity<PosSaleLine>();
        line.ToTable("sale_lines", "pos");
        line.HasKey(value => value.Id);
        line.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PosSaleLineId(value))
            .ValueGeneratedNever();
        line.Property(value => value.SaleId)
            .HasColumnName("sale_id")
            .HasConversion(id => id.Value, value => new PosSaleId(value));
        line.Property(value => value.Position).HasColumnName("position");
        // catalog.products(id), sin FK: la venta guarda snapshot y un producto borrado no invalida un ticket.
        line.Property(value => value.ProductId).HasColumnName("product_id");
        line.Property(value => value.ProductCode).HasColumnName("product_code").HasMaxLength(60);
        line.Property(value => value.ProductName).HasColumnName("product_name").HasMaxLength(200);
        line.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(10, 2);
        line.Property(value => value.UnitPrice).HasColumnName("unit_price").HasPrecision(14, 2);
        line.Property(value => value.DiscountPercentage).HasColumnName("discount_percentage").HasPrecision(5, 2);
        line.Property(value => value.TaxPercentage).HasColumnName("tax_percentage");
        line.Property(value => value.DiscountAmount).HasColumnName("discount_amount").HasPrecision(14, 2);
        line.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        line.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(14, 2);
        line.Ignore(value => value.LineTotal);
        line.HasIndex(value => new { value.SaleId, value.Position }).IsUnique();
    }

    private static void ConfigurePayment(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<PosPayment>();
        payment.ToTable("sale_payments", "pos", table =>
            table.HasCheckConstraint("CK_sale_payments_method", "method IN ('Cash','Card','Transfer')"));
        payment.HasKey(value => value.Id);
        payment.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PosPaymentId(value))
            .ValueGeneratedNever();
        payment.Property(value => value.SaleId)
            .HasColumnName("sale_id")
            .HasConversion(id => id.Value, value => new PosSaleId(value));
        payment.Property(value => value.Position).HasColumnName("position");
        payment.Property(value => value.Method).HasColumnName("method").HasConversion<string>().HasMaxLength(10);
        payment.Property(value => value.Amount).HasColumnName("amount").HasPrecision(14, 2);
        payment.Property(value => value.Tendered).HasColumnName("tendered").HasPrecision(14, 2);
        payment.Property(value => value.Reference).HasColumnName("reference").HasMaxLength(PosLimits.ReferenceMaxLength);
        payment.HasIndex(value => new { value.SaleId, value.Position }).IsUnique();
    }

    private static void ConfigureCounter(ModelBuilder modelBuilder)
    {
        var counter = modelBuilder.Entity<PosSaleNumberCounter>();
        counter.ToTable("sale_number_counters", "pos");
        counter.HasKey(value => value.TenantId);
        counter.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        counter.Property(value => value.NextValue).HasColumnName("next_value");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<PosOutboxMessage>();
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
