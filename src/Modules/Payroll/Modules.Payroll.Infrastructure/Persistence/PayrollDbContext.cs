using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
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

    public DbSet<CompanyPayrollSettings> CompanySettings => Set<CompanyPayrollSettings>();

    public DbSet<PayrollConcept> Concepts => Set<PayrollConcept>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureCompanySettings(modelBuilder);
        ConfigureConcept(modelBuilder);
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
}
