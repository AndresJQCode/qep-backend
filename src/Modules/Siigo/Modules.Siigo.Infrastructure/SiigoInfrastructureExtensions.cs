using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Siigo.Application;
using Modules.Siigo.Infrastructure.Persistence;

namespace Modules.Siigo.Infrastructure;

/// <summary>Persistencia y auditoría del módulo Siigo. Sin hosted services ni singletons con estado (nada que clasificar en
/// <c>MultiReplicaInventory</c>).</summary>
public static class SiigoInfrastructureExtensions
{
    public static IServiceCollection AddSiigoInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en el primer request con un error de
        // Npgsql que no explica nada.
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<SiigoDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    SiigoDbContext.Schema)));

        services.AddScoped<ISiigoUnitOfWork, SiigoUnitOfWork>();
        services.AddScoped<ISiigoAuditRecorder, SiigoAuditRecorder>();

        return services;
    }
}
