using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>Con tracking: el llamador modifica y guarda con <c>IShopifyUnitOfWork</c>. Todo filtra por <c>tenant_id</c>.</summary>
internal sealed class ShopifyStoreRepository(ShopifyDbContext dbContext) : IShopifyStoreRepository
{
    public Task<ShopifyStore?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Stores.SingleOrDefaultAsync(
            store => store.TenantId == tenantId && store.ConnectionId == connectionId, cancellationToken);

    public void Add(ShopifyStore store) => dbContext.Stores.Add(store);

    public async Task<IReadOnlyList<ShopifyStore>> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken) =>
        await dbContext.Stores.AsNoTracking().Where(store => store.ShopDomain == shopDomain).ToListAsync(cancellationToken);
}

internal sealed class ShopifyOrderMirrorRepository(ShopifyDbContext dbContext) : IShopifyOrderMirrorRepository
{
    public Task<ShopifyOrderMirror?> FindAsync(Guid tenantId, Guid connectionId, long shopifyOrderId, CancellationToken cancellationToken) =>
        dbContext.Orders.SingleOrDefaultAsync(
            row => row.TenantId == tenantId && row.ConnectionId == connectionId && row.ShopifyOrderId == shopifyOrderId,
            cancellationToken);

    public void Add(ShopifyOrderMirror row) => dbContext.Orders.Add(row);

    public Task<int> DeleteByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Orders
            .Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(cancellationToken);
}
