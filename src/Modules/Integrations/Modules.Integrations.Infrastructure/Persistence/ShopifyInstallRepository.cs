using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>Spec 2026-10-15 §9. Sin <c>tenantId</c> a propósito: ver <see cref="IShopifyInstallRepository"/>.</summary>
internal sealed class ShopifyInstallRepository(IntegrationsDbContext dbContext) : IShopifyInstallRepository
{
    public void Add(ShopifyInstall install) => dbContext.ShopifyInstalls.Add(install);
}
