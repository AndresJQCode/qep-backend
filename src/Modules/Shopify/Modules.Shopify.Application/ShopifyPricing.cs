namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §6.2.2: cómo se llevan los precios de Shopify a los de QEP. Las líneas del pedido y el envío usan la misma
/// regla de IVA (D-S3): la tasa es la de QEP (la del producto o la del envío del tenant), no la que calculó Shopify; la
/// diferencia se ve en <c>total_difference</c> (D-S6).
/// </summary>
public static class ShopifyPricing
{
    /// <summary>La moneda en la que el IVA se discrimina; fuera de ella la tasa del pedido se fuerza a 0 (D-S27).</summary>
    public const string Cop = "COP";

    /// <summary>§6.2.2 (H3, D-S27): con impuestos incluidos el precio va tal cual; si no, en COP se le suma el IVA de
    /// <paramref name="rate"/> y fuera de COP va tal cual, porque ahí la tasa se fuerza a 0.</summary>
    public static decimal UnitPriceWithVat(decimal original, bool taxesIncluded, int rate, string currency) =>
        taxesIncluded || !IsCop(currency)
            ? original
            : original * (100 + rate) / 100m;

    /// <summary>§6.2.2: la suma de las asignaciones de descuento sobre <c>originalUnitPrice × quantity</c>, en porcentaje con 4
    /// decimales. Sin base (cantidad o precio 0) no hay porcentaje que calcular.</summary>
    public static decimal DiscountPercentage(decimal discount, decimal original, decimal qty)
    {
        var gross = original * qty;
        return gross <= 0m || discount <= 0m ? 0m : Math.Round(discount / gross * 100m, 4, MidpointRounding.AwayFromZero);
    }

    public static bool IsCop(string? currency) =>
        string.Equals(currency?.Trim(), Cop, StringComparison.OrdinalIgnoreCase);
}
