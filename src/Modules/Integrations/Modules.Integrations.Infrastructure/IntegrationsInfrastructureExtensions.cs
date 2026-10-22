using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Infrastructure.ExternalApi;
using Modules.Integrations.Infrastructure.Meta;
using Modules.Integrations.Infrastructure.Persistence;
using Modules.Integrations.Infrastructure.SecretProtection;
using Modules.Integrations.Infrastructure.Shopify;
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
        // Spec 2026-10-09 §8.1: canje, registro, suscripción y lectura del número, sobre el mismo cliente.
        services.AddScoped<IWhatsAppSignupGateway, MetaGraphSignupGateway>();
        // Spec 2026-10-14 §6.1: el cliente de Graph de Instagram (graph.instagram.com), mismo patrón; el canje del code,
        // el token largo, /me y la suscripción sobre él; el probador y el worker diario de refresco del token.
        services.AddHttpClient(InstagramGraphClient.HttpClientName, InstagramGraphClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => InstagramGraphClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddSingleton<InstagramGraphClient>();
        services.AddSingleton<IProviderConnectionTester, InstagramConnectionTester>();
        services.AddScoped<IInstagramLoginGateway, InstagramLoginGateway>();
        services.AddOptions<InstagramRefreshOptions>().Bind(configuration.GetSection(InstagramRefreshOptions.SectionName));
        services.AddHostedService<InstagramTokenRefreshWorker>();
        // Spec 2026-10-21 D-H2/D-H3: el probador de external-api, con el handler de la política anti-SSRF (sin redirecciones
        // ni proxy, y ConnectCallback que no conecta a un rango interno) y sin los loggers, que pueden registrar el header de
        // la clave (D-H5).
        services.AddHttpClient(ExternalApiConnectionTester.HttpClientName, ExternalApiConnectionTester.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => ExternalApiUrlPolicy.CreateHandler())
            .RemoveAllLoggers();
        services.AddSingleton<IProviderConnectionTester, ExternalApiConnectionTester>();
        services.AddSingleton<IConnectionTester, ConnectionTesterRegistry>();

        // Spec 2026-10-09 §9 (decisión 2): la app de Meta de toda la plataforma. En Production
        // ValidateOnStart exige las cinco claves; fuera, el módulo arranca sin ellas y D-M3 decide.
        services.AddOptions<MetaAppOptions>()
            .Bind(configuration.GetSection(MetaAppOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MetaAppOptions>, MetaAppOptionsValidator>();
        services.AddSingleton<IMetaAppSettings, MetaAppSettings>();

        // Spec 2026-10-14 §9 (V-1 cerrada): el caso de uso Instagram tiene sus propias claves. En Production
        // ValidateOnStart exige AppId, AppSecret y un origen de retorno; fuera, el proveedor sale del catálogo.
        services.AddOptions<MetaInstagramOptions>()
            .Bind(configuration.GetSection(MetaInstagramOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MetaInstagramOptions>, MetaInstagramOptionsValidator>();
        services.AddSingleton<IInstagramAppSettings, InstagramAppSettings>();

        // Spec 2026-10-15 §4.2: la app de Shopify de toda la plataforma. En Production ValidateOnStart exige el par de la
        // app, la URL del webhook y un origen de retorno (sin ellos el pod entra en crash-loop, a propósito, como con
        // Meta); fuera, el módulo arranca sin ellos y D-M3 saca a shopify del catálogo.
        services.AddOptions<ShopifyAppOptions>()
            .Bind(configuration.GetSection(ShopifyAppOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ShopifyAppOptions>, ShopifyAppOptionsValidator>();
        services.AddSingleton<IShopifyAppSettings, ShopifyAppSettings>();
        // Spec 2026-10-15 §4.3: la instalación en curso, en la misma unidad de trabajo que las conexiones.
        services.AddScoped<IShopifyInstallRepository, ShopifyInstallRepository>();
        // Spec 2026-10-15 §4.3 paso 4 y §4.1: el canje, la Admin API y los webhooks de cada tienda, con el patrón de Graph
        // (sin redirecciones, 10 s, sin los loggers que pueden registrar el X-Shopify-Access-Token).
        services.AddHttpClient(ShopifyAdminGateway.HttpClientName, ShopifyAdminGateway.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => ShopifyAdminGateway.CreatePrimaryHandler())
            .RemoveAllLoggers();
        // Singleton: sus dependencias lo son, y el probador (singleton, como el registro de probadores) lo usa.
        services.AddSingleton<IShopifyAdminGateway, ShopifyAdminGateway>();
        services.AddSingleton<IProviderConnectionTester, ShopifyConnectionTester>();
        // §9: las instalaciones vencidas se barren cada hora, con margen para el canje en vuelo.
        services.AddHostedService<ShopifyInstallSweeper>();

        return services;
    }
}
