using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Owner 2026-11-14: «Sincronizar con Shopify» de un pedido (<c>POST /orders/{id}/push</c>, <c>quotations.order.manage</c>).
/// <see cref="MirrorOrderId"/> es la fila del espejo, como en <c>PUT /orders/{id}/corrections</c>.</summary>
public sealed record ReleaseShopifyOrderPushCommand(Guid TenantId, Guid MirrorOrderId) : ICommand<ShopifyOrderSyncDto>;

/// <summary>Owner 2026-11-14: «Sincronizar pedidos pendientes» de una tienda (<c>POST /stores/{connectionId}/pushes/release</c>,
/// <c>quotations.order.manage</c>, el mismo permiso que «Sincronizar ahora» de la tienda: lo hace quien corrige pedidos).</summary>
public sealed record ReleaseShopifyStorePushesCommand(Guid TenantId, Guid ConnectionId) : ICommand<ShopifyPushReleaseResult>;

/// <summary>
/// Owner 2026-11-14: suelta el envío retenido (<c>Held</c>) de un pedido para que el worker lo mande: <c>Pending</c> sin intentos ni
/// lease. Revalida tenant, permiso y módulo (403). Fila del espejo o pedido de QEP inexistente → 404 <c>shopify.order.not_found</c>;
/// conexión que sigue sin estar operativa → 422 <c>shopify.store.not_operational</c> (el envío sigue retenido); sin envío retenido →
/// 409 <c>shopify.push.nothing_held</c>. Una corrección que se fusiona a la vez → 412 <c>concurrency.conflict</c>. Responde el panel
/// del pedido, con el envío ya <c>Pending</c>.
/// </summary>
public sealed class ReleaseShopifyOrderPushHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyOrderWriter orders,
    IShopifyConnectionDirectory connections,
    IShopifyOrderPushRepository pushes,
    IShopifyAuditRecorder auditRecorder,
    IShopifyUnitOfWork unitOfWork,
    IClock clock,
    ShopifyOrderSyncReader reader) : ICommandHandler<ReleaseShopifyOrderPushCommand, ShopifyOrderSyncDto>
{
    public async Task<ShopifyOrderSyncDto> HandleAsync(ReleaseShopifyOrderPushCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);
        var row = await ShopifyOrderSupport.FindAsync(mirrors, command.TenantId, command.MirrorOrderId, cancellationToken);
        var order = row.QepOrderId is { } orderId ? await orders.ReadStateAsync(command.TenantId, orderId, cancellationToken) : null;
        if (order is null)
        {
            throw new ResourceNotFoundException(ShopifyErrorCodes.OrderNotFound, "The order of this Shopify order no longer exists.");
        }

        if (await connections.FindAsync(command.TenantId, row.ConnectionId, cancellationToken) is not { IsOperational: true })
        {
            throw new ShopifyDomainException(
                ShopifyErrorCodes.StoreNotOperational, "The Shopify connection is not operational; the held push stays held.");
        }

        if (await pushes.FindOpenAsync(command.TenantId, row.Id, cancellationToken) is not { Status: ShopifyOrderPushStatus.Held } push)
        {
            throw new ResourceConflictException(ShopifyErrorCodes.PushNothingHeld, "This Shopify order has no held push to release.");
        }

        var now = clock.UtcNow;
        push.Release(now);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            ShopifyAuditActions.OrderPushReleased,
            ShopifyAuditActions.ShopifyOrderResourceType,
            row.Id.ToString(),
            JsonSerializer.Serialize(new { groups = push.Groups.Select(group => group.ToString()) }),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await reader.ReadAsync(row, order, cancellationToken);
    }
}

/// <summary>
/// Owner 2026-11-14: suelta, en una sola sentencia, todos los envíos retenidos de una conexión del tenant, y responde cuántos. La
/// conexión tiene que ser de ese tenant y de Shopify (si no, 403 <c>authorization.denied</c>, nunca 404) y estar operativa (422
/// <c>shopify.store.not_operational</c>). La sentencia y la auditoría van en una transacción.
/// </summary>
public sealed class ReleaseShopifyStorePushesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyOrderPushRepository pushes,
    IShopifyAuditRecorder auditRecorder,
    IShopifyUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<ReleaseShopifyStorePushesCommand, ShopifyPushReleaseResult>
{
    public async Task<ShopifyPushReleaseResult> HandleAsync(ReleaseShopifyStorePushesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);
        var connection = await connections.FindAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (connection is null || connection.TenantId != command.TenantId || connection.ConnectionId != command.ConnectionId)
        {
            throw ShopifyAuthorization.Denied();
        }

        if (!connection.IsOperational)
        {
            throw new ShopifyDomainException(
                ShopifyErrorCodes.StoreNotOperational, "The Shopify connection is not operational; its held pushes stay held.");
        }

        var now = clock.UtcNow;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var released = await pushes.ReleaseHeldAsync(command.TenantId, command.ConnectionId, now, cancellationToken);
        if (released > 0)
        {
            auditRecorder.Record(
                command.TenantId,
                executionContext.SubjectId,
                AuditActorType.Human,
                ShopifyAuditActions.StorePushesReleased,
                ShopifyAuditActions.ConnectionResourceType,
                command.ConnectionId.ToString(),
                JsonSerializer.Serialize(new { released }),
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ShopifyPushReleaseResult(released);
    }
}
