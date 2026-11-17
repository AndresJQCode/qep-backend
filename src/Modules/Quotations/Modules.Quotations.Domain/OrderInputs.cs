namespace Modules.Quotations.Domain;

/// <summary>La cabecera que un pedido manual o externo trae resuelta desde Application (spec 2026-10-15 §4.4).
/// <see cref="Withholdings"/> son las tasas de retención que el pedido congela al nacer (spec impuestos por tipo
/// §7.5): las del cliente, ya resueltas con la fecha del pedido.</summary>
public sealed record OrderHeader(
    Guid ClientId,
    MemberId AdvisorId,
    string Currency,
    string? PaymentMethod,
    bool IsRetail,
    int? GlobalScaleFloor,
    bool CustomerVatSurplus,
    IReadOnlyList<WithholdingRate> Withholdings);

/// <summary>Una línea ya preciada: Application resolvió precio, descuento y tasa contra el catálogo,
/// igual que hace el cotizador. <see cref="Product"/> es la foto de código y nombre, si ya se tiene.
/// <see cref="VariantId"/> es la clave de la línea (spec 2026-10-20 §4); <see cref="ProductId"/> es su
/// producto, para agrupar por producto.</summary>
public sealed record OrderLineInput(
    Guid VariantId,
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage,
    QuotationProductSnapshot? Product = null,
    QuotationDiscountOrigin DiscountOrigin = QuotationDiscountOrigin.Own);
