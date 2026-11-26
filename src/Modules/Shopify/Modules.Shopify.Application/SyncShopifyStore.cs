using BuildingBlocks.Application;
using FluentValidation;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-15 §5.3: «Sincronizar ahora» de una tienda. Sin cuerpo. <paramref name="Source"/> (query, spec 2026-11-17
/// onboarding §7) es <c>null</c> o <see cref="SetupSource"/>: desde el asistente, la tienda guarda el resumen de la corrida.</summary>
public sealed record SyncShopifyStoreCommand(Guid TenantId, Guid ConnectionId, string? Source = null) : ICommand<ShopifySyncResult>
{
    /// <summary>La corrida del paso «Historial» del asistente.</summary>
    public const string SetupSource = "setup";
}

/// <summary>Plan D12: <c>source</c> es <c>setup</c> o no viene; cualquier otro valor es 422 con <c>errors["source"]</c>, antes de
/// correr nada.</summary>
public sealed class SyncShopifyStoreValidator : AbstractValidator<SyncShopifyStoreCommand>
{
    public SyncShopifyStoreValidator()
    {
        RuleFor(command => command.Source)
            .Equal(SyncShopifyStoreCommand.SetupSource, StringComparer.Ordinal)
            .When(command => command.Source is not null)
            .WithMessage($"The source must be '{SyncShopifyStoreCommand.SetupSource}' or absent.")
            .OverridePropertyName("source");
    }
}

/// <summary>
/// Corre <see cref="ReconcileStore"/> en el request, con la misma ventana que el cron —más los días de historial de la tienda,
/// si los tiene (spec 2026-10-21)— y devuelve sus conteos (§5.3). Revalida
/// tenant, permiso y módulo antes de tocar nada (doble capa con la política de la ruta) y que la conexión sea de ese tenant: una
/// de otro tenant, una que no existe o una que no es de Shopify dan 403 <c>authorization.denied</c>, nunca 404, para no confirmar
/// que el id existe en otro tenant. Una corrida en curso → 409 <c>shopify.store.sync_in_progress</c>; una conexión pausada o
/// rechazada → 422 <c>shopify.store.not_operational</c>; Shopify caído → 503 <c>shopify.store.sync_unavailable</c>.
/// <para>Spec 2026-11-17 (onboarding) §4.2, plan D12: con <c>source=setup</c>, después de la corrida la tienda guarda su resumen
/// (los días de historial con que corrió, los traídos, los importados y los que quedaron en espera, por motivo). No sube la
/// versión; si un <c>PUT</c> gana entre la lectura y el guardado, relee y reintenta una vez. La respuesta no cambia.</para>
/// </summary>
public sealed class SyncShopifyStoreHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    ReconcileStore reconcile,
    IShopifyStoreRepository stores,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyUnitOfWork unitOfWork,
    IClock clock,
    IValidator<SyncShopifyStoreCommand> validator) : ICommandHandler<SyncShopifyStoreCommand, ShopifySyncResult>
{
    public async Task<ShopifySyncResult> HandleAsync(SyncShopifyStoreCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var connection = await connections.FindAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (connection is null || connection.TenantId != command.TenantId || connection.ConnectionId != command.ConnectionId)
        {
            throw ShopifyAuthorization.Denied();
        }

        // Los días con que corre la ventana, leídos antes de la corrida: son los que ella usa.
        var historyDays = command.Source is null
            ? null
            : (await stores.FindAsync(command.TenantId, command.ConnectionId, cancellationToken))?.HistoryDays;

        // Spec 2026-10-21: la corrida manual es la única que usa los días de historial de la tienda.
        var result = await reconcile.RunAsync(connection, ShopifyReconcileMode.Manual, cancellationToken);
        if (command.Source is SyncShopifyStoreCommand.SetupSource)
        {
            await RecordSetupHistoryAsync(command, historyDays ?? 0, result, cancellationToken);
        }

        return result;
    }

    private async Task RecordSetupHistoryAsync(
        SyncShopifyStoreCommand command, int days, ShopifySyncResult result, CancellationToken cancellationToken)
    {
        IReadOnlyList<ShopifyWaitingGroup> waiting = result.WaitingOrderIds.Count == 0
            ? []
            : await mirrors.SummarizeWaitingAsync(command.TenantId, command.ConnectionId, result.WaitingOrderIds, cancellationToken);
        var summary = new ShopifySetupHistorySummary(
            clock.UtcNow, days, result.Fetched, result.Imported, [.. waiting.Select(group => new ShopifyWaitingCount(group.Reason, group.Count))]);
        for (var attempt = 1; ; attempt++)
        {
            // La conciliación ya creó la tienda si no existía.
            if (await stores.FindAsync(command.TenantId, command.ConnectionId, cancellationToken) is not { } store)
            {
                return;
            }

            store.RecordSetupHistory(summary);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (RequestConcurrencyException exception) when (ShopifyStoreSupport.IsConflict(exception) && attempt < 2)
            {
                unitOfWork.DiscardPendingChanges();
            }
        }
    }
}
