using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

public sealed class PayrollDbContext(DbContextOptions<PayrollDbContext> options)
    : DbContext(options)
{
    public const string Schema = "payroll";

    private static readonly ValueConverter<CompanyPayrollSettingsId, Guid> SettingsIdConverter =
        new(id => id.Value, value => new CompanyPayrollSettingsId(value));

    private static readonly ValueConverter<PayrollConceptId, Guid> ConceptIdConverter =
        new(id => id.Value, value => new PayrollConceptId(value));

    private static readonly ValueConverter<PayrollPeriodId, Guid> PeriodIdConverter =
        new(id => id.Value, value => new PayrollPeriodId(value));

    private static readonly ValueConverter<PeriodEventId, Guid> PeriodEventIdConverter =
        new(id => id.Value, value => new PeriodEventId(value));

    private static readonly ValueConverter<RecurringConceptId, Guid> RecurringIdConverter =
        new(id => id.Value, value => new RecurringConceptId(value));

    private static readonly ValueConverter<VacationId, Guid> VacationIdConverter =
        new(id => id.Value, value => new VacationId(value));

    // The typed months of an external vacation live as jsonb in the row: they are read and written whole, never queried.
    private static readonly ValueConverter<IReadOnlyList<ExternalVacationMonth>, string> ExternalMonthsConverter = new(
        months => JsonSerializer.Serialize(months, PayrollJson.Options),
        json => JsonSerializer.Deserialize<List<ExternalVacationMonth>>(json, PayrollJson.Options)!);

    private static readonly ValueComparer<IReadOnlyList<ExternalVacationMonth>> ExternalMonthsComparer = new(
        (left, right) => left!.SequenceEqual(right!),
        months => months.Aggregate(0, (hash, month) => HashCode.Combine(hash, month)),
        months => months.ToList());

    public DbSet<CompanyPayrollSettings> CompanySettings => Set<CompanyPayrollSettings>();

    public DbSet<PayrollConcept> Concepts => Set<PayrollConcept>();

    public DbSet<PayrollPeriod> Periods => Set<PayrollPeriod>();

    public DbSet<PeriodInput> PeriodInputs => Set<PeriodInput>();

    public DbSet<PeriodEvent> PeriodEvents => Set<PeriodEvent>();

    public DbSet<RecurringConcept> RecurringConcepts => Set<RecurringConcept>();

    public DbSet<Vacation> Vacations => Set<Vacation>();

    internal DbSet<EmployeeResultRow> EmployeeResults => Set<EmployeeResultRow>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureCompanySettings(modelBuilder);
        ConfigureConcept(modelBuilder);
        ConfigurePeriod(modelBuilder);
        ConfigurePeriodInput(modelBuilder);
        ConfigurePeriodEvent(modelBuilder);
        ConfigureEmployeeResult(modelBuilder);
        ConfigureRecurringConcept(modelBuilder);
        ConfigureVacation(modelBuilder);
    }

    // `column IN ('A','B')` para un enum guardado como texto: la base no depende de quien escribe.
    private static string In<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(',', Enum.GetNames<TEnum>().Select(name => $"'{name}'"))})";

    private static void ConfigureCompanySettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<CompanyPayrollSettings>();
        settings.ToTable("company_settings", Schema, table =>
        {
            table.HasCheckConstraint("CK_company_settings_transport_frequency", In<TransportFrequency>("transport_frequency"));
            table.HasCheckConstraint("CK_company_settings_transport_cap_base", In<TransportCapBase>("transport_cap_base"));
            table.HasCheckConstraint("CK_company_settings_overtime_base", In<OvertimeBase>("overtime_base"));
        });
        settings.HasKey(value => value.Id);
        settings.Property(value => value.Id).HasColumnName("id").HasConversion(SettingsIdConverter).ValueGeneratedNever();
        settings.Property(value => value.TenantId).HasColumnName("tenant_id");
        // FK real a companies.companies, agregada a mano en la migración (R-19): EF no modela relaciones entre DbContext de módulos.
        settings.Property(value => value.CompanyId).HasColumnName("company_id");
        settings.Property(value => value.TransportFrequency).HasColumnName("transport_frequency").HasConversion<string>().HasMaxLength(32);
        settings.Property(value => value.TransportCapBase).HasColumnName("transport_cap_base").HasConversion<string>().HasMaxLength(32);
        settings.Property(value => value.OvertimeBase).HasColumnName("overtime_base").HasConversion<string>().HasMaxLength(32);
        settings.Property(value => value.Exonerated).HasColumnName("exonerated");
        settings.Property(value => value.DualControl).HasColumnName("dual_control");
        settings.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        settings.Property(value => value.CreatedAt).HasColumnName("created_at");
        settings.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        settings.HasIndex(value => new { value.TenantId, value.CompanyId })
            .IsUnique()
            .HasDatabaseName("IX_company_settings_tenant_company");
    }

    private static void ConfigureConcept(ModelBuilder modelBuilder)
    {
        var concept = modelBuilder.Entity<PayrollConcept>();
        concept.ToTable("concepts", Schema, table =>
            table.HasCheckConstraint("CK_concepts_kind", In<ConceptKind>("kind")));
        concept.HasKey(value => value.Id);
        concept.Property(value => value.Id).HasColumnName("id").HasConversion(ConceptIdConverter).ValueGeneratedNever();
        concept.Property(value => value.TenantId).HasColumnName("tenant_id");
        concept.Property(value => value.CompanyId).HasColumnName("company_id");
        concept.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        concept.Property(value => value.Name).HasColumnName("name").HasMaxLength(80);
        concept.Property(value => value.NormalizedName).HasColumnName("normalized_name").HasMaxLength(80);
        concept.Property(value => value.DefaultSalaryConstitutive).HasColumnName("default_salary_constitutive");
        concept.Property(value => value.Multiplier).HasColumnName("multiplier").HasPrecision(18, 6);
        concept.Property(value => value.ArchivedAt).HasColumnName("archived_at");
        concept.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        concept.Property(value => value.CreatedAt).HasColumnName("created_at");
        concept.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        concept.Ignore(value => value.Archived);
        // El nombre es único por empresa y tipo, archivados incluidos.
        concept.HasIndex(value => new { value.TenantId, value.CompanyId, value.Kind, value.NormalizedName })
            .IsUnique()
            .HasDatabaseName("IX_concepts_tenant_company_kind_normalized_name");
    }

    private static void ConfigurePeriod(ModelBuilder modelBuilder)
    {
        var period = modelBuilder.Entity<PayrollPeriod>();
        period.ToTable("periods", Schema, table =>
        {
            table.HasCheckConstraint("CK_periods_half", In<PayrollHalf>("half"));
            table.HasCheckConstraint("CK_periods_status", In<PayrollPeriodStatus>("status"));
        });
        period.HasKey(value => value.Id);
        period.Property(value => value.Id).HasColumnName("id").HasConversion(PeriodIdConverter).ValueGeneratedNever();
        period.Property(value => value.TenantId).HasColumnName("tenant_id");
        // FK real a companies.companies, agregada a mano en la migración (R-19).
        period.Property(value => value.CompanyId).HasColumnName("company_id");
        period.Property(value => value.StartDate).HasColumnName("start_date");
        period.Property(value => value.EndDate).HasColumnName("end_date");
        period.Property(value => value.Half).HasColumnName("half").HasConversion<string>().HasMaxLength(32);
        period.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        period.Property(value => value.Outdated).HasColumnName("outdated");
        period.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        period.Property(value => value.LegalValuesJson).HasColumnName("legal_values").HasColumnType("jsonb");
        period.Property(value => value.LegalValuesDate).HasColumnName("legal_values_date");
        period.Property(value => value.EngineVersion).HasColumnName("engine_version").HasMaxLength(32);
        period.Property(value => value.Fingerprint).HasColumnName("fingerprint").HasMaxLength(128);
        period.Property(value => value.ExcludedJson).HasColumnName("excluded").HasColumnType("jsonb");
        period.Property(value => value.LastLiquidatedBy).HasColumnName("last_liquidated_by");
        period.Property(value => value.LastLiquidatedByName).HasColumnName("last_liquidated_by_name").HasMaxLength(200);
        period.Property(value => value.LastLiquidatedAt).HasColumnName("last_liquidated_at");
        period.Property(value => value.ApprovedBy).HasColumnName("approved_by");
        period.Property(value => value.ApprovedByName).HasColumnName("approved_by_name").HasMaxLength(200);
        period.Property(value => value.ApprovedAt).HasColumnName("approved_at");
        period.Property(value => value.ClosedBy).HasColumnName("closed_by");
        period.Property(value => value.ClosedByName).HasColumnName("closed_by_name").HasMaxLength(200);
        period.Property(value => value.ClosedAt).HasColumnName("closed_at");
        period.Property(value => value.CreatedAt).HasColumnName("created_at");
        period.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        period.HasMany(value => value.Inputs).WithOne().HasForeignKey(input => input.PeriodId).OnDelete(DeleteBehavior.Cascade);
        period.HasMany(value => value.Events).WithOne().HasForeignKey(evt => evt.PeriodId).OnDelete(DeleteBehavior.Cascade);
        period.Navigation(value => value.Inputs).UsePropertyAccessMode(PropertyAccessMode.Field);
        period.Navigation(value => value.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        // Una quincena por empresa: abrir dos veces la misma es la carrera de ensure-current (R-13).
        period.HasIndex(value => new { value.TenantId, value.CompanyId, value.StartDate })
            .IsUnique()
            .HasDatabaseName("IX_periods_tenant_company_start");
    }

    private static void ConfigurePeriodInput(ModelBuilder modelBuilder)
    {
        var input = modelBuilder.Entity<PeriodInput>();
        input.ToTable("period_inputs", Schema, table =>
            table.HasCheckConstraint("CK_period_inputs_kind", In<ConceptKind>("kind")));
        input.HasKey(value => value.Id);
        input.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        input.Property(value => value.PeriodId).HasColumnName("period_id").HasConversion(PeriodIdConverter);
        input.Property(value => value.EmployeeId).HasColumnName("employee_id");
        input.Property(value => value.ConceptId).HasColumnName("concept_id");
        input.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        input.Property(value => value.Hours).HasColumnName("hours").HasPrecision(6, 2);
        input.Property(value => value.Amount).HasColumnName("amount").HasPrecision(18, 2);
        input.Property(value => value.SalaryConstitutive).HasColumnName("salary_constitutive");
        input.Property(value => value.Position).HasColumnName("position");
        input.HasIndex(value => new { value.PeriodId, value.EmployeeId }).HasDatabaseName("IX_period_inputs_period_employee");
    }

    private static void ConfigurePeriodEvent(ModelBuilder modelBuilder)
    {
        var evt = modelBuilder.Entity<PeriodEvent>();
        evt.ToTable("period_events", Schema, table =>
        {
            table.HasCheckConstraint("CK_period_events_action", In<PeriodAction>("action"));
            table.HasCheckConstraint("CK_period_events_from_status", "from_status IS NULL OR " + In<PayrollPeriodStatus>("from_status"));
            table.HasCheckConstraint("CK_period_events_to_status", "to_status IS NULL OR " + In<PayrollPeriodStatus>("to_status"));
        });
        evt.HasKey(value => value.Id);
        evt.Property(value => value.Id).HasColumnName("id").HasConversion(PeriodEventIdConverter).ValueGeneratedNever();
        evt.Property(value => value.PeriodId).HasColumnName("period_id").HasConversion(PeriodIdConverter);
        evt.Property(value => value.Sequence).HasColumnName("sequence");
        evt.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        evt.Property(value => value.ActorSubjectId).HasColumnName("actor_subject_id");
        evt.Property(value => value.ActorName).HasColumnName("actor_name").HasMaxLength(200);
        evt.Property(value => value.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(32);
        evt.Property(value => value.FromStatus).HasColumnName("from_status").HasConversion<string>().HasMaxLength(32);
        evt.Property(value => value.ToStatus).HasColumnName("to_status").HasConversion<string>().HasMaxLength(32);
        evt.Property(value => value.Reason).HasColumnName("reason").HasMaxLength(500);
        evt.Property(value => value.AbsencesConfirmed).HasColumnName("absences_confirmed");
        evt.HasIndex(value => new { value.PeriodId, value.OccurredAt, value.Sequence }).HasDatabaseName("IX_period_events_period_occurred");
    }

    private static void ConfigureEmployeeResult(ModelBuilder modelBuilder)
    {
        var result = modelBuilder.Entity<EmployeeResultRow>();
        result.ToTable("employee_results", Schema);
        result.HasKey(value => value.Id);
        result.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        result.Property(value => value.PeriodId).HasColumnName("period_id").HasConversion(PeriodIdConverter);
        result.Property(value => value.EmployeeId).HasColumnName("employee_id");
        result.Property(value => value.DocumentType).HasColumnName("document_type").HasMaxLength(32);
        result.Property(value => value.DocumentNumber).HasColumnName("document_number").HasMaxLength(30);
        result.Property(value => value.FullName).HasColumnName("full_name").HasMaxLength(250);
        result.Property(value => value.Position).HasColumnName("position").HasMaxLength(100);
        result.Property(value => value.Workplace).HasColumnName("workplace").HasMaxLength(250);
        result.Property(value => value.WorkedDays).HasColumnName("worked_days");
        result.Property(value => value.NegativeNet).HasColumnName("negative_net");
        result.Property(value => value.FinalSettlementPending).HasColumnName("final_settlement_pending");
        result.Property(value => value.EngineVersion).HasColumnName("engine_version").HasMaxLength(32);
        result.Property(value => value.Fingerprint).HasColumnName("fingerprint").HasMaxLength(128);
        result.Property(value => value.LinesJson).HasColumnName("lines").HasColumnType("jsonb");
        result.Property(value => value.SummaryJson).HasColumnName("summary").HasColumnType("jsonb");
        result.Property(value => value.MonthJson).HasColumnName("month").HasColumnType("jsonb");
        result.Property(value => value.RecurringAppliedJson).HasColumnName("recurring_applied").HasColumnType("jsonb");
        // Additive (expand/contract): a row written before vacations existed reads 0 days and no photo.
        result.Property(value => value.VacationDays).HasColumnName("vacation_days").HasDefaultValue(0);
        result.Property(value => value.VacationPhotosJson).HasColumnName("vacation_photos").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        result.HasOne<PayrollPeriod>().WithMany().HasForeignKey(value => value.PeriodId).OnDelete(DeleteBehavior.Cascade);
        result.HasIndex(value => new { value.PeriodId, value.EmployeeId })
            .IsUnique()
            .HasDatabaseName("IX_employee_results_period_employee");
    }

    private static void ConfigureRecurringConcept(ModelBuilder modelBuilder)
    {
        var recurring = modelBuilder.Entity<RecurringConcept>();
        recurring.ToTable("recurring_concepts", Schema, table =>
            table.HasCheckConstraint("CK_recurring_concepts_mode", In<RecurringMode>("mode")));
        recurring.HasKey(value => value.Id);
        recurring.Property(value => value.Id).HasColumnName("id").HasConversion(RecurringIdConverter).ValueGeneratedNever();
        recurring.Property(value => value.TenantId).HasColumnName("tenant_id");
        recurring.Property(value => value.CompanyId).HasColumnName("company_id");
        recurring.Property(value => value.EmployeeId).HasColumnName("employee_id");
        recurring.Property(value => value.ConceptId).HasColumnName("concept_id");
        recurring.Property(value => value.Mode).HasColumnName("mode").HasConversion<string>().HasMaxLength(32);
        recurring.Property(value => value.Amount).HasColumnName("amount").HasPrecision(18, 2);
        recurring.Property(value => value.SalaryConstitutive).HasColumnName("salary_constitutive");
        recurring.Property(value => value.Cap).HasColumnName("cap").HasPrecision(18, 2);
        recurring.Property(value => value.Active).HasColumnName("active");
        recurring.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        recurring.Property(value => value.CreatedAt).HasColumnName("created_at");
        recurring.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        recurring.HasIndex(value => new { value.TenantId, value.CompanyId, value.EmployeeId })
            .HasDatabaseName("IX_recurring_concepts_tenant_company_employee");
    }

    private static void ConfigureVacation(ModelBuilder modelBuilder)
    {
        var vacation = modelBuilder.Entity<Vacation>();
        vacation.ToTable("vacations", Schema, table =>
            table.HasCheckConstraint("CK_vacations_origin", In<VacationOrigin>("origin")));
        vacation.HasKey(value => value.Id);
        vacation.Property(value => value.Id).HasColumnName("id").HasConversion(VacationIdConverter).ValueGeneratedNever();
        vacation.Property(value => value.TenantId).HasColumnName("tenant_id");
        // FK real a companies.companies, agregada a mano en la migración (R-19).
        vacation.Property(value => value.CompanyId).HasColumnName("company_id");
        vacation.Property(value => value.EmployeeId).HasColumnName("employee_id");
        vacation.Property(value => value.Origin).HasColumnName("origin").HasConversion<string>().HasMaxLength(16);
        vacation.Property(value => value.StartDate).HasColumnName("start_date");
        vacation.Property(value => value.EndDate).HasColumnName("end_date");
        vacation.Property(value => value.BusinessDays).HasColumnName("business_days");
        vacation.Property(value => value.ReturnDate).HasColumnName("return_date");
        vacation.Property(value => value.PreviousMonthIbc).HasColumnName("previous_month_ibc").HasPrecision(18, 2);
        vacation.Property(value => value.ExternalMonths).HasColumnName("external_months").HasColumnType("jsonb")
            .HasConversion(ExternalMonthsConverter, ExternalMonthsComparer).HasDefaultValueSql("'[]'::jsonb");
        vacation.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        vacation.Property(value => value.CreatedAt).HasColumnName("created_at");
        vacation.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        vacation.HasIndex(value => new { value.TenantId, value.CompanyId, value.EmployeeId, value.StartDate })
            .HasDatabaseName("IX_vacations_tenant_company_employee_start");
        // The EXCLUDE constraint EX_vacations_no_overlap (no two vacations of an employee share a day) is added by hand in the
        // migration: EF does not model it.
    }
}
