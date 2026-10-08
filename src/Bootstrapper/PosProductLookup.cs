using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Pos.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Pos. Resuelve tasa e imagen como QuotationProductPricingLookup y QuotationProductLookup:
/// tasas por id distinto y las imágenes de toda la página en una sola llamada, no una por producto.
/// </summary>
internal sealed class PosProductLookup(
    IProductRepository products,
    ITaxRateRepository taxRates,
    IProductImageLookup images)
    : IPosProductLookup
{
    public async Task<(IReadOnlyList<PosProductRef> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (items, total) = await products.SearchAsync(
            tenantId, string.IsNullOrWhiteSpace(search) ? null : search, null, null, true, page, pageSize, cancellationToken);
        return (await MapAsync(tenantId, items, cancellationToken), total);
    }

    public async Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken)
    {
        var product = await products.FindByCodeAsync(tenantId, code, cancellationToken);
        return product is null ? null : (await MapAsync(tenantId, [product], cancellationToken))[0];
    }

    public async Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, PosProductRef>();
        }

        var found = await products.ListByIdsAsync(
            tenantId, productIds.Distinct().Select(id => new ProductId(id)).ToArray(), cancellationToken);
        return (await MapAsync(tenantId, found, cancellationToken)).ToDictionary(product => product.Id);
    }

    private async Task<IReadOnlyList<PosProductRef>> MapAsync(
        Guid tenantId, IReadOnlyList<Product> items, CancellationToken cancellationToken)
    {
        var percentages = new Dictionary<TaxRateId, int>();
        foreach (var taxRateId in items.Where(item => item.TaxRateId.HasValue).Select(item => item.TaxRateId!.Value).Distinct())
        {
            var taxRate = await taxRates.FindAsync(tenantId, taxRateId, cancellationToken);
            if (taxRate is not null)
            {
                percentages[taxRateId] = taxRate.Percentage;
            }
        }

        var imageIds = items.Where(item => item.ImageFileId.HasValue).Select(item => item.ImageFileId!.Value).Distinct().ToArray();
        IReadOnlyDictionary<Guid, ProductImageRef> found = imageIds.Length == 0
            ? new Dictionary<Guid, ProductImageRef>()
            : await images.FindManyAsync(imageIds, cancellationToken);

        return items.Select(item => new PosProductRef(
            item.Id.Value,
            item.Code,
            item.Name,
            item.IsActive,
            item.PriceBaseCop,
            item.PriceBaseUsd,
            // Sin tasa, o con una inexistente, cotiza con 0 % (QuotationProductPricingResolver).
            item.TaxRateId is { } id && percentages.TryGetValue(id, out var percentage) ? percentage : 0,
            // Mismas reglas que QuotationProductLookup: del tenant y disponible (preflight F-16);
            // un archivo en cuarentena o pendiente no debe llegar a la pantalla.
            item.ImageFileId is { } fileId && found.TryGetValue(fileId, out var image)
                && image.TenantId == tenantId && image.IsAvailable
                ? image.PublicUrl
                : null)).ToList();
    }
}
