using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// Los tres cambios de estado sin cuerpo: <c>Send</c>, <c>Receive</c> y <c>Cancel</c>. Confirmar trae líneas y va aparte
/// (<see cref="ConfirmPurchaseOrderCommand"/>).
/// </summary>
public sealed record TransitionPurchaseOrderCommand(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    PurchaseOrderAction Action)
    : ICommand<PurchaseOrderDetailDto>;

/// <summary>
/// Un handler para enviar, recibir y cancelar. Enviar vuelve a mirar el proveedor: si lo desactivaron (o ya no está) después del
/// alta, 422 <c>supplier_inactive</c> y la orden sigue en borrador. Recibir y cancelar no tienen reglas extra.
/// </summary>
public sealed class TransitionPurchaseOrderHandler(
    IExecutionContext executionContext,
    IMembershipDirectory membershipDirectory,
    IClock clock,
    IPurchasesSupplierLookup suppliers,
    IPurchaseOrderRepository orders,
    IPurchasesUnitOfWork unitOfWork,
    IPurchasesAuditPublisher auditPublisher,
    PurchaseOrderComposer composer)
    : ICommandHandler<TransitionPurchaseOrderCommand, PurchaseOrderDetailDto>
{
    private static readonly string[] StatusField = ["status"];

    public async Task<PurchaseOrderDetailDto> HandleAsync(TransitionPurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        var auditAction = command.Action switch
        {
            PurchaseOrderAction.Send => PurchasesAuditActions.Sent,
            PurchaseOrderAction.Receive => PurchasesAuditActions.Received,
            PurchaseOrderAction.Cancel => PurchasesAuditActions.Cancelled,
            _ => throw new ArgumentOutOfRangeException(
                nameof(command), command.Action, "Only send, receive and cancel go through this handler."),
        };

        PurchasesAuthorization.EnsureAuthorized(executionContext, command.TenantId, PurchasesPermissions.OrderManage);
        var memberId = await PurchasesAuthorization.ResolveMemberAsync(
            executionContext, membershipDirectory, command.TenantId, cancellationToken);
        var order = await orders.GetAsync(command.TenantId, new PurchaseOrderId(command.OrderId), cancellationToken)
            ?? throw PurchasesConflicts.NotFound(command.OrderId);
        PurchasesConflicts.EnsureVersion(order, command.ExpectedVersion);
        PurchasesConflicts.EnsureAllowed(order, command.Action);

        if (command.Action == PurchaseOrderAction.Send)
        {
            var supplier = await suppliers.FindAsync(command.TenantId, order.SupplierId, cancellationToken);
            if (supplier is not { IsActive: true })
            {
                throw PurchaseOrderLineBuilder.SupplierInactive();
            }
        }

        var now = clock.UtcNow;
        switch (command.Action)
        {
            case PurchaseOrderAction.Send:
                order.Send(memberId, now);
                break;
            case PurchaseOrderAction.Receive:
                order.Receive(memberId, now);
                break;
            default:
                order.Cancel(memberId, now);
                break;
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            auditAction,
            PurchasesAuditActions.Resources.PurchaseOrder,
            order.Id.ToString(),
            "success",
            StatusField,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.DetailAsync(order, cancellationToken);
    }
}
