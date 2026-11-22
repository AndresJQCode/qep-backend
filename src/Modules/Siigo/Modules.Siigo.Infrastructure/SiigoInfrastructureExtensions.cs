using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Infrastructure.Api;
using Modules.Siigo.Infrastructure.Batches;
using Modules.Siigo.Infrastructure.Persistence;

namespace Modules.Siigo.Infrastructure;

/// <summary>Persistencia, auditoría y cliente HTTP del módulo Siigo, y el worker de las tandas de facturación; el único singleton con
/// estado es <see cref="SiigoTokenCache"/>, clasificado en <c>MultiReplicaInventory</c>.</summary>
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
        services.AddScoped<ISiigoSettingsRepository, SiigoSettingsRepository>();
        services.AddScoped<ISiigoInvoiceRepository, SiigoInvoiceRepository>();
        services.AddScoped<ISiigoCallFailureLog, SiigoCallFailureLog>();
        services.AddScoped<ISiigoCallFailureRepository, SiigoCallFailureRepository>();
        services.AddScoped<ISiigoInvoiceBatchRepository, SiigoInvoiceBatchRepository>();

        // Las tandas de facturación: un worker por réplica, con timbre. Quien factura cada pedido (ISiigoBatchItemInvoker) vive en
        // Bootstrapper, porque corre el handler como la persona que pidió la tanda.
        services.Configure<SiigoInvoiceBatchOptions>(configuration.GetSection(SiigoInvoiceBatchOptions.SectionName));
        services.AddHostedService<SiigoInvoiceBatchWorker>();

        // Spec 2026-11-18 §8: Siigo:BaseUrl y Siigo:PartnerId. Se validan al arrancar (en Production, sin Partner-Id el pod no arranca).
        services.AddOptions<SiigoOptions>().Bind(configuration.GetSection(SiigoOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SiigoOptions>, SiigoOptionsValidator>();

        // El cliente de la API: sin loggers (el log de HttpClient escribiría el Authorization y la URL con la identificación del
        // cliente) y sin redirecciones (la clave de acceso viaja en el cuerpo de /auth). El token por conexión vive en un singleton con
        // estado en memoria: un token de más por réplica no tiene efecto (MultiReplicaInventory).
        services.AddMemoryCache();
        // El caché de tokens es singleton y no ve IClock (scoped): lee la hora del TimeProvider, el del sistema si nadie registró otro.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SiigoTokenCache>();
        // Contadores de fallas por pod (qep_siigo_call_failures_total); Prometheus los suma entre réplicas (MultiReplicaInventory).
        services.AddSingleton<SiigoMetrics>();
        services.AddHttpClient<ISiigoApi, SiigoApiClient>(SiigoApiClient.HttpClientName, SiigoApiClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => SiigoApiClient.CreatePrimaryHandler())
            .RemoveAllLoggers();

        return services;
    }
}
