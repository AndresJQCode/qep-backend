namespace Modules.Pos.Application;

/// <summary>
/// Puerto hacia Catalog (adaptador en Bootstrapper). Precio = lista COP con IVA incluido (regla
/// detal); TaxPercentage 0 sin tasa o con tasa inexistente, como QuotationProductPricingResolver.
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
    Guid Id, string Code, string Name, bool IsActive, decimal? PriceCop, int TaxPercentage, string? ImageUrl);
