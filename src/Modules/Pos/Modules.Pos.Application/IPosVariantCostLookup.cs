namespace Modules.Pos.Application;

/// <summary>
/// El costo manual vigente de las variantes, que Pos congela en cada línea al vender (spec
/// 2026-11-10 §4.2). Puerto aparte de <see cref="IPosProductLookup"/>: el costo nunca sale en una
/// respuesta del POS (§6.2), así que no viaja en <c>PosProductRef</c>.
/// </summary>
public interface IPosVariantCostLookup
{
    /// <summary>
    /// Por id de variante, sólo las del tenant que tienen costo en <paramref name="currency"/>; una
    /// variante sin costo en esa moneda simplemente no aparece.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> FindAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}
