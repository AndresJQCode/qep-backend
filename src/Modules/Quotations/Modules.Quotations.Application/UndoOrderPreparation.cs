using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record UndoOrderPreparationCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;

/// <summary>«Deshacer preparado» (spec 2026-11-10 §3.3): sólo si el pedido no está facturado. Permiso de
/// gestión, como preparar.</summary>
public sealed class UndoOrderPreparationHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<UndoOrderPreparationCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(UndoOrderPreparationCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            repository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderManage, cancellationToken);

        var now = clock.UtcNow;
        order.UndoPreparation(now);
        OrderStateAction.Record(
            repository, auditPublisher, executionContext, command.TenantId, order, actor,
            OrderHistoryEventType.PreparationUndone, OrderChangeSummary.PreparationUndone(),
            "quotation.order.preparation_undone", now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return order.ToDto();
    }
}
