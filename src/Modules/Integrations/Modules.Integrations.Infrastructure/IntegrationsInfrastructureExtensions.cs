using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.Meta;
using Modules.Integrations.Infrastructure.Persistence;
using Modules.Integrations.Infrastructure.SecretProtection;
using Modules.Integrations.Infrastructure.Verification;
using Modules.Integrations.Infrastructure.Zenvia;

namespace Modules.Integrations.Infrastructure;

public static class IntegrationsInfrastructureExtensions
{
    public static IServiceCollection AddIntegrationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en
        // el primer request con un error de Npgsql que no explica nada.
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<IntegrationsDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IntegrationsDbContext.Schema)));

        services.AddScoped<IIntegrationConnectionRepository, IntegrationConnectionRepository>();
        services.AddScoped<IConnectionRouteRepository, ConnectionRouteRepository>();
        services.AddScoped<IConnectionRoutes, ConnectionRoutes>();
        services.AddScoped<IIntegrationsUnitOfWork, IntegrationsUnitOfWork>();
        services.AddScoped<IIntegrationsAuditRecorder, IntegrationsAuditRecorder>();
        services.AddScoped<IConnectionEventPublisher, IntegrationsEventPublisher>();
        services.AddSingleton<IIntegrationProviderCatalog, IntegrationProviderCatalog>();
        // Spec, «Puertos para los consumidores»: definidos y probados; nadie los usa todavía.
        services.AddScoped<IIntegrationConnections, IntegrationConnections>();
        services.AddScoped<IConnectionHealthReporter, ConnectionHealthReporter>();

        // Spec 2026-10-08, «Secreto en reposo»: en Production ValidateOnStart exige la llave activa
        // (sin ella el pod entra en crash-loop, a propósito); fuera de producción el host arranca y
        // crear o editar responde 503.
        services.AddOptions<SecretProtectionOptions>()
            .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretProtectionOptions>, SecretProtectionOptionsValidator>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddHostedService<ConnectionSecretRekeyWorker>();

        // Spec 2026-10-08, «Probar la credencial». IHttpClientFactory con un cliente propio del
        // módulo; sin redirecciones automáticas (el token no viaja a otro host: un 3xx es «no pude
        // verificar», P13) y sin los loggers por defecto, que pueden registrar headers.
        services.AddOptions<ZenviaOptions>()
            .Bind(configuration.GetSection(ZenviaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ZenviaOptions>, ZenviaOptionsValidator>();
        services.AddHttpClient(ZenviaConnectionTester.HttpClientName, ZenviaConnectionTester.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => ZenviaConnectionTester.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddSingleton<IProviderConnectionTester, ZenviaConnectionTester>();

        // Spec 2026-10-09, decisión 3: el cliente de Graph del módulo, mismo patrón que Zenvia.
        services.AddHttpClient(MetaGraphClient.HttpClientName, MetaGraphClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => MetaGraphClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddSingleton<MetaGraphClient>();
        services.AddSingleton<IProviderConnectionTester, WhatsAppCloudConnectionTester>();
        services.AddSingleton<IConnectionTester, ConnectionTesterRegistry>();

        // Spec 2026-10-09 §9 (decisión 2): la app de Meta de toda la plataforma. En Production
        // ValidateOnStart exige las cinco claves; fuera, el módulo arranca sin ellas y D-M3 decide.
        services.AddOptions<MetaAppOptions>()
            .Bind(configuration.GetSection(MetaAppOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MetaAppOptions>, MetaAppOptionsValidator>();
        services.AddSingleton<IMetaAppSettings, MetaAppSettings>();

        return services;
    }
}
