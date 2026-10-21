namespace Modules.Pos.Application;

/// <summary>
/// Catalog → Pos (adapter in Bootstrapper). Por **variante** (spec 2026-10-20 §5): la caja vende
/// una variante, con sus precios —todos, IVA incluido; el que se cobra es el de la moneda de la
/// sesión (PosProductMapping.PriceIn)— y su nombre visible ya armado.
/// TaxPercentage 0 sin tasa o con tasa inexistente, como QuotationProductPricingResolver; la tasa
/// es del producto.
/// </summary>
public interface IPosProductLookup
{
    /// <summary>
    /// Una página de **productos** activos, en el orden de relevancia de
    /// ProductRepository.SearchAsync, cada uno con sus variantes vendibles (activas) en el orden del
    /// producto: la cuadrícula muestra productos y el selector de opciones elige entre sus
    /// variantes sin otra llamada (spec §5). <c>Total</c> cuenta productos.
    /// </summary>
    Task<(IReadOnlyList<PosCatalogProductRef> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// El escáner (spec §5): primero por código de barras de una variante y, si ninguna lo tiene,
    /// por su <c>Code</c>. Si el texto es el código de barras de una variante y el <c>Code</c> de
    /// otra, gana el código de barras. Igualdad exacta con mayúsculas. Activas e inactivas
    /// (<see cref="PosProductRef.IsActive"/> lo dice).
    /// </summary>
    Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken);

    /// <summary>Por id de variante, activas e inactivas; los ids que no existen en el tenant no
    /// aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

/// <summary>Una variante vendible en la caja.</summary>
/// <param name="ProductId">El producto dueño: la línea lo guarda junto a la variante.</param>
/// <param name="Code">El <c>Code</c> de la variante.</param>
/// <param name="Barcode">El código de barras de la variante, si tiene.</param>
/// <param name="Name">El nombre visible (spec §3.4): el del producto en la variante por defecto,
/// «{producto} – {v1} / {v2}» con opciones. Es el que imprime el ticket.</param>
/// <param name="IsActive">Vendible: el producto **y** la variante están activos.</param>
/// <param name="ImageUrl">La foto de la variante, o la principal del producto si no eligió
/// ninguna; sólo del tenant y disponible.</param>
public sealed record PosProductRef(
    Guid VariantId,
    Guid ProductId,
    string Code,
    string? Barcode,
    string Name,
    bool IsActive,
    IReadOnlyDictionary<string, decimal> Prices,
    int TaxPercentage,
    string? ImageUrl);

/// <summary>Un producto de la cuadrícula del POS con sus variantes vendibles.</summary>
/// <param name="Name">El nombre del producto, sin opciones.</param>
/// <param name="HasOptions">Si el producto tiene opciones: con opciones, tocarlo abre el selector;
/// sin ellas, su única variante va directo al carrito (spec §5).</param>
/// <param name="ImageUrl">La foto principal del producto.</param>
/// <param name="Variants">Las variantes activas, en el orden del producto.</param>
public sealed record PosCatalogProductRef(
    Guid ProductId,
    string Name,
    bool HasOptions,
    string? ImageUrl,
    IReadOnlyList<PosProductRef> Variants);
