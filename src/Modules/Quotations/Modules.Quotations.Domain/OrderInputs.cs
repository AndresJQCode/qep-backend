namespace Modules.Quotations.Domain;

/// <summary>La cabecera que un pedido manual o externo trae resuelta desde Application (spec 2026-10-15 §4.4).</summary>
public sealed record OrderHeader(
    Guid ClientId,
    MemberId AdvisorId,
    string Currency,
    string? PaymentMethod,
    bool IsRetail,
    int? GlobalScaleFloor,
    bool CustomerWithRetention,
    bool CustomerVatSurplus);

/// <summary>Una línea ya preciada: Application resolvió precio, descuento y tasa contra el catálogo,
/// igual que hace el cotizador. <see cref="Product"/> es la foto de código y nombre, si ya se tiene.</summary>
public sealed record OrderLineInput(
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage,
    QuotationProductSnapshot? Product = null,
    QuotationDiscountOrigin DiscountOrigin = QuotationDiscountOrigin.Own);
