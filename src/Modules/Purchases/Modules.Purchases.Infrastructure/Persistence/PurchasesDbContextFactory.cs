using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Purchases.Infrastructure.Persistence;

public sealed class PurchasesDbContextFactory
    : IDesignTimeDbContextFactory<PurchasesDbContext>
{
    public PurchasesDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<PurchasesDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    PurchasesDbContext.Schema))
            .Options;
        return new PurchasesDbContext(options);
    }
}
