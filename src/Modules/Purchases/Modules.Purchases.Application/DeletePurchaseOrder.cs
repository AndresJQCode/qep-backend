using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record DeletePurchaseOrderCommand(Guid TenantId, Guid OrderId, long ExpectedVersion) : ICommand<bool>;

/// <summary>
/// Borra un borrador con sus líneas e historial (la base los cascadea). Es definitivo y deja un hueco en el consecutivo (D-10).
/// <para>Mayorista fase 3 (revisión del bloque B, M1): un borrador vinculado pudo dejar un pedido en el mayorista si el envío
/// no terminó de guardarse. Antes de borrarlo se anula ese pedido; si el mayorista ya lo aprobó, el 409 sale de ahí y el
/// borrador queda (volver a enviarlo lo adopta, D-8).</para>
/// </summary>
public sealed class DeletePurchaseOrderHandler(
    IExecutionContext executionContext,
    IMembershipDirectory membershipDirectory,
    IClock clock,
    IPurchaseOrderRepository orders,
    IPurchasesUnitOfWork unitOfWork,
    IPurchasesAuditPublisher auditPublisher,
    IWholesaleOrderIntake wholesaleOrders)
    : ICommandHandler<DeletePurchaseOrderCommand, bool>
{
    public async Task<bool> HandleAsync(DeletePurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, command.TenantId, PurchasesPermissions.OrderManage);
        await PurchasesAuthorization.ResolveMemberAsync(executionContext, membershipDirectory, command.TenantId, cancellationToken);
        var order = await orders.GetAsync(command.TenantId, new PurchaseOrderId(command.OrderId), cancellationToken)
            ?? throw PurchasesConflicts.NotFound(command.OrderId);
        PurchasesConflicts.EnsureVersion(order, command.ExpectedVersion);
        PurchasesConflicts.EnsureAllowed(order, PurchaseOrderAction.Delete);
        order.EnsureDeletable();

        if (order.IsLinked)
        {
            await wholesaleOrders.CancelAsync(
                command.TenantId, order.SupplierTenantId!.Value, order.Id.Value, order.Number, order.LinkedOrderId,
                cancellationToken);
        }

        orders.Remove(order);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            PurchasesAuditActions.Deleted,
            PurchasesAuditActions.Resources.PurchaseOrder,
            order.Id.ToString(),
            "success",
            [],
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
