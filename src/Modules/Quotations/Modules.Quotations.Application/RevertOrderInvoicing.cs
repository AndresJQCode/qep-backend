using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record RevertOrderInvoicingCommand(Guid TenantId, Guid OrderId)
    : ICommand<OrderDto>;

/// <summary>
/// Revierte la facturación de un pedido (spec 2026-10-05, decisión 4): vuelve a <c>Approved</c>
/// sin <c>InvoicedAt</c>/<c>InvoicedBy</c>. Es para corregir una marca equivocada; no anula nada
/// fuera de QEP. Calcado de <see cref="InvoiceOrderHandler"/>, con el mismo permiso
/// (<see cref="OrdersPermissions.OrderInvoice"/>): con uno aparte, facturación dependería de un
/// admin para corregir su propio error (decisión 5).
///
/// La historia —quién facturó y quién revirtió— queda en la auditoría
/// (<c>quotation.order.invoiced</c> y <c>quotation.order.invoice_reverted</c>), no en el pedido.
/// </summary>
public sealed class RevertOrderInvoicingHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RevertOrderInvoicingCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        RevertOrderInvoicingCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderInvoice);

        var order = await repository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        // Exige una membresía activa, igual que facturar. El agregado no la guarda (hallazgo 4 del
        // plan): quién revirtió lo registra la auditoría con el usuario que actúa.
        var revertedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.RevertInvoicing(revertedBy, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.invoice_reverted",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
