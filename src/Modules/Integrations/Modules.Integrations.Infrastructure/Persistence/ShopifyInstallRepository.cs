using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>Spec 2026-10-15 §9. Sin <c>tenantId</c> a propósito: ver <see cref="IShopifyInstallRepository"/>.</summary>
internal sealed class ShopifyInstallRepository(IntegrationsDbContext dbContext) : IShopifyInstallRepository
{
    public void Add(ShopifyInstall install) => dbContext.ShopifyInstalls.Add(install);

    public Task<ShopifyInstall?> FindByStateAsync(string state, CancellationToken cancellationToken) =>
        dbContext.ShopifyInstalls.AsNoTracking().SingleOrDefaultAsync(install => install.State == state, cancellationToken);

    public Task<ShopifyInstall?> FindAsync(Guid installId, CancellationToken cancellationToken) =>
        dbContext.ShopifyInstalls.AsNoTracking().SingleOrDefaultAsync(install => install.Id == installId, cancellationToken);

    // Una sola sentencia: la condición sobre access_token_protected es la que deja ganar a un solo canje. Las columnas son
    // las que MarkExchanged cambia; nada más.
    public async Task<bool> TryCompleteExchangeAsync(ShopifyInstall install, CancellationToken cancellationToken)
    {
        if (!install.IsExchanged)
        {
            throw new InvalidOperationException("MarkExchanged has to run before the exchange is stored.");
        }

        var updated = await dbContext.ShopifyInstalls
            .Where(row => row.Id == install.Id && row.State == install.State && row.ProtectedToken == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.ShopId, install.ShopId)
                    .SetProperty(row => row.ShopName, install.ShopName)
                    .SetProperty(row => row.Scopes, install.Scopes)
                    .SetProperty(row => row.ProtectedToken, install.ProtectedToken)
                    .SetProperty(row => row.KeyId, install.KeyId)
                    .SetProperty(row => row.ExpiresAt, install.ExpiresAt),
                cancellationToken);
        return updated == 1;
    }
}
