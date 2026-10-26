using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Admin;
using Modules.Shopify.Infrastructure.Messaging;
using Modules.Shopify.Infrastructure.Parsing;
using Modules.Shopify.Infrastructure.Persistence;
using Modules.Shopify.Infrastructure.Workers;

namespace Modules.Shopify.Infrastructure;

public static class ShopifyInfrastructureExtensions
{
    public static IServiceCollection AddShopifyInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en el primer request con un error
        // de Npgsql que no explica nada.
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<ShopifyDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ShopifyDbContext.Schema)));

        services.AddScoped<IShopifyUnitOfWork, ShopifyUnitOfWork>();
        // §9: auditoría atómica propia sobre audit.entries (nunca el IAuditRecorder ligado a TenancyDbContext).
        services.AddScoped<IShopifyAuditRecorder, ShopifyAuditRecorder>();

        // §5.1: el webhook encola; el tope del cuerpo y el intervalo del worker se validan al arrancar.
        services.AddOptions<ShopifyWebhookOptions>()
            .Bind(configuration.GetSection(ShopifyWebhookOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ShopifyWebhookOptions>, ShopifyWebhookOptionsValidator>();
        services.AddScoped<WebhookDeliveries>();
        services.AddScoped<IShopifyWebhookDeliveries>(provider => provider.GetRequiredService<WebhookDeliveries>());
        services.AddScoped<IShopifyWebhookDeliveryQueue>(provider => provider.GetRequiredService<WebhookDeliveries>());
        services.AddScoped<IShopifyStoreRepository, ShopifyStoreRepository>();
        services.AddScoped<IShopifyOrderMirrorRepository, ShopifyOrderMirrorRepository>();
        services.AddScoped<IShopifyCityCorrectionRepository, ShopifyCityCorrectionRepository>();
        services.AddScoped<IShopifyCityCorrectionJobRepository, ShopifyCityCorrectionJobRepository>();
        services.AddSingleton<IShopifyOrderPayloadParser, ShopifyOrderPayloadParser>();

        // §5.3: la conciliación. El cliente GraphQL es scoped (lleva el estado del balde de Shopify de una corrida) y su
        // HttpClient no sigue redirecciones ni registra headers: el token va en uno. TimeProvider.System sólo si nadie registró otro.
        services.AddOptions<ShopifyReconcileOptions>()
            .Bind(configuration.GetSection(ShopifyReconcileOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ShopifyReconcileOptions>, ShopifyReconcileOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(ShopifyGraphQlClient.HttpClientName, ShopifyGraphQlClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => ShopifyGraphQlClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddScoped<IShopifyOrdersSource, ShopifyGraphQlClient>();
        services.AddScoped<IShopifyReconcileLeases, ShopifyReconcileLeases>();
        return services;
    }

    /// <summary>
    /// §5.2: el procesador de entregas y el upsert del espejo. Aparte de <see cref="AddShopifyInfrastructure"/> porque dependen de
    /// puertos cuyos adaptadores registra Bootstrapper —<see cref="IShopifyConnectionDirectory"/>, <see cref="IShopifyConnectionHealth"/>
    /// e <see cref="IShopifyOrderImporter"/>— y en Development <c>ValidateOnBuild</c> no deja arrancar un servicio registrado con una
    /// dependencia sin registrar. Bootstrapper lo llama después de esos registros; sin él, el webhook encola y nadie
    /// procesa.
    /// </summary>
    public static IServiceCollection AddShopifyWebhookProcessing(this IServiceCollection services)
    {
        // La reacción de §6.3 vacía por defecto; Bootstrapper registra ShopifyOrderChanges antes de llamar a este método, y el
        // TryAdd no la pisa.
        services.TryAddScoped<IShopifyOrderChangeReaction, NoShopifyOrderChangeReaction>();
        services.AddScoped<ShopifyOrderIngestion>();
        services.AddScoped<ShopifyWebhookDeliveryProcessor>();
        // §5.3: la conciliación y «Sincronizar ahora» pasan cada pedido por la misma ingesta; necesitan también
        // IShopifyConnectionHealth e IShopifyApiVersion, de Bootstrapper.
        services.AddScoped<ReconcileStore>();
        return services;
    }

    /// <summary>§5.2: el worker en segundo plano. Requiere <see cref="AddShopifyWebhookProcessing"/>; las pruebas registran sólo
    /// aquél y corren el worker a mano.</summary>
    public static IServiceCollection AddShopifyWebhookWorker(this IServiceCollection services)
    {
        services.AddHostedService<ShopifyWebhookDeliveryWorker>();
        return services;
    }

    /// <summary>
    /// Spec 2026-10-26 D-C6: el consumidor de los eventos de sinónimos de Geography (<c>shopify.city-synonym-events</c>), que
    /// siembra los trabajos de corrección, y su worker. Sin esto, confirmar un sinónimo publica el evento y ningún pedido se corrige
    /// nunca, sin error en ningún lado (lo amarra <c>CompositionRootTests</c>). Las pruebas quitan el worker y corren el procesador
    /// a mano.
    /// </summary>
    public static IServiceCollection AddShopifyCityCorrections(this IServiceCollection services)
    {
        services.AddScoped<ShopifyCitySynonymEventApplier>();
        services.AddSingleton<CitySynonymEventsProcessor>();
        services.AddHostedService<CitySynonymEventsWorker>();
        return services;
    }

    /// <summary>§5.3: el cron de la conciliación. Requiere <see cref="AddShopifyWebhookProcessing"/>; las pruebas lo quitan y lo
    /// corren a mano.</summary>
    public static IServiceCollection AddShopifyReconcileWorker(this IServiceCollection services)
    {
        services.AddHostedService<ShopifyReconcileWorker>();
        return services;
    }
}
