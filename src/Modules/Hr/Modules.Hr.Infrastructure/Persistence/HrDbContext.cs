using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure.Persistence;

public sealed class HrDbContext(DbContextOptions<HrDbContext> options)
    : DbContext(options)
{
    public const string Schema = "hr";

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }
}
