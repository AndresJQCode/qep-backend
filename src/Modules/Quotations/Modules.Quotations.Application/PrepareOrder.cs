using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record PrepareOrderCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;

/// <summary>«Marcar como preparado» (spec 2026-11-10 §3.3): pedido abierto, pagado o no (un contraentrega se
/// despacha antes del cobro). Permiso de gestión. Si el pedido ya estaba pagado y el tenant archiva solo,
/// preparar lo archiva (§3.5).</summary>
public sealed class PrepareOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IQuotationSettingsStore settingsStore)
    : ICommandHandler<PrepareOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(PrepareOrderCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            repository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderManage, cancellationToken);

        var now = clock.UtcNow;
        order.Prepare(actor, now);
        OrderStateAction.Record(
            repository, auditPublisher, executionContext, command.TenantId, order, actor,
            OrderHistoryEventType.Prepared, OrderChangeSummary.Prepared(), "quotation.order.prepared", now);
        var settings = await settingsStore.GetOrderSettingsAsync(command.TenantId, cancellationToken);
        OrderAutoArchive.Apply(settings, repository, auditPublisher, executionContext, command.TenantId, order, actor, now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return order.ToDto();
    }
}
