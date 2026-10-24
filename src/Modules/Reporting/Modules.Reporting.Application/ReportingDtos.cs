namespace Modules.Reporting.Application;

/// <summary>
/// Un pedido, con sus propios importes y la cotización de origen ya resuelta cuando la hay (spec
/// 2026-10-15). <c>Source</c> viaja con el nombre (<c>Quotation</c>, <c>Manual</c>, <c>Shopify</c>…)
/// porque el diccionario lo tiene el frontend; un pedido manual o de una integración trae
/// <c>QuotationId</c> y <c>QuotationNumber</c> en <c>null</c>, y la tabla dibuja la celda vacía
/// sin tener que adivinar el origen.
///
/// <c>AdvisorName</c> es el **email** del asesor, no su nombre. El nombre vive en
/// <c>Tenancy.Membership.DisplayName</c> desde el 2026-09-11, pero sólo lo usan el PDF y el
/// listado de cotizaciones (spec 2026-09-11, D1 y su nota del 2026-09-14). El nombre del campo se
/// mantiene porque es el que el contrato de API fija con el frontend; léase "la etiqueta con la
/// que mostrar a esta persona". Nulo cuando la fila de usuario no está.
///
/// <c>ClientName</c> sí es un nombre real: sale de <c>Customer.Name</c>.
///
/// Desde el spec 2026-10-23 una fila también puede ser una **venta POS** (<c>Source = "Pos"</c>):
/// <c>OrderNumber</c> es el <c>POS-…</c> de la venta, <c>ConvertedAt</c> su creación,
/// <c>AdvisorId</c> el cajero, <c>Status</c> <c>Completed</c> o <c>Voided</c>,
/// <c>PaymentStatus</c> siempre <c>FullPaymentReceived</c> y el envío en cero.
/// </summary>
public sealed record OrdersReportItemDto(
    Guid OrderId,
    string OrderNumber,
    string Source,
    Guid? QuotationId,
    string? QuotationNumber,
    DateTimeOffset ConvertedAt,
    Guid AdvisorId,
    string? AdvisorName,
    /// <summary>Nulo sólo en una venta POS a consumidor final, que no tiene ficha de cliente: inventar un id
    /// sería peor (spec 2026-10-23 §1). Ahí <c>ClientName</c> es el nombre que guardó la venta
    /// («Consumidor final») y <c>ClientCuc</c> es nulo.</summary>
    Guid? ClientId,
    string? ClientName,
    string? ClientCuc,
    string Status,
    string PaymentStatus,
    /// <summary>The quotation's currency: every amount of this row is in it.</summary>
    string Currency,
    /// <summary>Sólo productos: el envío no está acá sino en los dos campos siguientes.</summary>
    decimal Subtotal,
    decimal TaxAmount,
    /// <summary>El envío cobrado sin IVA, o 0. <c>Total</c> ya lo incluye: se explica con un campo y no con
    /// una omisión, así la fila cierra (<c>Subtotal + TaxAmount + FreightSubtotal + FreightTaxAmount = Total</c>)
    /// sin que la pantalla sepa del envío (spec 2026-10-17 §7).</summary>
    decimal FreightSubtotal,
    decimal FreightTaxAmount,
    decimal Total);

/// <summary>
/// Una cotización, en cualquiera de sus cinco estados. Ver <see cref="OrdersReportItemDto"/> sobre
/// <c>AdvisorName</c>.
///
/// <c>ValidUntil</c> es opcional porque el dominio lo permite: <c>Quotation.ValidUntil</c> es
/// <c>DateOnly?</c> y una cotización en borrador puede no tenerlo todavía.
/// </summary>
public sealed record QuotationsReportItemDto(
    Guid QuotationId,
    string QuotationNumber,
    DateTimeOffset CreatedAt,
    DateOnly? ValidUntil,
    Guid AdvisorId,
    string? AdvisorName,
    Guid ClientId,
    string? ClientName,
    string? ClientCuc,
    string Status,
    /// <summary>The quotation's currency: every amount of this row is in it.</summary>
    string Currency,
    /// <summary>Sólo productos: el envío no está acá sino en los dos campos siguientes.</summary>
    decimal Subtotal,
    decimal TaxAmount,
    /// <summary>El envío cobrado sin IVA, o 0. <c>Total</c> ya lo incluye: se explica con un campo y no con
    /// una omisión, así la fila cierra (<c>Subtotal + TaxAmount + FreightSubtotal + FreightTaxAmount = Total</c>)
    /// sin que la pantalla sepa del envío (spec 2026-10-17 §7).</summary>
    decimal FreightSubtotal,
    decimal FreightTaxAmount,
    decimal Total);

