using BuildingBlocks.Domain.Catalog;

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
/// producto, para agrupar por producto. <see cref="Kind"/> y <see cref="SalesUnit"/> son los del producto en el
/// catálogo (spec 2026-11-18 §3.4); una línea externa (Shopify) no los trae y nace <c>Physical</c>/<c>Unit</c>.
/// <see cref="SourcePurchaseOrderLineId"/> es la línea de la orden de compra de la que salió, sólo en un pedido
/// mayorista: con ella la aprobación vuelve a la orden línea por línea. <see cref="IsGift"/> (spec 2026-11-23) marca un
/// regalo: sólo lo usa el pedido manual al nacer; la línea ya viene a precio de lista y el dominio fuerza el 100 %.</summary>
public sealed record OrderLineInput(
    Guid VariantId,
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage,
    QuotationProductSnapshot? Product = null,
    QuotationDiscountOrigin DiscountOrigin = QuotationDiscountOrigin.Own,
    ProductKind Kind = ProductKind.Physical,
    SalesUnit SalesUnit = SalesUnit.Unit,
    Guid? SourcePurchaseOrderLineId = null,
    bool IsGift = false);
