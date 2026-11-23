using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

public sealed class HrDbContext(DbContextOptions<HrDbContext> options)
    : DbContext(options)
{
    public const string Schema = "hr";

    private static readonly ValueConverter<SiteId, Guid> SiteIdConverter = new(id => id.Value, value => new SiteId(value));
    private static readonly ValueConverter<WorkplaceId, Guid> WorkplaceIdConverter = new(id => id.Value, value => new WorkplaceId(value));
    private static readonly ValueConverter<AreaId, Guid> AreaIdConverter = new(id => id.Value, value => new AreaId(value));
    private static readonly ValueConverter<PositionId, Guid> PositionIdConverter = new(id => id.Value, value => new PositionId(value));
    private static readonly ValueConverter<EmployeeId, Guid> EmployeeIdConverter = new(id => id.Value, value => new EmployeeId(value));
    private static readonly ValueConverter<EmploymentContractId, Guid> ContractIdConverter = new(id => id.Value, value => new EmploymentContractId(value));
    private static readonly ValueConverter<SalaryRecordId, Guid> SalaryIdConverter = new(id => id.Value, value => new SalaryRecordId(value));
    private static readonly ValueConverter<AssignmentRecordId, Guid> AssignmentIdConverter = new(id => id.Value, value => new AssignmentRecordId(value));
    private static readonly ValueConverter<ContractRenewalId, Guid> RenewalIdConverter = new(id => id.Value, value => new ContractRenewalId(value));

    public DbSet<LegalParameterValue> LegalParameters => Set<LegalParameterValue>();

    public DbSet<LegalParameterBracket> LegalParameterBrackets => Set<LegalParameterBracket>();

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<Workplace> Workplaces => Set<Workplace>();

    public DbSet<Area> Areas => Set<Area>();

    public DbSet<Position> Positions => Set<Position>();

    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<EmploymentContract> Contracts => Set<EmploymentContract>();

    public DbSet<SalaryRecord> SalaryRecords => Set<SalaryRecord>();

    public DbSet<AssignmentRecord> AssignmentRecords => Set<AssignmentRecord>();

    public DbSet<ContractRenewal> ContractRenewals => Set<ContractRenewal>();

    internal DbSet<EmployeeActivityRow> Activity => Set<EmployeeActivityRow>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureLegalParameter(modelBuilder);
        ConfigureSite(modelBuilder);
        ConfigureWorkplace(modelBuilder);
        ConfigureArea(modelBuilder);
        ConfigurePosition(modelBuilder);
        ConfigureEmployee(modelBuilder);
        ConfigureContract(modelBuilder);
        ConfigureSalary(modelBuilder);
        ConfigureAssignment(modelBuilder);
        ConfigureRenewal(modelBuilder);
        ConfigureActivity(modelBuilder);
    }

    // `column IN ('A','B')` para un enum guardado como texto: la base no depende de quien escribe.
    private static string In<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(',', Enum.GetNames<TEnum>().Select(name => $"'{name}'"))})";

    // Dato de la plataforma, sin tenant (patrón de catalog.tax_unit_values): el valor vigente a una fecha es el de mayor
    // effective_from que no la pasa. Los enums van como texto con CHECK, para que la base no dependa de quien escribe.
    private static void ConfigureLegalParameter(ModelBuilder modelBuilder)
    {
        var parameter = modelBuilder.Entity<LegalParameterValue>();
        parameter.ToTable("legal_parameters", Schema, table =>
            table.HasCheckConstraint("CK_legal_parameters_key", In<LegalParameterKey>("key")));
        parameter.HasKey(value => new { value.Key, value.EffectiveFrom });
        parameter.Property(value => value.Key)
            .HasColumnName("key")
            .HasConversion<string>()
            .HasMaxLength(32);
        parameter.Property(value => value.EffectiveFrom).HasColumnName("effective_from");
        // Seis decimales: las tasas (0.00522) y las fracciones caben sin redondeo (R-1 de nómina).
        parameter.Property(value => value.Value).HasColumnName("value").HasPrecision(18, 6);

        var bracket = modelBuilder.Entity<LegalParameterBracket>();
        bracket.ToTable("legal_parameter_brackets", Schema, table =>
            table.HasCheckConstraint("CK_legal_parameter_brackets_key", In<LegalBracketKey>("key")));
        bracket.HasKey(value => new { value.Key, value.EffectiveFrom, value.LowerBound });
        bracket.Property(value => value.Key)
            .HasColumnName("key")
            .HasConversion<string>()
            .HasMaxLength(32);
        bracket.Property(value => value.EffectiveFrom).HasColumnName("effective_from");
        bracket.Property(value => value.LowerBound).HasColumnName("lower_bound").HasPrecision(9, 4);
        bracket.Property(value => value.UpperBound).HasColumnName("upper_bound").HasPrecision(9, 4);
        bracket.Property(value => value.Rate).HasColumnName("rate").HasPrecision(18, 6);
    }

    // Lo común de las cuatro configuraciones: nombre con su forma normalizada, archivado y la versión de `If-Match`.
    private static void ConfigureSetting<T>(EntityTypeBuilder<T> setting)
        where T : OrganizationSetting
    {
        setting.Property(value => value.TenantId).HasColumnName("tenant_id");
        setting.Property(value => value.Name).HasColumnName("name").HasMaxLength(100);
        setting.Property(value => value.NormalizedName).HasColumnName("normalized_name").HasMaxLength(100);
        setting.Property(value => value.ArchivedAt).HasColumnName("archived_at");
        setting.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        setting.Property(value => value.CreatedAt).HasColumnName("created_at");
        setting.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        setting.Ignore(value => value.Archived);
    }

    private static void ConfigureSite(ModelBuilder modelBuilder)
    {
        var site = modelBuilder.Entity<Site>();
        site.ToTable("sites", Schema);
        site.HasKey(value => value.Id);
        site.Property(value => value.Id).HasColumnName("id").HasConversion(SiteIdConverter).ValueGeneratedNever();
        ConfigureSetting(site);
        // FK real a companies.companies, agregada a mano en la migración (D-11): EF no modela relaciones entre DbContext de
        // módulos distintos. Una empresa con sedes no se borra: sale como 422 companies.company.in_use.
        site.Property(value => value.CompanyId).HasColumnName("company_id");
        site.Property(value => value.Address).HasColumnName("address").HasMaxLength(200);
        // geography.cities(id), sin FK: otro módulo.
        site.Property(value => value.CityId).HasColumnName("city_id");
        site.Property(value => value.CompensationFund).HasColumnName("compensation_fund").HasMaxLength(120);
        // El nombre de una sede es único por empresa: dos empresas del tenant pueden tener cada una su «Principal».
        site.HasIndex(value => new { value.TenantId, value.CompanyId, value.NormalizedName })
            .IsUnique()
            .HasDatabaseName("IX_sites_tenant_company_normalized_name");
        site.HasIndex(value => value.CompanyId).HasDatabaseName("IX_sites_company_id");
    }

    private static void ConfigureWorkplace(ModelBuilder modelBuilder)
    {
        var workplace = modelBuilder.Entity<Workplace>();
        workplace.ToTable("workplaces", Schema, table =>
            table.HasCheckConstraint("CK_workplaces_risk_class", In<ArlRiskClass>("risk_class")));
        workplace.HasKey(value => value.Id);
        workplace.Property(value => value.Id).HasColumnName("id").HasConversion(WorkplaceIdConverter).ValueGeneratedNever();
        ConfigureSetting(workplace);
        workplace.Property(value => value.SiteId).HasColumnName("site_id").HasConversion(SiteIdConverter);
        workplace.Property(value => value.CiiuCode).HasColumnName("ciiu_code").HasMaxLength(4);
        workplace.Property(value => value.RiskClass).HasColumnName("risk_class").HasConversion<string>().HasMaxLength(32);
        workplace.Property(value => value.HighRiskPension).HasColumnName("high_risk_pension");
        workplace.HasOne<Site>().WithMany().HasForeignKey(value => value.SiteId).OnDelete(DeleteBehavior.Restrict);
        workplace.HasIndex(value => new { value.TenantId, value.SiteId, value.NormalizedName })
            .IsUnique()
            .HasDatabaseName("IX_workplaces_tenant_site_normalized_name");
    }

    private static void ConfigureArea(ModelBuilder modelBuilder)
    {
        var area = modelBuilder.Entity<Area>();
        area.ToTable("areas", Schema);
        area.HasKey(value => value.Id);
        area.Property(value => value.Id).HasColumnName("id").HasConversion(AreaIdConverter).ValueGeneratedNever();
        ConfigureSetting(area);
        area.HasIndex(value => new { value.TenantId, value.NormalizedName })
            .IsUnique()
            .HasDatabaseName("IX_areas_tenant_normalized_name");
    }

    private static void ConfigurePosition(ModelBuilder modelBuilder)
    {
        var position = modelBuilder.Entity<Position>();
        position.ToTable("positions", Schema);
        position.HasKey(value => value.Id);
        position.Property(value => value.Id).HasColumnName("id").HasConversion(PositionIdConverter).ValueGeneratedNever();
        ConfigureSetting(position);
        position.HasIndex(value => new { value.TenantId, value.NormalizedName })
            .IsUnique()
            .HasDatabaseName("IX_positions_tenant_normalized_name");
    }

    private static void ConfigureEmployee(ModelBuilder modelBuilder)
    {
        var employee = modelBuilder.Entity<Employee>();
        employee.ToTable("employees", Schema, table =>
        {
            table.HasCheckConstraint("CK_employees_document_type", In<HrDocumentType>("document_type"));
            table.HasCheckConstraint("CK_employees_contributor_subtype", In<ContributorSubtype>("contributor_subtype"));
            table.HasCheckConstraint("CK_employees_payment_method", "payment_method IS NULL OR " + In<PaymentMethod>("payment_method"));
            table.HasCheckConstraint("CK_employees_bank_account_type", "bank_account_type IS NULL OR " + In<BankAccountType>("bank_account_type"));
            table.HasCheckConstraint("CK_employees_status", In<EmployeeStatus>("status"));
        });
        employee.HasKey(value => value.Id);
        employee.Property(value => value.Id).HasColumnName("id").HasConversion(EmployeeIdConverter).ValueGeneratedNever();
        employee.Property(value => value.TenantId).HasColumnName("tenant_id");
        employee.Property(value => value.FirstName).HasColumnName("first_name").HasMaxLength(100);
        employee.Property(value => value.LastName).HasColumnName("last_name").HasMaxLength(100);
        employee.Property(value => value.DocumentType).HasColumnName("document_type").HasConversion<string>().HasMaxLength(32);
        employee.Property(value => value.DocumentNumber).HasColumnName("document_number").HasMaxLength(30);
        // Nacionalidad (ISO 3166-1 alfa-2). Sin FK: el catálogo de Geography se consulta por puerto, como member_id y city_id.
        employee.Property(value => value.Country).HasColumnName("country").HasMaxLength(2);
        employee.Property(value => value.BirthDate).HasColumnName("birth_date");
        employee.Property(value => value.Phone).HasColumnName("phone").HasMaxLength(32);
        employee.Property(value => value.PersonalEmail).HasColumnName("personal_email").HasMaxLength(254);
        employee.Property(value => value.WorkEmail).HasColumnName("work_email").HasMaxLength(254);
        employee.Property(value => value.Address).HasColumnName("address").HasMaxLength(200);
        employee.Property(value => value.CityId).HasColumnName("city_id");
        // tenancy.memberships(id), sin FK: otro módulo.
        employee.Property(value => value.MemberId).HasColumnName("member_id");
        employee.Property(value => value.HealthProvider).HasColumnName("health_provider").HasMaxLength(120);
        employee.Property(value => value.PensionFund).HasColumnName("pension_fund").HasMaxLength(120);
        employee.Property(value => value.SeveranceFund).HasColumnName("severance_fund").HasMaxLength(120);
        employee.Property(value => value.OccupationalRiskProvider).HasColumnName("occupational_risk_provider").HasMaxLength(120);
        employee.Property(value => value.ContributorSubtype).HasColumnName("contributor_subtype").HasConversion<string>().HasMaxLength(32);
        employee.Property(value => value.ForeignPensionExempt).HasColumnName("foreign_pension_exempt");
        employee.Property(value => value.RestDays)
            .HasColumnName("rest_days")
            .HasMaxLength(80)
            .HasConversion(
                days => RestDaysToText(days),
                text => (IReadOnlyList<DayOfWeek>)RestDaysFromText(text),
                new ValueComparer<IReadOnlyList<DayOfWeek>>(
                    (left, right) => left!.SequenceEqual(right!),
                    days => days.Aggregate(0, (hash, day) => HashCode.Combine(hash, day)),
                    days => (IReadOnlyList<DayOfWeek>)days.ToArray()));
        employee.Property(value => value.PaymentMethod).HasColumnName("payment_method").HasConversion<string>().HasMaxLength(32);
        employee.Property(value => value.BankName).HasColumnName("bank_name").HasMaxLength(80);
        employee.Property(value => value.BankAccountType).HasColumnName("bank_account_type").HasConversion<string>().HasMaxLength(32);
        employee.Property(value => value.BankAccountNumber).HasColumnName("bank_account_number").HasMaxLength(17);
        employee.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        employee.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        employee.Property(value => value.CreatedAt).HasColumnName("created_at");
        employee.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        employee.Ignore(value => value.Payment);
        employee.Ignore(value => value.ActiveContract);
        employee.Ignore(value => value.CanDelete);
        employee.HasMany(value => value.Contracts)
            .WithOne()
            .HasForeignKey(contract => contract.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);
        employee.Navigation(value => value.Contracts).UsePropertyAccessMode(PropertyAccessMode.Field);
        employee.HasIndex(value => new { value.TenantId, value.DocumentNumber })
            .IsUnique()
            .HasDatabaseName("IX_employees_tenant_document_number");
        employee.HasIndex(value => new { value.TenantId, value.MemberId })
            .IsUnique()
            .HasFilter("member_id IS NOT NULL")
            .HasDatabaseName("IX_employees_tenant_member_id");
        employee.HasIndex(value => new { value.TenantId, value.Status, value.LastName, value.FirstName })
            .HasDatabaseName("IX_employees_tenant_status_name");
    }

    private static string RestDaysToText(IReadOnlyList<DayOfWeek> days) => string.Join(',', days.Select(day => day.ToString()));

    private static DayOfWeek[] RestDaysFromText(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => Enum.Parse<DayOfWeek>(name)).ToArray();

    private static void ConfigureContract(ModelBuilder modelBuilder)
    {
        var contract = modelBuilder.Entity<EmploymentContract>();
        contract.ToTable("employment_contracts", Schema, table =>
        {
            table.HasCheckConstraint("CK_employment_contracts_contract_type", In<ContractType>("contract_type"));
            table.HasCheckConstraint("CK_employment_contracts_duration", In<ContractDuration>("duration"));
            table.HasCheckConstraint("CK_employment_contracts_salary_type", In<SalaryType>("salary_type"));
            table.HasCheckConstraint("CK_employment_contracts_status", In<ContractStatus>("status"));
            table.HasCheckConstraint(
                "CK_employment_contracts_termination_reason", "termination_reason IS NULL OR " + In<TerminationReason>("termination_reason"));
        });
        contract.HasKey(value => value.Id);
        contract.Property(value => value.Id).HasColumnName("id").HasConversion(ContractIdConverter).ValueGeneratedNever();
        contract.Property(value => value.EmployeeId).HasColumnName("employee_id").HasConversion(EmployeeIdConverter);
        // FK real a companies.companies, agregada a mano en la migración (D-11): EF no modela relaciones entre DbContext de
        // módulos distintos.
        contract.Property(value => value.CompanyId).HasColumnName("company_id");
        contract.Property(value => value.ContractType).HasColumnName("contract_type").HasConversion<string>().HasMaxLength(32);
        contract.Property(value => value.Duration).HasColumnName("duration").HasConversion<string>().HasMaxLength(32);
        contract.Property(value => value.SalaryType).HasColumnName("salary_type").HasConversion<string>().HasMaxLength(32);
        contract.Property(value => value.StartDate).HasColumnName("start_date");
        contract.Property(value => value.EndDate).HasColumnName("end_date");
        contract.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        contract.Property(value => value.EndedOn).HasColumnName("ended_on");
        contract.Property(value => value.ConvertedToIndefiniteOn).HasColumnName("converted_to_indefinite_on");
        contract.Property(value => value.PreviousContractId).HasColumnName("previous_contract_id").HasConversion(ContractIdConverter);
        contract.Property(value => value.DocumentFileId).HasColumnName("document_file_id");
        contract.Property(value => value.DocumentFileName).HasColumnName("document_file_name").HasMaxLength(255);
        contract.Property(value => value.TerminationReason).HasColumnName("termination_reason").HasConversion<string>().HasMaxLength(32);
        contract.Property(value => value.TerminationNotes).HasColumnName("termination_notes").HasMaxLength(1000);
        contract.Property(value => value.CreatedAt).HasColumnName("created_at");
        contract.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        contract.HasOne<EmploymentContract>()
            .WithMany()
            .HasForeignKey(value => value.PreviousContractId)
            .OnDelete(DeleteBehavior.Restrict);
        contract.HasMany(value => value.Salaries).WithOne().HasForeignKey(record => record.ContractId).OnDelete(DeleteBehavior.Cascade);
        contract.HasMany(value => value.Assignments).WithOne().HasForeignKey(record => record.ContractId).OnDelete(DeleteBehavior.Cascade);
        contract.HasMany(value => value.Renewals).WithOne().HasForeignKey(record => record.ContractId).OnDelete(DeleteBehavior.Cascade);
        contract.Navigation(value => value.Salaries).UsePropertyAccessMode(PropertyAccessMode.Field);
        contract.Navigation(value => value.Assignments).UsePropertyAccessMode(PropertyAccessMode.Field);
        contract.Navigation(value => value.Renewals).UsePropertyAccessMode(PropertyAccessMode.Field);
        // A lo sumo un contrato vigente por empleado. Parcial: los cerrados se acumulan sin límite.
        contract.HasIndex(value => value.EmployeeId)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName("IX_employment_contracts_employee_active");
        contract.HasIndex(value => value.CompanyId).HasDatabaseName("IX_employment_contracts_company_id");
    }

    private static void ConfigureSalary(ModelBuilder modelBuilder)
    {
        var salary = modelBuilder.Entity<SalaryRecord>();
        salary.ToTable("salary_records", Schema);
        salary.HasKey(value => value.Id);
        salary.Property(value => value.Id).HasColumnName("id").HasConversion(SalaryIdConverter).ValueGeneratedNever();
        salary.Property(value => value.ContractId).HasColumnName("contract_id").HasConversion(ContractIdConverter);
        salary.Property(value => value.EffectiveDate).HasColumnName("effective_date");
        salary.Property(value => value.Amount).HasColumnName("amount").HasPrecision(18, 2);
        salary.Property(value => value.Teleworker).HasColumnName("teleworker");
        salary.Property(value => value.BelowMinimumAcknowledged).HasColumnName("below_minimum_acknowledged");
        salary.Property(value => value.DocumentFileId).HasColumnName("document_file_id");
        salary.Property(value => value.DocumentFileName).HasColumnName("document_file_name").HasMaxLength(255);
        salary.Property(value => value.CreatedAt).HasColumnName("created_at");
        salary.Property(value => value.UpdatedAt).HasColumnName("updated_at");
    }

    private static void ConfigureAssignment(ModelBuilder modelBuilder)
    {
        var assignment = modelBuilder.Entity<AssignmentRecord>();
        assignment.ToTable("assignment_records", Schema);
        assignment.HasKey(value => value.Id);
        assignment.Property(value => value.Id).HasColumnName("id").HasConversion(AssignmentIdConverter).ValueGeneratedNever();
        assignment.Property(value => value.ContractId).HasColumnName("contract_id").HasConversion(ContractIdConverter);
        assignment.Property(value => value.EffectiveDate).HasColumnName("effective_date");
        assignment.Property(value => value.PositionId).HasColumnName("position_id").HasConversion(PositionIdConverter);
        assignment.Property(value => value.AreaId).HasColumnName("area_id").HasConversion(AreaIdConverter);
        assignment.Property(value => value.SiteId).HasColumnName("site_id").HasConversion(SiteIdConverter);
        assignment.Property(value => value.WorkplaceId).HasColumnName("workplace_id").HasConversion(WorkplaceIdConverter);
        assignment.Property(value => value.DocumentFileId).HasColumnName("document_file_id");
        assignment.Property(value => value.DocumentFileName).HasColumnName("document_file_name").HasMaxLength(255);
        assignment.Property(value => value.CreatedAt).HasColumnName("created_at");
        // Una configuración usada por un registro no se elimina (restrict): por eso el repositorio dice qué está en uso.
        assignment.HasOne<Position>().WithMany().HasForeignKey(value => value.PositionId).OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne<Area>().WithMany().HasForeignKey(value => value.AreaId).OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne<Site>().WithMany().HasForeignKey(value => value.SiteId).OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne<Workplace>().WithMany().HasForeignKey(value => value.WorkplaceId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRenewal(ModelBuilder modelBuilder)
    {
        var renewal = modelBuilder.Entity<ContractRenewal>();
        renewal.ToTable("contract_renewals", Schema);
        renewal.HasKey(value => value.Id);
        renewal.Property(value => value.Id).HasColumnName("id").HasConversion(RenewalIdConverter).ValueGeneratedNever();
        renewal.Property(value => value.ContractId).HasColumnName("contract_id").HasConversion(ContractIdConverter);
        renewal.Property(value => value.PreviousEndDate).HasColumnName("previous_end_date");
        renewal.Property(value => value.NewEndDate).HasColumnName("new_end_date");
        renewal.Property(value => value.IsShort).HasColumnName("is_short");
        renewal.Property(value => value.DocumentFileId).HasColumnName("document_file_id");
        renewal.Property(value => value.DocumentFileName).HasColumnName("document_file_name").HasMaxLength(255);
        renewal.Property(value => value.CreatedAt).HasColumnName("created_at");
    }

    private static void ConfigureActivity(ModelBuilder modelBuilder)
    {
        var activity = modelBuilder.Entity<EmployeeActivityRow>();
        activity.ToTable("employee_activity", Schema, table =>
        {
            table.HasCheckConstraint("CK_employee_activity_action", In<HrActivityAction>("action"));
            table.HasCheckConstraint("CK_employee_activity_category", In<HrActivityCategory>("category"));
        });
        activity.HasKey(value => value.Id);
        activity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        activity.Property(value => value.TenantId).HasColumnName("tenant_id");
        activity.Property(value => value.EmployeeId).HasColumnName("employee_id").HasConversion(EmployeeIdConverter);
        activity.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        activity.Property(value => value.ActorSubjectId).HasColumnName("actor_subject_id");
        activity.Property(value => value.ActorName).HasColumnName("actor_name").HasMaxLength(200);
        activity.Property(value => value.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(32);
        activity.Property(value => value.Category).HasColumnName("category").HasConversion<string>().HasMaxLength(32);
        activity.Property(value => value.ContractId).HasColumnName("contract_id");
        // Lista de { field, before, after, sensitive }: se lee entera, nunca se consulta por dentro.
        activity.Property(value => value.Changes).HasColumnName("changes").HasColumnType("jsonb");
        activity.HasOne<Employee>().WithMany().HasForeignKey(value => value.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        activity.HasIndex(value => new { value.EmployeeId, value.OccurredAt }).HasDatabaseName("IX_employee_activity_employee_occurred");
    }
}
