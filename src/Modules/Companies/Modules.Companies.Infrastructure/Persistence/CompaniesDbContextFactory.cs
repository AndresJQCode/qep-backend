using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Companies.Infrastructure.Persistence;

public sealed class CompaniesDbContextFactory
    : IDesignTimeDbContextFactory<CompaniesDbContext>
{
    public CompaniesDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<CompaniesDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "companies"))
            .Options;
        return new CompaniesDbContext(options);
    }
}
