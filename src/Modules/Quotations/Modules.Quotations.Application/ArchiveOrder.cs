using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ArchiveOrderCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;

/// <summary>Archivar a mano (spec 2026-11-10 §3.5): siempre disponible salvo en un pedido anulado o ya
/// archivado. No quita ni pone ninguna otra marca. Permiso de gestión.</summary>
public sealed class ArchiveOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ArchiveOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(ArchiveOrderCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            repository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderManage, cancellationToken);

        var now = clock.UtcNow;
        order.Archive(actor, now);
        OrderStateAction.Record(
            repository, auditPublisher, executionContext, command.TenantId, order, actor,
            OrderHistoryEventType.Archived, OrderChangeSummary.Archived(), OrderAutoArchive.AuditAction, now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return order.ToDto();
    }
}
