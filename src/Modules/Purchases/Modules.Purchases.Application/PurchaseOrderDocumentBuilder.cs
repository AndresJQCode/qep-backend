using Modules.Purchases.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Purchases.Application;

/// <summary>El documento de la orden (ya formateado con el formato regional del tenant) y su PDF generado.</summary>
public sealed record PurchaseOrderDocumentBundle(PurchaseOrderPdfDocument Document, byte[] Pdf, string FileName);

/// <summary>
/// Arma el documento de una orden y su PDF. Lo comparten la descarga del PDF y el correo al proveedor: así el correo, el PDF y la
/// pantalla no pueden mostrar totales distintos.
/// </summary>
public interface IPurchaseOrderDocumentBuilder
{
    Task<PurchaseOrderDocumentBundle> BuildAsync(
        PurchaseOrder order, PurchaseOrderDetailDto detail, PurchasesSupplierRef supplier, CancellationToken cancellationToken);
}

/// <summary>
/// Al detalle de la orden le suma el nombre del tenant y su formato regional, y fecha el documento con el día local del tenant en
/// que la orden se envió (o se creó, si nunca se envió).
/// </summary>
public sealed class PurchaseOrderDocumentBuilder(
    ITenantDirectory tenants,
    ITenantClock tenantClock,
    IPurchaseOrderPdfRenderer renderer) : IPurchaseOrderDocumentBuilder
{
    public async Task<PurchaseOrderDocumentBundle> BuildAsync(
        PurchaseOrder order, PurchaseOrderDetailDto detail, PurchasesSupplierRef supplier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(supplier);

        var tenantId = new TenantId(order.TenantId);
        var tenantName = await tenants.GetDisplayNameAsync(tenantId, cancellationToken) ?? string.Empty;
        var regional = await tenants.GetRegionalSettingsAsync(tenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(order.TenantId, cancellationToken);

        var document = PurchaseOrderPdfMapper.Map(detail, supplier, tenantName, regional, calendar.TimeZone);
        var content = await renderer.RenderAsync(document, cancellationToken);
        return new PurchaseOrderDocumentBundle(document, content, $"{order.Number}.pdf");
    }
}
