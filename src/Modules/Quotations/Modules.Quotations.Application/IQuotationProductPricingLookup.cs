namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia el módulo Catalog (US-3/US-4: precio base y escalas de precio por cantidad). Mismo
/// criterio de aislamiento que <see cref="IQuotationCustomerLookup"/> — el adaptador vive en
/// <c>Bootstrapper</c>.
///
/// Por **variante** (spec 2026-10-20 §4): precio, escalas y empaques son de la variante que vende
/// la línea; la tasa de impuesto sigue siendo del producto (spec §2, «IVA por variante» fuera).
///
/// Exposes **every** base price the catalogue holds, one per currency: the quotation is expressed
/// in its billing account currency (<c>Quotation.Currency</c>) and
/// <see cref="QuotationProductPricingResolver"/> takes that one and no other. A variant without a
/// price in that currency cannot be quoted there — there is no conversion.
///
/// <see cref="FindManyAsync"/> existe para revalorizar: cambiar la moneda de una cotización
/// obliga a volver a pedir el precio de **cada** línea, y una consulta por línea convierte un
/// cambio de cuenta en veinte lecturas.
/// </summary>
public interface IQuotationProductPricingLookup
{
    /// <summary>La variante, activa o no (<see cref="QuotationVariantPricingRef.IsActive"/> lo
    /// dice); null si no existe en el tenant.</summary>
    Task<QuotationVariantPricingRef?> FindVariantAsync(
        Guid tenantId, Guid variantId, CancellationToken cancellationToken);

    /// <summary>Por id de variante, activas e inactivas; un id que no existe en el tenant no
    /// aparece.</summary>
    Task<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>> FindManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken);
}

/// <param name="ProductId">El producto dueño: la línea lo guarda junto a la variante (spec §4) y
/// la petición sólo trae el id de la variante.</param>
/// <param name="Name">El nombre visible de la variante (spec §3.4), ya armado. Para el resumen del
/// historial ("Agregó Camiseta – M x3"). Viaja con el precio y no por su propia consulta: el
/// producto ya se carga entero para cotizarlo.</param>
/// <param name="Code">El <c>Code</c> de la variante.</param>
/// <param name="IsActive">Vendible: el producto **y** la variante están activos. Una variante
/// activa de un producto inactivo no se vende, igual que hoy no se vende un producto inactivo.</param>
/// <param name="TaxPercentage">La tasa de impuesto del producto
/// (<c>Catalog.TaxRate.Percentage</c>). Null si el producto no tiene tasa asignada — se resuelve
/// a 0% (RN-013, el gap que <c>Catalog.TaxRate</c> dejaba abierto a propósito).</param>
public sealed record QuotationVariantPricingRef(
    Guid VariantId,
    Guid ProductId,
    Guid TenantId,
    string Name,
    string Code,
    bool IsActive,
    IReadOnlyDictionary<string, decimal> Prices,
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

/// <param name="Restriction">Null en una escala incompleta: la que deja la copia de escalas en
/// Catalog, con rango y descuento pero sin restricción. Un producto con una sola así no se cotiza
/// (<see cref="QuotationProductPricingResolver"/>), pero una cotización que ya lo tenía se sigue
/// pudiendo leer.</param>
/// <param name="Multiple">Poblado sólo cuando <paramref name="Restriction"/> es
/// <c>Multiple</c>. La exclusión mutua con <paramref name="PackagingUnits"/> la garantiza el
/// adaptador de <c>Bootstrapper</c> (<c>QuotationPriceScaleMapping</c>), que sólo copia los
/// empaques de la variante en las escalas <c>PackagingUnit</c>; Catalog ya no la ve, porque los
/// empaques dejaron de ser de la escala.</param>
/// <param name="PackagingUnits">Los empaques de la **variante**, no de la escala: Catalog los
/// guarda una vez por variante (spec 2026-10-20 §2; por producto entre el 2026-10-01 y las
/// variantes), y el adaptador de <c>Bootstrapper</c> los copia en cada escala <c>PackagingUnit</c>
/// para que la regla no necesite la variante. Vacío en
/// cualquier otra restricción.</param>
/// <param name="AllowGrouping">Si las cantidades de varias líneas que caen en esta misma escala
/// se suman para validar el múltiplo. Siempre <c>false</c> con <c>PackagingUnit</c>: lo hace
/// cumplir Catalog. Último y con default para no tocar las construcciones que ya existen.</param>
public sealed record QuotationPriceScaleRef(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    QuotationPriceScaleRestriction? Restriction,
    int? Multiple,
    IReadOnlyList<int> PackagingUnits,
    bool AllowGrouping = false);
