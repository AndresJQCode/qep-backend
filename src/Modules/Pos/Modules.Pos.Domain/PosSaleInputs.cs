namespace Modules.Pos.Domain;

/// <summary>Una línea ya resuelta contra el catálogo: el precio y la tasa son los de ahora.</summary>
/// <param name="VariantId">La variante que se vende (spec 2026-10-20 §4): la línea es por variante.</param>
/// <param name="ProductId">El producto dueño de la variante, para agrupar por producto en reportes.</param>
/// <param name="ProductName">El nombre visible de la variante; la línea lo recorta a
/// <see cref="PosSaleLine.ProductNameMaxLength"/>.</param>
public sealed record PosSaleLineInput(
    Guid VariantId,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage);

/// <summary>Cash trae sólo Tendered (el Amount lo calcula el dominio); Card/Transfer traen Amount.</summary>
public sealed record PosPaymentInput(
    PosPaymentMethod Method,
    decimal? Amount,
    decimal? Tendered,
    string? Reference);

/// <summary>Desglose de IVA por tasa para el ticket.</summary>
public sealed record PosTaxBreakdownEntry(int TaxPercentage, decimal Base, decimal TaxAmount);

/// <summary>
/// La foto del cliente al vender (spec 2026-10-14, decisión 8): se copia a la venta y no hay FK, así que
/// renombrar o desactivar al cliente después no cambia el ticket.
/// </summary>
public sealed record PosSaleCustomer(Guid Id, string Name, string IdentificationType, string IdentificationNumber);
