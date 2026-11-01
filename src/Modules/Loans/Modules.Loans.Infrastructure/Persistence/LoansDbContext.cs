using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Loans.Domain;

namespace Modules.Loans.Infrastructure.Persistence;

public sealed class LoansDbContext(DbContextOptions<LoansDbContext> options)
    : DbContext(options)
{
    public const string Schema = "loans";

    public DbSet<Loan> Loans => Set<Loan>();

    public DbSet<LoanInstallment> Installments => Set<LoanInstallment>();

    public DbSet<LoanCapitalPayment> CapitalPayments => Set<LoanCapitalPayment>();

    internal DbSet<TenantRun> TenantRuns => Set<TenantRun>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureLoan(modelBuilder);
        ConfigureInstallment(modelBuilder);
        ConfigureSegment(modelBuilder);
        ConfigureCapitalPayment(modelBuilder);
        ConfigureTenantRun(modelBuilder);
    }

    // Los enums van como texto de 16 y los `version` como bigint fijado a mano: un cambio futuro del tipo CLR no
    // cambia el esquema. Los CHECK repiten lo que el dominio ya valida, para que la base no dependa de quien escribe.
    private static void ConfigureLoan(ModelBuilder modelBuilder)
    {
        var loan = modelBuilder.Entity<Loan>();
        loan.ToTable("loans", Schema, table =>
        {
            table.HasCheckConstraint("CK_loans_status", "status IN ('Active','Settled')");
            table.HasCheckConstraint("CK_loans_type", "type IN ('InterestOnly')");
            table.HasCheckConstraint("CK_loans_cutoff_rule", "cutoff_rule IN ('EndOfMonth','DisbursementDay')");
            table.HasCheckConstraint("CK_loans_monthly_rate", "monthly_rate_percent > 0 AND monthly_rate_percent <= 100");
        });
        loan.HasKey(value => value.Id);
        loan.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new LoanId(value))
            .ValueGeneratedNever();
        loan.Property(value => value.TenantId).HasColumnName("tenant_id");
        // Cliente y encargado son referencias blandas: viven en otros esquemas.
        loan.Property(value => value.CustomerId).HasColumnName("customer_id");
        loan.Property(value => value.Name).HasColumnName("name").HasMaxLength(Loan.NameMaxLength);
        loan.Property(value => value.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(16);
        loan.Property(value => value.PrincipalAmount).HasColumnName("principal_amount").HasPrecision(18, 2);
        loan.Property(value => value.OutstandingPrincipal).HasColumnName("outstanding_principal").HasPrecision(18, 2);
        loan.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        loan.Property(value => value.MonthlyRatePercent).HasColumnName("monthly_rate_percent").HasPrecision(7, 4);
        loan.Property(value => value.DisbursedOn).HasColumnName("disbursed_on");
        loan.Property(value => value.CutoffRule)
            .HasColumnName("cutoff_rule")
            .HasConversion<string>()
            .HasMaxLength(16);
        loan.Property(value => value.AssigneeMemberId).HasColumnName("assignee_member_id");
        loan.Property(value => value.Note).HasColumnName("note").HasMaxLength(Loan.NoteMaxLength);
        loan.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        loan.Property(value => value.LastGeneratedPeriodEnd).HasColumnName("last_generated_period_end");
        loan.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        loan.Property(value => value.CreatedAt).HasColumnName("created_at");
        loan.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // Terms es calculada desde las columnas: no se mapea.
        loan.Ignore(value => value.Terms);
        loan.HasIndex(value => new { value.TenantId, value.Status }).HasDatabaseName("IX_loans_tenant_status");
        loan.HasIndex(value => new { value.TenantId, value.CustomerId }).HasDatabaseName("IX_loans_tenant_customer");
    }

    private static void ConfigureInstallment(ModelBuilder modelBuilder)
    {
        var installment = modelBuilder.Entity<LoanInstallment>();
        installment.ToTable("installments", Schema, table =>
        {
            table.HasCheckConstraint("CK_installments_status", "status IN ('Pending','Paid')");
            // Una cuota importada no trae el detalle del período; la generada siempre lo trae.
            table.HasCheckConstraint("CK_installments_period_days", "is_imported OR period_days IS NOT NULL");
        });
        installment.HasKey(value => value.Id);
        installment.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        installment.Property(value => value.TenantId).HasColumnName("tenant_id");
        installment.Property(value => value.LoanId)
            .HasColumnName("loan_id")
            .HasConversion(id => id.Value, value => new LoanId(value));
        installment.Property(value => value.PeriodStart).HasColumnName("period_start");
        installment.Property(value => value.PeriodEnd).HasColumnName("period_end");
        installment.Property(value => value.PeriodDays).HasColumnName("period_days");
        installment.Property(value => value.Amount).HasColumnName("amount").HasPrecision(18, 2);
        installment.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        installment.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        installment.Property(value => value.IsImported).HasColumnName("is_imported");
        installment.Property(value => value.PaidOn).HasColumnName("paid_on");
        installment.Property(value => value.PaymentNote)
            .HasColumnName("payment_note")
            .HasMaxLength(LoanInstallment.PaymentNoteMaxLength);
        installment.Property(value => value.PaidByMemberId).HasColumnName("paid_by_member_id");
        installment.Property(value => value.PaidAt).HasColumnName("paid_at");
        installment.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        installment.Property(value => value.CreatedAt).HasColumnName("created_at");
        // La deduplicación de la generación: dos réplicas que conviertan el mismo período en cuota chocan acá.
        installment.HasIndex(value => new { value.LoanId, value.PeriodStart })
            .IsUnique()
            .HasDatabaseName("IX_installments_loan_period");
        installment.HasIndex(value => new { value.TenantId, value.Status, value.PeriodEnd })
            .HasDatabaseName("IX_installments_tenant_status_end");
        // Las FK son reales: viven dentro del esquema, y borrar un préstamo sin movimiento se lleva sus cuotas pendientes.
        installment.HasOne<Loan>().WithMany().HasForeignKey(value => value.LoanId).OnDelete(DeleteBehavior.Cascade);
        installment.HasMany(value => value.Segments)
            .WithOne()
            .HasForeignKey(segment => segment.InstallmentId)
            .OnDelete(DeleteBehavior.Cascade);
        installment.Navigation(value => value.Segments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureSegment(ModelBuilder modelBuilder)
    {
        var segment = modelBuilder.Entity<LoanInstallmentSegment>();
        segment.ToTable("installment_segments", Schema);
        segment.HasKey(value => value.Id);
        segment.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        segment.Property(value => value.InstallmentId).HasColumnName("installment_id");
        segment.Property(value => value.Ordinal).HasColumnName("ordinal");
        segment.Property(value => value.StartOn).HasColumnName("start_on");
        segment.Property(value => value.EndOn).HasColumnName("end_on");
        segment.Property(value => value.Principal).HasColumnName("principal").HasPrecision(18, 2);
        segment.Property(value => value.Days).HasColumnName("days");
        segment.Property(value => value.Interest).HasColumnName("interest").HasPrecision(18, 2);
        segment.HasIndex(value => new { value.InstallmentId, value.Ordinal })
            .IsUnique()
            .HasDatabaseName("IX_installment_segments_installment_ordinal");
    }

    private static void ConfigureCapitalPayment(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<LoanCapitalPayment>();
        payment.ToTable("capital_payments", Schema, table =>
            table.HasCheckConstraint("CK_capital_payments_amount", "amount > 0"));
        payment.HasKey(value => value.Id);
        payment.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        payment.Property(value => value.TenantId).HasColumnName("tenant_id");
        payment.Property(value => value.LoanId)
            .HasColumnName("loan_id")
            .HasConversion(id => id.Value, value => new LoanId(value));
        payment.Property(value => value.Amount).HasColumnName("amount").HasPrecision(18, 2);
        payment.Property(value => value.PaidOn).HasColumnName("paid_on");
        payment.Property(value => value.Note)
            .HasColumnName("note")
            .HasMaxLength(LoanCapitalPayment.NoteMaxLength);
        payment.Property(value => value.BalanceAfter).HasColumnName("balance_after").HasPrecision(18, 2);
        payment.Property(value => value.VoidedAt).HasColumnName("voided_at");
        payment.Property(value => value.VoidReason)
            .HasColumnName("void_reason")
            .HasMaxLength(LoanCapitalPayment.VoidReasonMaxLength);
        payment.Property(value => value.CreatedAt).HasColumnName("created_at");
        payment.HasIndex(value => new { value.LoanId, value.PaidOn }).HasDatabaseName("IX_capital_payments_loan_paid_on");
        payment.HasOne<Loan>().WithMany().HasForeignKey(value => value.LoanId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureTenantRun(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<TenantRun>();
        run.ToTable("tenant_runs", Schema);
        run.HasKey(value => new { value.TenantId, value.RunDate });
        run.Property(value => value.TenantId).HasColumnName("tenant_id");
        run.Property(value => value.RunDate).HasColumnName("run_date");
        run.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        run.Property(value => value.FinishedAt).HasColumnName("finished_at");
    }
}
