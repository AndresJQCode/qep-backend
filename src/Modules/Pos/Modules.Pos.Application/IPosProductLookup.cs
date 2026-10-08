namespace Modules.Pos.Application;

/// <summary>
/// Puerto hacia Catalog (adaptador en Bootstrapper). Viajan las dos listas base, con IVA incluido
/// (regla detal); cuál se cobra lo decide la moneda de la caja (PosProductMapping.PriceIn).
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
    decimal? PriceCop,
    decimal? PriceUsd,
    int TaxPercentage,
    string? ImageUrl);
