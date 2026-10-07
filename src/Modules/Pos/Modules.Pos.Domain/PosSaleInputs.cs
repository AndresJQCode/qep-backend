namespace Modules.Pos.Domain;

/// <summary>Una línea ya resuelta contra el catálogo: el precio y la tasa son los de ahora.</summary>
public sealed record PosSaleLineInput(
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
