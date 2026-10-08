using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Application;
using Modules.Pos.Infrastructure.Persistence;

namespace Modules.Pos.Infrastructure;

public static class PosInfrastructureExtensions
{
    public static IServiceCollection AddPosInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar
        // en el primer request con un error de Npgsql que no explica nada.
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<PosDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "pos")));

        services.AddScoped<ICashSessionRepository, CashSessionRepository>();
        services.AddScoped<IPosSaleRepository, PosSaleRepository>();
        services.AddScoped<IPosSaleNumberGenerator, PosSaleNumberGenerator>();
        services.AddScoped<IPosUnitOfWork, PosUnitOfWork>();
        services.AddScoped<IPosSaleIdLock, PosSaleIdLock>();
        services.AddScoped<IPosAuditPublisher, PosAuditPublisher>();
        services.AddScoped<IUserReferenceProbe, PosUserReferenceProbe>();

        return services;
    }
}
