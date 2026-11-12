using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record UnarchiveOrderCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;

/// <summary>Desarchivar (spec 2026-11-10 §3.1): el pedido vuelve a <see cref="OrderStatus.Open"/> con sus
/// marcas intactas. Permiso de gestión.</summary>
public sealed class UnarchiveOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<UnarchiveOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(UnarchiveOrderCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            repository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderManage, cancellationToken);

        var now = clock.UtcNow;
        order.Unarchive(now);
        OrderStateAction.Record(
            repository, auditPublisher, executionContext, command.TenantId, order, actor,
            OrderHistoryEventType.Unarchived, OrderChangeSummary.Unarchived(), "quotation.order.unarchived", now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return order.ToDto();
    }
}
