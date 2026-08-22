using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Geography.Infrastructure.Persistence;

public sealed class GeographyDbContextFactory
    : IDesignTimeDbContextFactory<GeographyDbContext>
{
    public GeographyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<GeographyDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "geography"))
            .Options;
        return new GeographyDbContext(options);
    }
}
