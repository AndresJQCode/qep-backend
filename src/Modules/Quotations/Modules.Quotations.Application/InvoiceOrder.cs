using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record InvoiceOrderCommand(Guid TenantId, Guid OrderId)
    : ICommand<OrderDto>;

/// <summary>
/// Marca un pedido como facturado (spec 2026-10-05; desde 2026-11-10 es una marca, no un estado). Mismo orden que
/// <see cref="CancelOrderHandler"/> —permiso, pedido, membresía de quien actúa,
/// dominio, auditoría— y la misma unidad de trabajo.
///
/// Es sólo una marca: QEP no emite la factura ni llama a ningún sistema externo; deja
/// constancia de quién la marcó y cuándo. Permiso propio
/// (<see cref="OrdersPermissions.OrderInvoice"/>) y no el de aprobar: de fábrica lo tienen admin y
/// facturación (decisión 5).
///
/// Sin validador, porque no hay texto libre, y sin evento de outbox.
///
/// Desde spec 2026-11-10 §3.4 el pago completo lo exige el ajuste del tenant
/// (<see cref="OrderSettings.RequireFullPaymentToInvoice"/>, prendido por defecto), no exige preparado, y
/// corre las mismas validaciones de datos de facturación que marcar pagado, después de las guardas del pedido.
/// </summary>
public sealed class InvoiceOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IQuotationSettingsStore settingsStore,
    ITenantDirectory tenantDirectory,
    IQuotationCustomerLookup customerLookup)
    : ICommandHandler<InvoiceOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        InvoiceOrderCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderInvoice);

        // Por el id del pedido, igual que aprobar: facturación llega desde el listado de pedidos.
        var order = await repository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        // InvoicedBy es un id de membresía, como CancelledBy: el módulo nunca guarda el usuario.
        var invoicedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var settings = await settingsStore.GetOrderSettingsAsync(command.TenantId, cancellationToken);
        order.EnsureCanInvoice(settings.RequireFullPaymentToInvoice);
        await BilledPersonTypeRequired.EnsureSatisfiedAsync(
            tenantDirectory, customerLookup, command.TenantId, order, cancellationToken);
        await BilledCustomerFieldsRequired.EnsureSatisfiedAsync(
            tenantDirectory, customerLookup, command.TenantId, order, cancellationToken);

        var now = clock.UtcNow;
        order.Invoice(invoicedBy, settings.RequireFullPaymentToInvoice, now);
        repository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Invoiced, invoicedBy, OrderChangeSummary.Invoiced(), now));

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.invoiced",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
