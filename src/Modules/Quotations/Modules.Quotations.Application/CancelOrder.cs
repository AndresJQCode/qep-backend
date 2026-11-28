using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record CancelOrderCommand(Guid TenantId, Guid OrderId, string? Reason)
    : ICommand<OrderDto>;

/// <summary>
/// Anula un pedido (spec 2026-09-16), calcado de <see cref="InvoiceOrderHandler"/>: mismo orden
/// —permiso, pedido, membresía de quien actúa, dominio, auditoría— y la misma unidad de trabajo.
///
/// Permiso propio (<see cref="OrdersPermissions.OrderCancel"/>) y no el de gestión: anular deshace
/// también un pedido que otra persona ya aprobó (decisión 5).
///
/// Sin validador a propósito: las reglas del motivo son del dominio y responden con sus códigos
/// (<c>order.order.cancellation_reason_required</c>/<c>_too_long</c>), no con
/// <c>validation.failed</c>. Escribe <c>quotations.order.cancelled.v1</c> (spec 2026-10-13 §4.5) en la misma unidad de trabajo: con él, Pipeline reabre el negocio ganado por esta cotización.
///
/// La auditoría registra la acción y el pedido, no el motivo: el payload de auditoría no tiene
/// dónde llevar texto libre (sólo <c>changedFields</c>). El motivo queda en el propio pedido.
/// </summary>
public sealed class CancelOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IExternalInvoiceStatus externalInvoices)
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

        // Spec 2026-11-18 §5.5: con una factura de Siigo viva (saliendo, sin resolver o creada), anular dejaría la factura sin pedido al que
        // marcar. Es un 409, como la marca manual: se resuelve la factura de Siigo primero.
        if (await externalInvoices.HasOpenInvoiceAsync(command.TenantId, command.OrderId, cancellationToken))
        {
            throw new ResourceConflictException(
                OrderStateErrors.ExternalInvoicePending,
                "An external invoice for this order is still being issued or exists; resolve it before cancelling the order.");
        }

        // CancelledBy es un id de membresía, como InvoicedBy: el módulo nunca guarda el usuario.
        var cancelledBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.Cancel(cancelledBy, command.Reason, now);
        repository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(),
            order.Id,
            OrderHistoryEventType.Cancelled,
            cancelledBy,
            OrderChangeSummary.Cancelled(order.CancellationReason!),
            now));

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.cancelled",
            order.Id.ToString(),
            "success",
            now);
        lifecycleEvents.PublishOrderCancelled(order, fromPlatform: false, now);
        if (order.Source == OrderSource.Wholesale)
        {
            // Mayorista fase 3 (D-21): la orden de compra del distribuidor se anula con este evento.
            lifecycleEvents.PublishWholesaleCancelled(order, byBuyer: false, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
