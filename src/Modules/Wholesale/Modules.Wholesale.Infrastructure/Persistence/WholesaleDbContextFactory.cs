using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Wholesale.Infrastructure.Persistence;

public sealed class WholesaleDbContextFactory
    : IDesignTimeDbContextFactory<WholesaleDbContext>
{
    public WholesaleDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<WholesaleDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    WholesaleDbContext.Schema))
            .Options;
        return new WholesaleDbContext(options);
    }
}
