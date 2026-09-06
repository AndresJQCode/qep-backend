namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia el módulo Catalog (US-3/US-4: precio base y escalas de precio por cantidad del
/// producto). Mismo criterio de aislamiento que <see cref="IQuotationCustomerLookup"/> — el
/// adaptador vive en <c>Bootstrapper</c>.
///
/// Sólo expone el precio en COP: el módulo de cotizaciones trabaja en una sola moneda
/// (US-5, "todos los valores monetarios se muestran en COP"). Un producto sin precio base en COP
/// no se puede cotizar — lo resuelve <c>QuotationProductPricingResolver</c>.
/// </summary>
public interface IQuotationProductPricingLookup
{
    Task<QuotationProductPricingRef?> FindAsync(
        Guid tenantId, Guid productId, CancellationToken cancellationToken);
}

/// <param name="TaxPercentage">La tasa de impuesto del producto
/// (<c>Catalog.TaxRate.Percentage</c>). Null si el producto no tiene tasa asignada — se resuelve
/// a 0% (RN-013, el gap que <c>Catalog.TaxRate</c> dejaba abierto a propósito).</param>
public sealed record QuotationProductPricingRef(
    Guid Id,
    Guid TenantId,
    bool IsActive,
    decimal? UnitPriceCop,
    IReadOnlyCollection<QuotationPriceScaleRef> Scales,
    int? TaxPercentage);

/// <summary>
/// Espeja a <c>Catalog.Domain.PriceScaleRestriction</c> sin referenciarlo: este assembly no
/// puede depender de <c>Catalog</c> —lo verifica <c>QuotationsLayerTests</c>— y el adaptador de
/// <c>Bootstrapper</c> es el único que traduce entre los dos. Si allá se agrega un caso, acá
/// hay que agregarlo a mano.
/// </summary>
public enum QuotationPriceScaleRestriction
{
    Multiple,
    PackagingUnit
}

/// <param name="Multiple">Poblado sólo cuando <paramref name="Restriction"/> es
/// <c>Multiple</c>; el dominio de Catalog garantiza la exclusión mutua con
/// <paramref name="PackagingUnit"/>.</param>
public sealed record QuotationPriceScaleRef(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    QuotationPriceScaleRestriction Restriction,
    int? Multiple,
    int? PackagingUnit);
