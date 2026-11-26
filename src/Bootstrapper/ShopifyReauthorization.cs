using BuildingBlocks.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-11-17 D6: la URL para volver a autorizar la app cuando a la tienda le falta <c>read_products</c>. Es el inicio A de la
/// instalación de Integrations (<see cref="StartShopifyInstallForTenantCommand"/>) con el dominio de la conexión: la URL lleva los
/// scopes configurados hoy y, al volver, el canje guarda los nuevos (camino de hoy). Corre en el request del asesor, con su permiso
/// (<c>integrations.connection.manage</c>, que el handler revalida). Shopify no referencia Integrations: el composition root los une.
/// </summary>
internal sealed class ShopifyReauthorization(
    ICommandHandler<StartShopifyInstallForTenantCommand, ShopifyAuthorizeResponse> startInstall) : IShopifyReauthorization
{
    public async Task<string> CreateUrlAsync(Guid tenantId, string shopDomain, string redirectUri, CancellationToken cancellationToken)
    {
        var response = await startInstall.HandleAsync(new StartShopifyInstallForTenantCommand(tenantId, shopDomain, redirectUri), cancellationToken);
        return response.AuthorizeUrl;
    }
}
