using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record InvoiceOrderCommand(Guid TenantId, Guid OrderId)
    : ICommand<OrderDto>;

/// <summary>
/// Marca un pedido aprobado como facturado (spec 2026-10-05), calcado de
/// <see cref="ApproveOrderHandler"/>: mismo orden —permiso, pedido, membresía de quien actúa,
/// dominio, auditoría— y la misma unidad de trabajo.
///
/// Es sólo un cambio de estado: QEP no emite la factura ni llama a ningún sistema externo; deja
/// constancia de quién la marcó y cuándo. Permiso propio
/// (<see cref="OrdersPermissions.OrderInvoice"/>) y no el de aprobar: de fábrica lo tienen admin y
/// facturación (decisión 5).
///
/// Sin validador, porque no hay texto libre, y sin evento de outbox, igual que aprobar.
/// </summary>
public sealed class InvoiceOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
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

        // InvoicedBy es un id de membresía, como ApprovedBy: el módulo nunca guarda el usuario.
        var invoicedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.Invoice(invoicedBy, now);

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
