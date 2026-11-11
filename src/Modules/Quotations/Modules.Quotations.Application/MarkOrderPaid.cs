using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record MarkOrderPaidCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;

/// <summary>
/// «Marcar como pagado» (spec 2026-11-10 §3.2): la única forma, junto con la plataforma, de llegar a
/// <see cref="OrderPaymentStatus.FullPaymentReceived"/>. Permiso propio
/// (<see cref="OrdersPermissions.OrderApprove"/>) y no el de gestión: quien carga los comprobantes no es
/// quien verifica la plata en el banco.
///
/// Orden de las guardas: primero las del pedido (abierto, no pagado, comprobante si el tenant lo exige),
/// después los datos de facturación (<see cref="BilledPersonTypeRequired"/>,
/// <see cref="BilledCustomerFieldsRequired"/>): un pedido ya pagado sigue respondiendo
/// <c>already_paid</c> aunque al cliente le falte un dato. Un 422 no deja historial ni auditoría.
/// </summary>
public sealed class MarkOrderPaidHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantDirectory tenantDirectory,
    IQuotationCustomerLookup customerLookup,
    IQuotationSettingsStore settingsStore)
    : ICommandHandler<MarkOrderPaidCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(MarkOrderPaidCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            repository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderApprove, cancellationToken);

        var settings = await settingsStore.GetOrderSettingsAsync(command.TenantId, cancellationToken);
        order.EnsureCanMarkPaid(settings.RequirePaymentProofToMarkPaid);
        await BilledPersonTypeRequired.EnsureSatisfiedAsync(
            tenantDirectory, customerLookup, command.TenantId, order, cancellationToken);
        await BilledCustomerFieldsRequired.EnsureSatisfiedAsync(
            tenantDirectory, customerLookup, command.TenantId, order, cancellationToken);

        var now = clock.UtcNow;
        order.MarkPaid(actor, settings.RequirePaymentProofToMarkPaid, now);
        OrderStateAction.Record(
            repository, auditPublisher, executionContext, command.TenantId, order, actor,
            OrderHistoryEventType.PaidMarked, OrderChangeSummary.PaidMarked(), "quotation.order.paid_marked", now);
        OrderAutoArchive.Apply(settings, repository, auditPublisher, executionContext, command.TenantId, order, actor, now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return order.ToDto();
    }
}
