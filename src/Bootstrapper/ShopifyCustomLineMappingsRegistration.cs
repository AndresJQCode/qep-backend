using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-11-13: lo que las asociaciones de líneas personalizadas necesitan del composition root. Archivo propio y una
/// sola línea en <c>QepServiceCollectionExtensions</c>, para no chocar con las otras ramas de Shopify.</summary>
public static class ShopifyCustomLineMappingsRegistration
{
    public static IServiceCollection AddShopifyCustomLineMappings(this IServiceCollection services)
    {
        services.AddScoped<IShopifyVariantLabels, ShopifyVariantLabels>();
        // El validador entra con AddValidatorsFromAssemblyContaining<UpdateShopifyStoreValidator> (mismo ensamblado).
        services.AddScoped<IQueryHandler<ListShopifyCustomLineMappingsQuery, IReadOnlyList<ShopifyCustomLineMappingDto>>, ListShopifyCustomLineMappingsHandler>();
        services.AddScoped<ICommandHandler<CreateShopifyCustomLineMappingCommand, ShopifyCustomLineMappingDto>, CreateShopifyCustomLineMappingHandler>();
        services.AddScoped<ICommandHandler<DeleteShopifyCustomLineMappingCommand, bool>, DeleteShopifyCustomLineMappingHandler>();
        return services;
    }
}
