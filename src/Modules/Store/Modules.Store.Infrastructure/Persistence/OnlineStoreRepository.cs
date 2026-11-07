using Microsoft.EntityFrameworkCore;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Infrastructure.Persistence;

internal sealed class OnlineStoreRepository(StoreDbContext dbContext) : IOnlineStoreRepository
{
    public async Task<OnlineStore?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var store = await dbContext.Stores.FirstOrDefaultAsync(value => value.TenantId == tenantId, cancellationToken);
        if (store is not null)
        {
            RestoreCurrencyOrder(store);
        }

        return store;
    }

    public void Add(OnlineStore store) => dbContext.Stores.Add(store);

    public Task<bool> SlugTakenByAnotherAsync(Guid tenantId, string slug, CancellationToken cancellationToken) =>
        dbContext.Stores
            .AsNoTracking()
            .AnyAsync(value => value.TenantId != tenantId && value.Slug == slug, cancellationToken);

    // EF lee una colección owned ordenada por su clave (tenant, moneda), no por el orden que eligió quien configuró la tienda: ese
    // vive en `position`. Se reordena la lista que el agregado ya tiene en vez de pedirle al dominio un método que sólo sirve a
    // la persistencia. Sólo con la entidad sin cambios: una lista ya modificada en memoria no se toca.
    private void RestoreCurrencyOrder(OnlineStore store)
    {
        if (dbContext.Entry(store).State != EntityState.Unchanged
            || dbContext.Entry(store).Collection(value => value.Currencies).CurrentValue is not List<StoreCurrency> currencies)
        {
            return;
        }

        var positions = currencies.ToDictionary(
            currency => currency,
            currency => dbContext.Entry(currency).Property<int>(StoreDbContext.CurrencyPositionColumn).CurrentValue);
        currencies.Sort((left, right) => positions[left].CompareTo(positions[right]));
    }
}
