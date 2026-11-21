using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

public sealed class HrDbContext(DbContextOptions<HrDbContext> options)
    : DbContext(options)
{
    public const string Schema = "hr";

    public DbSet<LegalParameterValue> LegalParameters => Set<LegalParameterValue>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureLegalParameter(modelBuilder);
    }

    // Dato de la plataforma, sin tenant (patrón de catalog.tax_unit_values): el valor vigente a una fecha es el de mayor
    // effective_from que no la pasa. Los enums van como texto con CHECK, para que la base no dependa de quien escribe.
    private static void ConfigureLegalParameter(ModelBuilder modelBuilder)
    {
        var parameter = modelBuilder.Entity<LegalParameterValue>();
        parameter.ToTable("legal_parameters", Schema, table =>
            table.HasCheckConstraint("CK_legal_parameters_key", "key IN ('Smmlv','IntegralSalaryFloor')"));
        parameter.HasKey(value => new { value.Key, value.EffectiveFrom });
        parameter.Property(value => value.Key)
            .HasColumnName("key")
            .HasConversion<string>()
            .HasMaxLength(32);
        parameter.Property(value => value.EffectiveFrom).HasColumnName("effective_from");
        parameter.Property(value => value.Value).HasColumnName("value").HasPrecision(18, 2);
    }
}
