using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ApproveWholesaleOrderCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;

/// <summary>
/// Mayorista fase 3, D-1 y D-2: el mayorista aprueba el pedido de un distribuidor con el permiso de gestión (quien edita el
/// pedido es quien lo revisa). Mismo orden que las otras acciones de estado (<see cref="OrderStateAction"/>): permiso, pedido
/// (404 si no es del tenant), membresía; después el dominio, el evento de vuelta con todas las líneas finales, el historial
/// (<see cref="OrderHistoryEventType.WholesaleApproved"/>, adenda del contrato) y la auditoría, en la misma unidad de trabajo.
/// </summary>
public sealed class ApproveWholesaleOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ApproveWholesaleOrderCommand, OrderDto>
{
    internal const string AuditAction = "quotation.order.wholesale_approved";

    public async Task<OrderDto> HandleAsync(ApproveWholesaleOrderCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            repository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderManage, cancellationToken);

        var now = clock.UtcNow;
        order.ApproveWholesale(actor, now);
        lifecycleEvents.PublishWholesaleApproved(order, now);
        OrderStateAction.Record(
            repository, auditPublisher, executionContext, command.TenantId, order, actor,
            OrderHistoryEventType.WholesaleApproved, OrderChangeSummary.WholesaleApproved(), AuditAction, now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return order.ToDto();
    }
}
