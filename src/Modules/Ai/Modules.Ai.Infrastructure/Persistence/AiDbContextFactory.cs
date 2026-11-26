using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Ai.Infrastructure.Persistence;

/// <summary>Para <c>dotnet ef</c>: <c>Api.csproj</c> no referencia EF Design (CLAUDE.md, gotchas).</summary>
public sealed class AiDbContextFactory : IDesignTimeDbContextFactory<AiDbContext>
{
    public AiDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<AiDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AiDbContext.Schema))
            .Options;
        return new AiDbContext(options);
    }
}
