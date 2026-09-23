namespace Modules.Quotations.Domain;

/// <summary>
/// El descuento que el recálculo le baja a una línea, con su procedencia. Van juntos porque
/// aplicar uno sin el otro deja la línea diciendo que su 12% se lo dio el piso global cuando en
/// realidad la compuerta de compra mínima ya se lo quitó.
/// </summary>
public sealed record QuotationItemDiscount(decimal Percentage, QuotationDiscountOrigin Origin);
