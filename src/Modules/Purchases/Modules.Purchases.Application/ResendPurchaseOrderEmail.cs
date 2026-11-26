using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record ResendPurchaseOrderEmailCommand(Guid TenantId, Guid OrderId) : ICommand<PurchaseOrderDetailDto>;

/// <summary>
/// Vuelve a mandar la orden al correo del proveedor. No cambia la orden: sin <c>If-Match</c>, sin subir su versión (un reenvío no
/// puede dejar a otro miembro con la versión vieja) y sin pasar por el historial. Sólo en una orden enviada, confirmada o recibida.
/// El destino es el correo que el proveedor tiene guardado hoy, nunca uno que traiga la petición; sin él, 422. Un fallo del canal no
/// es un error HTTP: se responde 200 con <c>email.status = Failed</c>.
/// </summary>
public sealed class ResendPurchaseOrderEmailHandler(
    IExecutionContext executionContext,
    IPurchaseOrderRepository orders,
    IPurchasesSupplierLookup suppliers,
    IClock clock,
    PurchaseOrderComposer composer,
    PurchaseOrderEmailDelivery emailDelivery)
    : ICommandHandler<ResendPurchaseOrderEmailCommand, PurchaseOrderDetailDto>
{
    public async Task<PurchaseOrderDetailDto> HandleAsync(ResendPurchaseOrderEmailCommand command, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, command.TenantId, PurchasesPermissions.OrderManage);
        var order = await orders.GetAsync(command.TenantId, new PurchaseOrderId(command.OrderId), cancellationToken)
            ?? throw PurchasesConflicts.NotFound(command.OrderId);
        if (!order.CanEmailSupplier)
        {
            throw PurchasesConflicts.EmailNotAllowed();
        }

        var supplier = await suppliers.FindAsync(command.TenantId, order.SupplierId, cancellationToken);
        if (supplier is not { Email: { } address } || string.IsNullOrWhiteSpace(address))
        {
            throw PurchasesConflicts.SupplierEmailMissing();
        }

        var detail = await composer.DetailAsync(order, cancellationToken);
        var email = await emailDelivery.DeliverAsync(order, detail, supplier, executionContext.SubjectId, clock.UtcNow, cancellationToken);
        return detail with { Email = email };
    }
}
