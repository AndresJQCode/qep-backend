using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Coordinadora.Infrastructure.Persistence;

/// <summary>El factory de diseño con el que se generan las migraciones (<c>Api.csproj</c> no referencia
/// <c>Microsoft.EntityFrameworkCore.Design</c>).</summary>
public sealed class CoordinadoraDbContextFactory : IDesignTimeDbContextFactory<CoordinadoraDbContext>
{
    public CoordinadoraDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<CoordinadoraDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    CoordinadoraDbContext.Schema))
            .Options;
        return new CoordinadoraDbContext(options);
    }
}
