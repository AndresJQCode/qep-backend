using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-11-14 §6: lo que la bandeja de cambios de Shopify necesita del composition root (consultas y decisiones).
/// Archivo propio y una sola línea en <c>QepServiceCollectionExtensions</c>, como las líneas personalizadas.</summary>
public static class ShopifyOrderEditsRegistration
{
    public static IServiceCollection AddShopifyOrderEditDecisions(this IServiceCollection services)
    {
        // El validador entra con AddValidatorsFromAssemblyContaining<UpdateShopifyStoreValidator> (mismo ensamblado).
        services.AddScoped<ShopifyOrderChangeMapper>();
        services.AddScoped<ShopifyChangeDecider>();
        services.AddScoped<IQueryHandler<ListShopifyChangesQuery, ShopifyOrderChangePageDto>, ListShopifyChangesHandler>();
        services.AddScoped<IQueryHandler<ListShopifyOrderChangesQuery, IReadOnlyList<ShopifyOrderChangeDto>>, ListShopifyOrderChangesHandler>();
        services.AddScoped<ICommandHandler<AcceptShopifyChangeCommand, ShopifyOrderChangeDto>, AcceptShopifyChangeHandler>();
        services.AddScoped<ICommandHandler<RejectShopifyChangeCommand, ShopifyOrderChangeDto>, RejectShopifyChangeHandler>();
        services.AddScoped<ICommandHandler<ReviewShopifyChangeCommand, ShopifyOrderChangeDto>, ReviewShopifyChangeHandler>();
        // Spec 2026-11-14 §5: la corrección del asesor y el panel del pedido. El escritor enrola Quotations en la transacción de
        // Shopify del request (ver ShopifyOrderCorrectionWriter): scoped y sin scope propio, a propósito.
        services.AddScoped<IShopifyOrderCorrectionWriter, ShopifyOrderCorrectionWriter>();
        services.AddScoped<ShopifyOrderSyncReader>();
        services.AddScoped<ICommandHandler<CorrectShopifyOrderCommand, ShopifyOrderSyncDto>, CorrectShopifyOrderHandler>();
        services.AddScoped<IQueryHandler<GetShopifyOrderSyncQuery, ShopifyOrderSyncDto>, GetShopifyOrderSyncHandler>();
        return services;
    }
}
