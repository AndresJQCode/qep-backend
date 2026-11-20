using System.Text.Json.Serialization;

namespace Modules.Purchases.Application;

/// <summary>Una fila de la tabla del PDF, con cada valor ya formateado: la plantilla imprime texto, no calcula.</summary>
public sealed record PurchaseOrderPdfLine(
    string Code,
    string Name,
    string Quantity,
    string UnitCost,
    string TaxRate,
    string Subtotal);

/// <summary>
/// Lo que recibe la plantilla Typst de la orden de compra. Se serializa en camelCase (<c>data.number</c>, <c>data.lines</c>…): ese
/// JSON es el contrato con <c>purchase-order.typ</c>, así que renombrar una propiedad exige tocar la plantilla. Montos y fechas
/// llegan como texto con el formato del tenant (<see cref="PurchaseOrderPdfMapper"/>). <c>ShowsConfirmedValues</c> dice que al
/// menos una línea trae lo que confirmó el proveedor (D-5), y la plantilla lo aclara bajo la tabla.
/// </summary>
public sealed record PurchaseOrderPdfDocument(
    string TenantName,
    string Number,
    string StatusLabel,
    string IssuedOn,
    string SupplierName,
    string SupplierIdentification,
    string Currency,
    string? Notes,
    IReadOnlyList<PurchaseOrderPdfLine> Lines,
    string Subtotal,
    string Tax,
    string Total,
    bool ShowsConfirmedValues,
    // No viaja a la plantilla Typst (su contrato no cambia): sólo le dice al correo si imprime la tabla de montos.
    [property: JsonIgnore] bool ShowsAmounts = true);

/// <summary>Genera el PDF de una orden; el adaptador (Infrastructure) llama a <c>qcode-pdf</c> con su plantilla.</summary>
public interface IPurchaseOrderPdfRenderer
{
    Task<byte[]> RenderAsync(PurchaseOrderPdfDocument document, CancellationToken cancellationToken);
}
