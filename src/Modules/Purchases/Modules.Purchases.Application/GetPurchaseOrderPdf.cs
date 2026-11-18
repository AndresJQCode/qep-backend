using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Purchases.Application;

public sealed record GetPurchaseOrderPdfQuery(Guid TenantId, Guid OrderId) : IQuery<PurchaseOrderPdfFile>;

/// <summary>El PDF y su nombre de descarga (<c>OC-0001.pdf</c>).</summary>
public sealed record PurchaseOrderPdfFile(byte[] Content, string FileName);

/// <summary>
/// El PDF de una orden en cualquier estado, con el permiso de leer. Arma el mismo detalle que <c>GET /{id}</c> (así el PDF no
/// puede mostrar otros totales), le suma el documento del proveedor, el nombre del tenant y su formato regional, y fecha el
/// documento con el día local del tenant en que la orden se envió (o se creó, si nunca se envió).
/// </summary>
public sealed class GetPurchaseOrderPdfHandler(
    IExecutionContext executionContext,
    IPurchaseOrderRepository orders,
    IPurchasesSupplierLookup suppliers,
    PurchaseOrderComposer composer,
    ITenantDirectory tenants,
    ITenantClock tenantClock,
    IPurchaseOrderPdfRenderer renderer)
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
        var tenantId = new TenantId(query.TenantId);
        var tenantName = await tenants.GetDisplayNameAsync(tenantId, cancellationToken) ?? string.Empty;
        var regional = await tenants.GetRegionalSettingsAsync(tenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        var document = PurchaseOrderPdfMapper.Map(detail, supplier, tenantName, regional, calendar.TimeZone);
        var content = await renderer.RenderAsync(document, cancellationToken);

        return new PurchaseOrderPdfFile(content, $"{order.Number}.pdf");
    }
}
