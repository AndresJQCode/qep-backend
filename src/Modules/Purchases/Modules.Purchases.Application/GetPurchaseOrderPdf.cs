using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record GetPurchaseOrderPdfQuery(Guid TenantId, Guid OrderId) : IQuery<PurchaseOrderPdfFile>;

/// <summary>El PDF y su nombre de descarga (<c>OC-0001.pdf</c>).</summary>
public sealed record PurchaseOrderPdfFile(byte[] Content, string FileName);

/// <summary>
/// El PDF de una orden en cualquier estado, con el permiso de leer. Arma el mismo detalle que <c>GET /{id}</c> (así el PDF no
/// puede mostrar otros totales) y le suma el documento del proveedor; el resto (nombre del tenant, formato regional, día local)
/// lo resuelve <see cref="IPurchaseOrderDocumentBuilder"/>, el mismo que arma el correo al proveedor.
/// </summary>
public sealed class GetPurchaseOrderPdfHandler(
    IExecutionContext executionContext,
    IPurchaseOrderRepository orders,
    IPurchasesSupplierLookup suppliers,
    PurchaseOrderComposer composer,
    IPurchaseOrderDocumentBuilder documents)
    : IQueryHandler<GetPurchaseOrderPdfQuery, PurchaseOrderPdfFile>
{
    public async Task<PurchaseOrderPdfFile> HandleAsync(GetPurchaseOrderPdfQuery query, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, query.TenantId, PurchasesPermissions.OrderRead);
        var order = await orders.GetAsync(query.TenantId, new PurchaseOrderId(query.OrderId), cancellationToken)
            ?? throw PurchasesConflicts.NotFound(query.OrderId);

        var detail = await composer.DetailAsync(order, cancellationToken);
        // Un proveedor que ya no está se imprime con el nombre del detalle y sin documento, como lo muestra la pantalla.
        var supplier = await suppliers.FindAsync(query.TenantId, order.SupplierId, cancellationToken)
            ?? new PurchasesSupplierRef(order.SupplierId, detail.Supplier.Name, string.Empty, string.Empty, false);

        var bundle = await documents.BuildAsync(order, detail, supplier, cancellationToken);
        return new PurchaseOrderPdfFile(bundle.Pdf, bundle.FileName);
    }
}
