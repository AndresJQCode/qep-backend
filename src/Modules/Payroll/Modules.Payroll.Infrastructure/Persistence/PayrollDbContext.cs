using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;

namespace Modules.Payroll.Infrastructure.Persistence;

public sealed class PayrollDbContext(DbContextOptions<PayrollDbContext> options)
    : DbContext(options)
{
    public const string Schema = "payroll";

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Sin tablas todavía: llegan con la Tarea 8. El esquema se fija para que el historial de migraciones viva en `payroll`.
        modelBuilder.HasDefaultSchema(Schema);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }
}