/// <summary>
/// Un cambio de precio del **catálogo de productos**: el precio base en cada moneda y el descuento
/// de una escala. No tiene nada que ver con los precios de una línea de cotización.
///
/// <c>Currency</c> is the ISO 4217 code of a <c>PriceBase</c> row; null for <c>ScaleDiscount</c>.
///
/// <c>ScaleFromUnit</c>/<c>ScaleToUnit</c> vienen con valor sólo cuando <c>Field</c> es
/// <c>ScaleDiscount</c>: los precios base son del producto entero y no tienen rango.
///
/// <c>ChangedByName</c> es el email del autor. Ver <see cref="OrdersReportItemDto"/>.
///
/// **El cambio es de una variante** (spec 2026-10-20 §3.5) y la fila lo dice con los cuatro campos
/// que la pantalla pinta, para que no tenga que cruzar con el catálogo: <c>VariantId</c> (lo que
/// manda de vuelta al filtro <c>variantId</c>), <c>ProductCode</c> (el <c>Code</c> de **esa
/// variante**; el nombre se conserva porque es el que la tabla ya pintaba y el producto dejó de
/// tener código propio), <c>ProductName</c> (el del producto, para agrupar sin partir un texto) y
/// <c>VariantName</c> (el nombre visible de §3.4, «Camiseta – M / Azul»; en un producto sin
/// opciones es igual a <c>ProductName</c>). Se leen vivos, no son foto: a diferencia de una línea
/// de cotización, una fila del histórico describe el catálogo de hoy.
/// </summary>
public sealed record PriceChangeReportItemDto(
    Guid ChangeId,
    Guid ProductId,
    Guid VariantId,
    string ProductCode,
    string ProductName,
    string VariantName,
    string Field,
    string? Currency,
    int? ScaleFromUnit,
    int? ScaleToUnit,
    decimal? PreviousValue,
    decimal? NewValue,
    decimal Difference,
    Guid ChangedById,
    string? ChangedByName,
    DateTimeOffset ChangedAt);

/// <summary>
/// Un cliente del padrón, con clasificación y geografía resueltas.
///
/// **Sin columna de lista de precios**: la relación cliente/lista de precios se retiró del
/// sistema el 2026-08-23 (commit <c>78f30a0</c>, migración
/// <c>20260823200904_DropObsoletePriceListColumn</c>), así que el dato ya no existe.
///
/// <c>IdentificationType</c> viaja como el nombre del enum (<c>Nit</c>), que es lo que el
/// contrato de este reporte fija — **no** el valor en mayúsculas (<c>NIT</c>) que usan los
/// endpoints de <c>customers</c>.
/// </summary>
public sealed record CustomerReportItemDto(
    Guid CustomerId,
    string Cuc,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    Guid ClassificationId,
    string? ClassificationName,
    Guid? DepartmentId,
    string? DepartmentName,
    /// <summary>Nulo para un cliente que no es de Colombia: no tiene ciudad DIVIPOLA. Su ciudad
    /// viaja igual en <c>CityName</c>, escrita a mano.</summary>
    Guid? CityId,
    string? CityName,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>
/// El sobre de un listado de reporte. Genérico y no cuatro records casi idénticos: a diferencia
/// de <c>ProductsResponse</c>/<c>CustomersResponse</c>, que envuelven tipos con historia propia,
/// acá los cuatro sobres tendrían exactamente la misma forma y ninguna razón para divergir.
/// </summary>
public sealed record ReportPage<TItem>(
    IReadOnlyList<TItem> Items,
    int Total,
    int Page,
    int PageSize);
