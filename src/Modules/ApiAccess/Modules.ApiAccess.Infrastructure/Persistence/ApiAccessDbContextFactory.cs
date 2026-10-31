using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.ApiAccess.Infrastructure.Persistence;

public sealed class ApiAccessDbContextFactory
    : IDesignTimeDbContextFactory<ApiAccessDbContext>
{
    public ApiAccessDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<ApiAccessDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    ApiAccessDbContext.Schema))
            .Options;
        return new ApiAccessDbContext(options);
    }
}
