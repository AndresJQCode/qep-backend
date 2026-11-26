using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Buyers.Infrastructure.Persistence;

public sealed class BuyersDbContextFactory : IDesignTimeDbContextFactory<BuyersDbContext>
{
    public BuyersDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<BuyersDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    BuyersDbContext.Schema))
            .Options;
        return new BuyersDbContext(options);
    }
}
