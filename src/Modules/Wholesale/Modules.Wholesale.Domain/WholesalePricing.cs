using BuildingBlocks.Domain.Pricing;

namespace Modules.Wholesale.Domain;

/// <summary>
/// Spec 2026-11-17 (mayorista), D-10: el precio que ve el distribuidor. La regla de redondeo es la del mayorista para su moneda
/// por defecto: con COP sin decimales, dos decimales darían precios que el mayorista nunca pondría. La fase 3 la reutiliza
/// para el pedido.
/// </summary>
public static class WholesalePricing
{
    public static decimal ListPrice(decimal basePrice, decimal discountPercent, PriceRounding rounding) =>
        rounding.Apply(basePrice * (1 - (discountPercent / 100m)));
}
