using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>El factory de diseño con el que se generan las migraciones (<c>Api.csproj</c> no referencia
/// <c>Microsoft.EntityFrameworkCore.Design</c>).</summary>
public sealed class SiigoDbContextFactory
    : IDesignTimeDbContextFactory<SiigoDbContext>
{
    public SiigoDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<SiigoDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    SiigoDbContext.Schema))
            .Options;
        return new SiigoDbContext(options);
    }
}
