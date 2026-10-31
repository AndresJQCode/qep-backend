using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

public sealed class ServiceContractsDbContextFactory
    : IDesignTimeDbContextFactory<ServiceContractsDbContext>
{
    public ServiceContractsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<ServiceContractsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "billing"))
            .Options;
        return new ServiceContractsDbContext(options);
    }
}
