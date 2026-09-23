namespace Modules.Reporting.Application;

/// <summary>
/// Un pedido convertido, con la cotización de origen ya resuelta. Los importes son los de la
/// cotización: <c>Order</c> no los duplica (modelo-datos-cotizaciones.md §1.2).
///
/// <c>AdvisorName</c> es el **email** del asesor, no su nombre. El nombre vive en
/// <c>Tenancy.Membership.DisplayName</c> desde el 2026-09-11, pero sólo lo usan el PDF y el
/// listado de cotizaciones (spec 2026-09-11, D1 y su nota del 2026-09-14). El nombre del campo se
/// mantiene porque es el que el contrato de API fija con el frontend; léase "la etiqueta con la
/// que mostrar a esta persona". Nulo cuando la fila de usuario no está.
///
/// <c>ClientName</c> sí es un nombre real: sale de <c>Customer.Name</c>.
/// </summary>
public sealed record OrdersReportItemDto(
    Guid OrderId,
    string OrderNumber,
    Guid QuotationId,
    string QuotationNumber,
    DateTimeOffset ConvertedAt,
    Guid AdvisorId,
    string? AdvisorName,
    Guid ClientId,
    string? ClientName,
    string? ClientCuc,
    string Status,
    string PaymentStatus,
    decimal Subtotal,
    decimal TaxAmount,
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
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total);

/// <summary>
/// Un cambio de precio del **catálogo de productos**: los dos precios base y el descuento de una
/// escala. No tiene nada que ver con los precios de una línea de cotización.
///
/// <c>ScaleFromUnit</c>/<c>ScaleToUnit</c> vienen con valor sólo cuando <c>Field</c> es
/// <c>ScaleDiscount</c>: los precios base son del producto entero y no tienen rango.
///
/// <c>ChangedByName</c> es el email del autor. Ver <see cref="OrdersReportItemDto"/>.
/// </summary>
public sealed record PriceChangeReportItemDto(
    Guid ChangeId,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    string Field,
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
