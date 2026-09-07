namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia el módulo Catalog (US-3/US-4: precio base y escalas de precio por cantidad del
/// producto). Mismo criterio de aislamiento que <see cref="IQuotationCustomerLookup"/> — el
/// adaptador vive en <c>Bootstrapper</c>.
///
/// Expone las **dos** monedas que el catálogo guarda, no una: la cotización se expresa en la
/// moneda de su cuenta de cobro (<c>Quotation.Currency</c>), y cuál de los dos precios aplica lo
/// decide <see cref="QuotationProductPricingResolver"/>. Un producto sin precio en la moneda de
/// la cotización no se puede cotizar ahí — no hay tabla de cambio y este módulo no convierte.
///
/// <see cref="FindManyAsync"/> existe para revalorizar: cambiar la moneda de una cotización
/// obliga a volver a pedir el precio de **cada** línea, y una consulta por línea convierte un
/// cambio de cuenta en veinte lecturas.
/// </summary>
public interface IQuotationProductPricingLookup
{
    Task<QuotationProductPricingRef?> FindAsync(
        Guid tenantId, Guid productId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, QuotationProductPricingRef>> FindManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken);
}

/// <param name="TaxPercentage">La tasa de impuesto del producto
/// (<c>Catalog.TaxRate.Percentage</c>). Null si el producto no tiene tasa asignada — se resuelve
/// a 0% (RN-013, el gap que <c>Catalog.TaxRate</c> dejaba abierto a propósito).</param>
public sealed record QuotationProductPricingRef(
    Guid Id,
    Guid TenantId,
    /// <summary>Para el resumen del historial ("Agregó Bebidas x3"). Viaja con el precio y no
    /// por su propia consulta: el producto ya se carga entero para cotizarlo.</summary>
    string Name,
    bool IsActive,
    decimal? UnitPriceCop,
    decimal? UnitPriceUsd,
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
/// <param name="AllowGrouping">Si las cantidades de varias líneas que caen en esta misma escala
/// se suman para validar el múltiplo. Siempre <c>false</c> con <c>PackagingUnit</c>: lo hace
/// cumplir Catalog. Último y con default para no tocar las construcciones que ya existen.</param>
public sealed record QuotationPriceScaleRef(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    QuotationPriceScaleRestriction Restriction,
    int? Multiple,
    int? PackagingUnit,
    bool AllowGrouping = false);
