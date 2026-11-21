using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Coordinadora.Application;
using Modules.Coordinadora.Infrastructure.Api;
using Modules.Coordinadora.Infrastructure.Persistence;

namespace Modules.Coordinadora.Infrastructure;

/// <summary>Persistencia, auditoría y cliente HTTP del módulo Coordinadora. Sin hosted services ni singletons con estado: nada que
/// clasificar en <c>MultiReplicaInventory</c>.</summary>
public static class CoordinadoraInfrastructureExtensions
{
    public static IServiceCollection AddCoordinadoraInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en el primer request con un error de
        // Npgsql que no explica nada.
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<CoordinadoraDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    CoordinadoraDbContext.Schema)));

        services.AddScoped<ICoordinadoraUnitOfWork, CoordinadoraUnitOfWork>();
        services.AddScoped<ICoordinadoraAuditRecorder, CoordinadoraAuditRecorder>();
        services.AddScoped<ICoordinadoraSettingsRepository, CoordinadoraSettingsRepository>();
        services.AddScoped<ICoordinadoraShipmentRepository, CoordinadoraShipmentRepository>();

        // Spec 2026-11-21 §8: las URL de los dos servidores y el xmlns de guías. Se validan al arrancar.
        services.AddOptions<CoordinadoraOptions>().Bind(configuration.GetSection(CoordinadoraOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<CoordinadoraOptions>, CoordinadoraOptionsValidator>();

        // El cliente: sin loggers (el log de HttpClient escribiría el cuerpo con las claves y el destinatario a nivel Trace) y sin
        // redirecciones (las claves viajan en el cuerpo).
        services.AddHttpClient<ICoordinadoraApi, CoordinadoraApiClient>(CoordinadoraApiClient.HttpClientName, CoordinadoraApiClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => CoordinadoraApiClient.CreatePrimaryHandler())
            .RemoveAllLoggers();

        return services;
    }
}

public static class CoordinadoraDatabaseInitializer
{
    public static async Task InitializeCoordinadoraDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoordinadoraDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
