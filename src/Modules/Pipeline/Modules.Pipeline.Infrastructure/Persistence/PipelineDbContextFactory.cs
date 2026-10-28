using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>Para <c>dotnet ef</c>: <c>Api.csproj</c> no referencia EF Design (CLAUDE.md, gotchas).</summary>
public sealed class PipelineDbContextFactory : IDesignTimeDbContextFactory<PipelineDbContext>
{
    public PipelineDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PipelineDbContext.Schema))
            .Options;
        return new PipelineDbContext(options);
    }
}
