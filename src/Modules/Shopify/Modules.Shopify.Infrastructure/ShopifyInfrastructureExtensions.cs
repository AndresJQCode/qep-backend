using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Shopify.Application;
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
        services.AddSingleton<IShopifyOrderPayloadParser, ShopifyOrderPayloadParser>();
        return services;
    }

    /// <summary>
    /// §5.2: el procesador de entregas y el upsert del espejo. Aparte de <see cref="AddShopifyInfrastructure"/> porque dependen de
    /// puertos que todavía no tienen adaptador —<see cref="IShopifyConnectionDirectory"/> e <see cref="IShopifyConnectionHealth"/>
    /// (tarea 12), <see cref="IShopifyOrderImporter"/> (tarea 13)— y en Development <c>ValidateOnBuild</c> no deja arrancar un
    /// servicio registrado con una dependencia sin registrar. Mientras no se llame, el webhook encola y las entregas esperan; nada
    /// se pierde.
    /// </summary>
    public static IServiceCollection AddShopifyWebhookProcessing(this IServiceCollection services)
    {
        services.AddScoped<ShopifyOrderIngestion>();
        services.AddScoped<ShopifyWebhookDeliveryProcessor>();
        return services;
    }

    /// <summary>§5.2: el worker en segundo plano. Requiere <see cref="AddShopifyWebhookProcessing"/>; las pruebas registran sólo
    /// aquél y corren el worker a mano.</summary>
    public static IServiceCollection AddShopifyWebhookWorker(this IServiceCollection services)
    {
        services.AddHostedService<ShopifyWebhookDeliveryWorker>();
        return services;
    }
}
