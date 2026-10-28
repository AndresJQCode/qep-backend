using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>Spec 2026-10-15 §9. Sin <c>tenantId</c> a propósito: ver <see cref="IShopifyInstallRepository"/>.</summary>
internal sealed class ShopifyInstallRepository(IntegrationsDbContext dbContext) : IShopifyInstallRepository
{
    public void Add(ShopifyInstall install) => dbContext.ShopifyInstalls.Add(install);

    // Remove adjunta la instancia sin tracking como Deleted: el DELETE sale con el SaveChanges del enlace. Si otro enlace
    // ya la borró, EF ve 0 filas y el UnitOfWork lo responde concurrency.conflict.
    public void Remove(ShopifyInstall install) => dbContext.ShopifyInstalls.Remove(install);

    public Task<ShopifyInstall?> FindByStateAsync(string state, CancellationToken cancellationToken) =>
        dbContext.ShopifyInstalls.AsNoTracking().SingleOrDefaultAsync(install => install.State == state, cancellationToken);

    public Task<ShopifyInstall?> FindAsync(Guid installId, CancellationToken cancellationToken) =>
        dbContext.ShopifyInstalls.AsNoTracking().SingleOrDefaultAsync(install => install.Id == installId, cancellationToken);

    // Una sola sentencia: la condición sobre el state anterior es la que deja ganar a un solo reclamo, antes de que nadie
    // llame a Shopify. Sin transacción abierta durante las llamadas: el reclamo ya es atómico.
    public async Task<bool> TryClaimAsync(ShopifyInstall install, string state, CancellationToken cancellationToken)
    {
        if (!install.IsClaimed)
        {
            throw new InvalidOperationException("Claim has to run before the claim is stored.");
        }

        var updated = await dbContext.ShopifyInstalls
            .Where(row => row.Id == install.Id && row.State == state && row.ProtectedToken == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, install.State), cancellationToken);
        return updated == 1;
    }

    // Sobre la fila reclamada. Las columnas son las que MarkExchanged cambia; nada más.
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
