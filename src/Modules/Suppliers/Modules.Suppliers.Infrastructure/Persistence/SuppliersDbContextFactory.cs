using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Suppliers.Infrastructure.Persistence;

public sealed class SuppliersDbContextFactory
    : IDesignTimeDbContextFactory<SuppliersDbContext>
{
    public SuppliersDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "suppliers"))
            .Options;
        return new SuppliersDbContext(options);
    }
}
