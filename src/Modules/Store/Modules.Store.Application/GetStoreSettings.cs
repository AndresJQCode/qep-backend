using BuildingBlocks.Application;
using Modules.Store.Domain;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

public sealed record GetStoreSettingsQuery(Guid TenantId) : IQuery<StoreSettingsDto>;

public sealed class GetStoreSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IOnlineStoreRepository stores,
    IClock clock,
    StoreSettingsComposer composer)
    : IQueryHandler<GetStoreSettingsQuery, StoreSettingsDto>
{
    public async Task<StoreSettingsDto> HandleAsync(GetStoreSettingsQuery query, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, StorePermissions.SettingsManage, cancellationToken);

        // Una tienda que nunca se guardó se lee con sus valores por defecto y versión 0: mirar la pantalla no crea nada.
        var store = await stores.FindAsync(query.TenantId, cancellationToken)
            ?? OnlineStore.CreateEmpty(query.TenantId, clock.UtcNow);
        return await composer.ComposeAsync(store, cancellationToken);
    }
}
