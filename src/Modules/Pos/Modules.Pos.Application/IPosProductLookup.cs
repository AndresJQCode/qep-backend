namespace Modules.Pos.Application;

/// <summary>
/// Catalog → Pos (adapter in Bootstrapper). Every base price the product has, VAT included; which
/// one is charged is the session currency's (PosProductMapping.PriceIn).
/// TaxPercentage 0 sin tasa o con tasa inexistente, como QuotationProductPricingResolver.
/// </summary>
public interface IPosProductLookup
{
    /// <summary>Sólo activos, en el orden de relevancia de ProductRepository.SearchAsync.</summary>
    Task<(IReadOnlyList<PosProductRef> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Igualdad exacta con mayúsculas. Activos e inactivos.</summary>
    Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken);

    /// <summary>Activos e inactivos; los ids que no existen en el tenant no aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}

public sealed record PosProductRef(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    IReadOnlyDictionary<string, decimal> Prices,
    int TaxPercentage,
    string? ImageUrl);
