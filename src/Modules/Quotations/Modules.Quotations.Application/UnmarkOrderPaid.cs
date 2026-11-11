using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record UnmarkOrderPaidCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;

/// <summary>«Deshacer pagado» (spec 2026-11-10 §3.2): mismo permiso que marcar; el pago vuelve al valor
/// calculado con los comprobantes. El dominio lo rechaza si el pedido ya está preparado o facturado.</summary>
public sealed class UnmarkOrderPaidHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<UnmarkOrderPaidCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(UnmarkOrderPaidCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            repository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderApprove, cancellationToken);

        var now = clock.UtcNow;
        order.UnmarkPaid(now);
        OrderStateAction.Record(
            repository, auditPublisher, executionContext, command.TenantId, order, actor,
            OrderHistoryEventType.PaidUnmarked, OrderChangeSummary.PaidUnmarked(), "quotation.order.paid_unmarked", now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return order.ToDto();
    }
}
