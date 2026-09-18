using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record CancelOrderCommand(Guid TenantId, Guid OrderId, string? Reason)
    : ICommand<OrderDto>;

/// <summary>
/// Anula un pedido (spec 2026-09-16), calcado de <see cref="ApproveOrderHandler"/>: mismo orden
/// —permiso, pedido, membresía de quien actúa, dominio, auditoría— y la misma unidad de trabajo.
///
/// Permiso propio (<see cref="OrdersPermissions.OrderCancel"/>) y no el de gestión: anular deshace
/// también un pedido que otra persona ya aprobó (decisión 5).
///
/// Sin validador a propósito: las reglas del motivo son del dominio y responden con sus códigos
/// (<c>order.order.cancellation_reason_required</c>/<c>_too_long</c>), no con
/// <c>validation.failed</c>. Sin evento de outbox, igual que aprobar.
///
/// La auditoría registra la acción y el pedido, no el motivo: el payload de auditoría no tiene
/// dónde llevar texto libre (sólo <c>changedFields</c>). El motivo queda en el propio pedido.
/// </summary>
public sealed class CancelOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<CancelOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        CancelOrderCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderCancel);

        // Por el id del pedido, igual que aprobar.
        var order = await repository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        // CancelledBy es un id de membresía, como ApprovedBy: el módulo nunca guarda el usuario.
        var cancelledBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.Cancel(cancelledBy, command.Reason, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.cancelled",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
