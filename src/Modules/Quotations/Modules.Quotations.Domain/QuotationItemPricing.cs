namespace Modules.Quotations.Domain;

/// <summary>
/// El precio de una línea en una moneda dada, ya resuelto por la aplicación contra el catálogo
/// (precio base, escala de cantidad y tasa de impuesto del producto).
///
/// Existe para revalorizar: cuando la cotización cambia de moneda, cada línea guardada tiene que
/// volver a nacer con el precio del producto en la moneda nueva. El dominio no sabe consultar el
/// catálogo — recibe el resultado y lo aplica, mismo criterio que <c>AddItem</c>.
/// </summary>
public sealed record QuotationItemPricing(
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage);
