using BuildingBlocks.Application;
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
        services.AddScoped<IShopifyOrderEditRepository, ShopifyOrderEditRepository>();
        services.AddScoped<IShopifyOrderPushRepository, ShopifyOrderPushRepository>();
        services.AddScoped<ShopifyOrderPushes>();
        services.AddScoped<IShopifyCityCorrectionRepository, ShopifyCityCorrectionRepository>();
        services.AddScoped<IShopifyCityCorrectionJobRepository, ShopifyCityCorrectionJobRepository>();
        services.AddScoped<IShopifyCustomLineMappingRepository, ShopifyCustomLineMappingRepository>();
        services.AddScoped<IShopifyProductLinkRepository, ShopifyProductLinkRepository>();
        services.AddScoped<IShopifyProductImportRepository, ShopifyProductImportRepository>();
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
        // Spec 2026-11-17 §5.2: el catálogo, con el mismo transporte (y su propio balde por scope).
        services.AddScoped<IShopifyProductsSource, ShopifyProductsClient>();
        services.AddSingleton<IShopifyProductPayloadParser, ShopifyProductPayloadParser>();
        // La imagen destacada: sólo el CDN de Shopify, sin redirecciones ni loggers (la query de la URL lleva la firma del CDN).
        services.AddHttpClient(ShopifyImageFetcher.HttpClientName, ShopifyImageFetcher.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => ShopifyImageFetcher.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddScoped<IShopifyImageFetcher, ShopifyImageFetcher>();
        // El IMeterFactory es el del host, como el de MessagingMetrics.
        services.AddSingleton<IShopifyProductSyncMetrics, ShopifyMetrics>();
        // Spec 2026-11-17: un producto de Shopify aplicado a Catalog (importación y webhooks). Sus puertos de Catalog y Storage los
        // registra Bootstrapper.
        services.AddScoped<ShopifyProductSync>();
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
        // Spec 2026-11-14 §3: la detección de ediciones, con el mismo patrón (Bootstrapper registra ShopifyOrderEdits antes).
        services.TryAddScoped<IShopifyOrderEditDetector, NoShopifyOrderEditDetector>();
        services.AddScoped<ShopifyOrderIngestion>();
        // Spec 2026-11-17 §6: los products/*. TryAdd porque AddShopifyProductSync registra lo mismo; depende además de
        // IShopifyCatalogWriter e IShopifyBillingAccounts (Bootstrapper) y de ITenantPriceRounding (Tenancy).
        services.TryAddScoped<ShopifyProductPricingResolver>();
        services.TryAddScoped<IShopifyProductSyncScope, ShopifyProductSyncScope>();
        services.TryAddScoped<ShopifyProductWebhooks>();
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
    /// siembra los trabajos de corrección, y el worker que los corrige. Sin esto, confirmar un sinónimo publica el evento y ningún
    /// pedido se corrige nunca, sin error en ningún lado (lo amarra <c>CompositionRootTests</c>). El procesador del worker depende
    /// de <see cref="IShopifyCitySynonymLookup"/> e <see cref="IShopifyCityCorrectionWriter"/>, que registra Bootstrapper. Las
    /// pruebas quitan los dos workers y corren el consumidor (<c>ProcessBatchAsync</c>) y el que corrige (<c>DrainAsync</c>) a mano.
    /// </summary>
    public static IServiceCollection AddShopifyCityCorrections(this IServiceCollection services)
    {
        services.AddScoped<ShopifyCitySynonymEventApplier>();
        services.AddSingleton<CitySynonymEventsProcessor>();
        services.AddHostedService<CitySynonymEventsWorker>();
        services.AddScoped<IShopifyCityCorrectionJobQueue, ShopifyCityCorrectionJobQueue>();
        services.AddScoped<IShopifyCityCorrectionJobs, ShopifyCityCorrectionJobs>();
        services.AddScoped<ShopifyCityCorrectionProcessor>();
        services.AddHostedService<ShopifyCityCorrectionWorker>();
        return services;
    }

    /// <summary>
    /// Spec 2026-11-24 §11.1.1: el consumidor de los eventos de pedido de Quotations (<c>shopify.quotation-order-events</c>), que
    /// encola a Shopify la dirección cambiada en «Editar pedido», y su worker. Sin esto el evento se publica y la dirección nunca
    /// llega a la tienda, sin error en ningún lado (lo amarra <c>CompositionRootTests</c>). El aplicador depende de
    /// <see cref="IShopifyConnectionDirectory"/>, que registra Bootstrapper. Las pruebas quitan el worker y corren el consumidor
    /// (<c>ProcessBatchAsync</c>) a mano.
    /// </summary>
    public static IServiceCollection AddShopifyQuotationOrderEvents(this IServiceCollection services)
    {
        services.AddScoped<ShopifyShippingPartyChangedApplier>();
        services.AddSingleton<QuotationOrderEventsProcessor>();
        services.AddHostedService<QuotationOrderEventsWorker>();
        return services;
    }

    /// <summary>
    /// Spec 2026-11-14 §5: el envío a Shopify de las correcciones (<c>orderUpdate</c>) y su worker. El cliente usa el mismo
    /// <c>shopify.admin</c> (sin redirecciones, sin loggers de headers) que registra <see cref="AddShopifyInfrastructure"/>. El
    /// procesador depende de <see cref="IShopifyConnectionDirectory"/>, <see cref="IShopifyOrderWriter"/> e
    /// <see cref="IShopifyGeographyLookup"/>, que registra Bootstrapper. Las pruebas quitan el worker y lo corren a mano
    /// (<c>DrainAsync</c>).
    /// </summary>
    public static IServiceCollection AddShopifyOrderPushes(this IServiceCollection services)
    {
        services.AddScoped<IShopifyOrderUpdater, ShopifyOrderUpdateClient>();
        services.AddScoped<IShopifyOrderPushQueue, ShopifyOrderPushQueue>();
        services.AddScoped<ShopifyOrderPushProcessor>();
        services.AddHostedService<ShopifyOrderPushWorker>();
        return services;
    }

    /// <summary>
    /// Spec 2026-11-17 §5.2: la importación de productos y su worker. El procesador y la regla de precios dependen de
    /// <see cref="IShopifyConnectionDirectory"/>, <see cref="IShopifyBillingAccounts"/>, <see cref="IShopifyCatalogWriter"/> (Bootstrapper)
    /// y de <c>ITenantPriceRounding</c> (Tenancy): Bootstrapper lo llama después de esos registros. Las pruebas quitan el worker y lo
    /// corren a mano (<c>DrainAsync</c>).
    /// </summary>
    public static IServiceCollection AddShopifyProductSync(this IServiceCollection services)
    {
        services.TryAddScoped<ShopifyProductPricingResolver>();
        services.TryAddScoped<IShopifyProductSyncScope, ShopifyProductSyncScope>();
        services.AddScoped<IShopifyProductImportQueue, ShopifyProductImportQueue>();
        services.AddScoped<ShopifyProductImportProcessor>();
        services.AddHostedService<ShopifyProductImportWorker>();
        // §7: importar, ver la importación, la cobertura y desvincular. El validador entra con AddValidatorsFromAssemblyContaining
        // (mismo ensamblado que UpdateShopifyStoreValidator).
        services.AddScoped<ICommandHandler<StartShopifyProductImportCommand, ShopifyProductImportStarted>, StartShopifyProductImportHandler>();
        services.AddScoped<IQueryHandler<GetShopifyProductImportQuery, ShopifyProductImportDto?>, GetShopifyProductImportHandler>();
        services.AddScoped<ShopifyProductCoverageCalculator>();
        services.AddScoped<IQueryHandler<GetShopifyProductCoverageQuery, ShopifyProductCoverageDto>, GetShopifyProductCoverageHandler>();
        // Spec 2026-11-17 (onboarding) §4.2: «Seguir así».
        services.AddScoped<ICommandHandler<AcceptShopifyProductCoverageCommand, bool>, AcceptShopifyProductCoverageHandler>();
        services.AddScoped<ICommandHandler<UnlinkShopifyProductCommand, bool>, UnlinkShopifyProductHandler>();
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
