using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-15 §5.3: «Sincronizar ahora» de una tienda. Sin cuerpo: no hay texto libre que validar.</summary>
public sealed record SyncShopifyStoreCommand(Guid TenantId, Guid ConnectionId) : ICommand<ShopifySyncResult>;

/// <summary>
/// Corre <see cref="ReconcileStore"/> en el request, con la misma ventana que el cron, y devuelve sus conteos (§5.3). Revalida
/// tenant, permiso y módulo antes de tocar nada (doble capa con la política de la ruta) y que la conexión sea de ese tenant: una
/// de otro tenant, una que no existe o una que no es de Shopify dan 403 <c>authorization.denied</c>, nunca 404, para no confirmar
/// que el id existe en otro tenant. Una corrida en curso → 409 <c>shopify.store.sync_in_progress</c>; una conexión pausada o
/// rechazada → 422 <c>shopify.store.not_operational</c>; Shopify caído → 503 <c>shopify.store.sync_unavailable</c>.
/// </summary>
public sealed class SyncShopifyStoreHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    ReconcileStore reconcile) : ICommandHandler<SyncShopifyStoreCommand, ShopifySyncResult>
{
    public async Task<ShopifySyncResult> HandleAsync(SyncShopifyStoreCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);

        var connection = await connections.FindAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (connection is null || connection.TenantId != command.TenantId || connection.ConnectionId != command.ConnectionId)
        {
            throw ShopifyAuthorization.Denied();
        }

        return await reconcile.RunAsync(connection, cancellationToken);
    }
}
