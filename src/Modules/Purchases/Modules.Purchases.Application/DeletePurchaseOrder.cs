using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record DeletePurchaseOrderCommand(Guid TenantId, Guid OrderId, long ExpectedVersion) : ICommand<bool>;

/// <summary>
/// Borra un borrador con sus líneas e historial (la base los cascadea). Es definitivo y deja un hueco en el consecutivo (D-10).
/// </summary>
public sealed class DeletePurchaseOrderHandler(
    IExecutionContext executionContext,
    IMembershipDirectory membershipDirectory,
    IClock clock,
    IPurchaseOrderRepository orders,
    IPurchasesUnitOfWork unitOfWork,
    IPurchasesAuditPublisher auditPublisher)
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
