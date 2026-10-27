using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Bootstrapper.UnitTests;

// Dobles de Catalog para los adaptadores por variante (Quotations, Pos y Shopify; spec 2026-10-20 §4, §5 y §2.9). A mano y
// sin librería de mocking, como el resto del repositorio. Lo que ningún adaptador usa lanza, para que una prueba no pase por
// un camino que no creía estar ejerciendo.

/// <summary>Los productos en memoria. Cada búsqueda filtra por tenant y compara como el índice único: igualdad ordinal.</summary>
internal sealed class FakeProductRepository(params Product[] products) : IProductRepository
{
    public int BarcodeLookups { get; private set; }

    public int CodeLookups { get; private set; }

    public Task<Product?> FindVariantByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken)
    {
        CodeLookups++;
        return Task.FromResult(products.FirstOrDefault(product => product.TenantId == tenantId
            && product.Variants.Any(variant => string.Equals(variant.Code, code, StringComparison.Ordinal))));
    }

    public Task<Product?> FindVariantByBarcodeAsync(Guid tenantId, string barcode, CancellationToken cancellationToken)
    {
        BarcodeLookups++;
        return Task.FromResult(products.FirstOrDefault(product => product.TenantId == tenantId
            && product.Variants.Any(variant => string.Equals(variant.Barcode, barcode, StringComparison.Ordinal))));
    }

    public Task<IReadOnlyList<Product>> FindVariantsByIdsAsync(
        Guid tenantId, IReadOnlyCollection<VariantId> variantIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Product>>(products
            .Where(product => product.TenantId == tenantId && product.Variants.Any(variant => variantIds.Contains(variant.Id)))
            .ToList());

    /// <summary>Las categorías de la última búsqueda, tal como llegaron (null = sin filtro).</summary>
    public IReadOnlyCollection<Guid>? LastCategoryIds { get; private set; }

    public int SearchCalls { get; private set; }

    // Sin relevancia ni filtro de texto ni de categoría: lo que se prueba es el mapeo del adaptador, no
    // la consulta. Las categorías sólo se graban, para ver qué le pidió el adaptador al repositorio.
    public Task<(IReadOnlyList<Product> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, string? name, string? code, bool? isActive, IReadOnlyCollection<Guid>? categoryIds, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        SearchCalls++;
        LastCategoryIds = categoryIds;
        var matching = products
            .Where(product => product.TenantId == tenantId && (isActive is null || product.IsActive == isActive))
            .ToList();
        return Task.FromResult<(IReadOnlyList<Product>, int)>(
            (matching.Skip((page - 1) * pageSize).Take(pageSize).ToList(), matching.Count));
    }

    public Task<IReadOnlyList<Product>> ListForExportAsync(
        Guid tenantId, string? search, string? name, string? code, bool? isActive, int skip, int take,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Product>> FindVariantsByIdsForUpdateAsync(
        Guid tenantId, IReadOnlyCollection<VariantId> variantIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Product?> FindAsync(Guid tenantId, ProductId productId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlySet<string>> FindExistingCodesAsync(
        Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlySet<string>> FindExistingBarcodesAsync(
        Guid tenantId, IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<SellableVariantSummary>> ListActiveVariantsAsync(
        Guid tenantId, IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<(IReadOnlyList<ProductSummary> Items, int Total)> SearchSummariesAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        IReadOnlyCollection<Guid>? categoryIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(Product product) => throw new NotSupportedException();

    public void AddPriceChanges(IReadOnlyList<ProductPriceChange> changes) => throw new NotSupportedException();

    public Task<bool> AnyWithTaxRateAsync(Guid tenantId, TaxRateId taxRateId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(products.Count(product => product.TenantId == tenantId && product.IsActive));
}

/// <summary>La biblioteca de opciones en memoria; <see cref="ListByIdsAsync"/> filtra por tenant como la real.</summary>
internal sealed class FakeOptionDefinitionRepository(params OptionDefinition[] options) : IOptionDefinitionRepository
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<OptionDefinition>> ListByIdsAsync(
        Guid tenantId, IReadOnlyCollection<OptionDefinitionId> optionIds, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<OptionDefinition>>(options
            .Where(option => option.TenantId == tenantId && optionIds.Contains(option.Id))
            .ToList());
    }

    public Task<IReadOnlyList<OptionDefinition>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<OptionDefinition?> FindAsync(
        Guid tenantId, OptionDefinitionId optionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<OptionUsage> GetUsageAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(OptionDefinition definition) => throw new NotSupportedException();

    public void Remove(OptionDefinition definition) => throw new NotSupportedException();
}

/// <summary>Los archivos en memoria, con el conteo de llamadas: los adaptadores prometen una sola por lote.</summary>
internal sealed class FakeProductImageLookup(params ProductImageRef[] images) : IProductImageLookup
{
    public int Calls { get; private set; }

    public List<IReadOnlyCollection<Guid>> Requests { get; } = [];

    public Task<ProductImageRef?> FindAsync(Guid fileId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<Guid, ProductImageRef>> FindManyAsync(
        IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        Calls++;
        Requests.Add([.. fileIds]);
        return Task.FromResult<IReadOnlyDictionary<Guid, ProductImageRef>>(
            images.Where(image => fileIds.Contains(image.FileId)).ToDictionary(image => image.FileId));
    }
}

/// <summary>
/// Una camiseta con «Talla» (M, L) y, si se pide, código de barras por variante y foto propia en M. La primera variante (M)
/// toma el id del producto, como cualquier producto nuevo.
/// </summary>
internal sealed record VariantWorld(Product Product, OptionDefinition Size, Guid MainFileId, Guid MediumFileId)
{
    public ProductVariant Medium => VariantWith("M");

    public ProductVariant Large => VariantWith("L");

    public static VariantWorld Create(
        Guid tenantId,
        DateTimeOffset at,
        string? mediumBarcode = null,
        string? largeBarcode = null,
        bool mediumHasOwnImage = true,
        TaxRateId? taxRateId = null,
        bool largeIsActive = true)
    {
        var size = OptionDefinition.Create(tenantId, "Talla", ["M", "L"], isActive: true, at);
        var medium = size.Values.Single(value => value.Value == "M").Id;
        var large = size.Values.Single(value => value.Value == "L").Id;
        var mainFileId = Guid.CreateVersion7();
        var mediumFileId = Guid.CreateVersion7();

        var product = Product.Create(
            ProductId.New(),
            tenantId,
            "Camiseta básica",
            new ProductDetails { TaxRateId = taxRateId },
            [size.Id.Value],
            [
                new ProductVariantInput
                {
                    Code = "CAM-M",
                    Barcode = mediumBarcode,
                    OptionValueIds = [medium],
                    GalleryFileId = mediumHasOwnImage ? mediumFileId : null,
                    Pricing = new ProductPricing
                    {
                        Prices = new Dictionary<string, decimal> { ["COP"] = 30_000m },
                        Scales = [new PriceScaleInput(1, 99, 10m, PriceScaleRestriction.PackagingUnit, null)],
                        PackagingUnits = [12],
                    },
                },
                new ProductVariantInput
                {
                    Code = "CAM-L",
                    Barcode = largeBarcode,
                    IsActive = largeIsActive,
                    OptionValueIds = [large],
                    Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 32_000m } },
                },
            ],
            [mainFileId, mediumFileId],
            at);

        return new VariantWorld(product, size, mainFileId, mediumFileId);
    }

    private ProductVariant VariantWith(string value)
    {
        var valueId = Size.Values.Single(candidate => candidate.Value == value).Id;
        return Product.Variants.Single(variant => variant.OptionValueIds.Contains(valueId));
    }
}
