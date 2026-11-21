using BuildingBlocks.Domain.Catalog;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// El producto que crea Compras al confirmar una orden vinculada (mayorista fase 3, D-16 a D-18): costo en la moneda del
/// borrador, sin precio de venta, sin origen externo y sin tasa; y la búsqueda de la variante por código exacto recortado.
/// </summary>
public sealed class ExternalProductWriterTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly WriterProductRepository _products = new();

    private ExternalProductWriter Writer() =>
        new(
            _products,
            new WriterTaxRateRepository(),
            new WriterOptionRepository(),
            new WriterUsageChecker(),
            new WriterUnitOfWork(),
            new WriterAuditPublisher(),
            new WriterImageReleasePublisher());

    private static ExternalProductDraft Draft(
        decimal? price,
        decimal? cost = null,
        string? source = null,
        string code = "ABC-1") =>
        new(
            TenantId,
            source,
            "Aceite de oliva",
            IsActive: true,
            TaxRateId: null,
            "COP",
            [],
            [new ExternalVariantDraft("line-1", code, null, [], price, cost)],
            Guid.Empty,
            Now);

    private static Product PricedProduct(string code, bool active)
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Vela",
            ProductDetails.Empty,
            new ProductVariantInput
            {
                Code = code,
                IsActive = active,
                Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 1_000m } },
            },
            [],
            Now);
        return product;
    }

    [Fact]
    public async Task CreateWithCostStoresItInTheDraftCurrency()
    {
        await Writer().CreateAsync(Draft(price: null, cost: 8403.36m), CancellationToken.None);

        var variant = Assert.Single(_products.Added).Variants.Single();
        Assert.Equal(8403.36m, variant.CostIn("COP"));
    }

    [Fact]
    public async Task CreateWithoutPriceLeavesNoPrice()
    {
        await Writer().CreateAsync(Draft(price: null), CancellationToken.None);

        var product = Assert.Single(_products.Added);
        Assert.Empty(product.Variants.Single().Prices);
        Assert.True(product.IsActive);
        Assert.Equal("ABC-1", product.Variants.Single().Code);
    }

    [Fact]
    public async Task CreateWithAPriceStillStoresIt()
    {
        await Writer().CreateAsync(Draft(price: 11_900m, source: "shopify"), CancellationToken.None);

        var product = Assert.Single(_products.Added);
        Assert.Equal(11_900m, product.Variants.Single().PriceIn("COP"));
        Assert.Equal("shopify", product.ExternalSource);
    }

    [Fact]
    public async Task CreateWithANullSourceIsNotExternallyManaged()
    {
        await Writer().CreateAsync(Draft(price: null, source: null), CancellationToken.None);

        Assert.Null(Assert.Single(_products.Added).ExternalSource);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ACostOfZeroIsNotStored(decimal cost)
    {
        await Writer().CreateAsync(Draft(price: null, cost: cost), CancellationToken.None);

        Assert.Null(Assert.Single(_products.Added).Variants.Single().CostIn("COP"));
    }

    [Fact]
    public async Task UpdateWithANullPriceThrows()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().UpdateAsync(
            Guid.CreateVersion7(), Draft(price: null), new Dictionary<string, Guid>(), CancellationToken.None));
    }

    [Fact]
    public async Task FindVariantIdByCodeTrimsAndMatchesInactiveVariants()
    {
        var inactive = PricedProduct("ABC-1", active: false);
        _products.Seed(inactive);

        var found = await Writer().FindVariantIdByCodeAsync(TenantId, " ABC-1 ", CancellationToken.None);
        var lowercase = await Writer().FindVariantIdByCodeAsync(TenantId, "abc-1", CancellationToken.None);
        var otherTenant = await Writer().FindVariantIdByCodeAsync(Guid.CreateVersion7(), "ABC-1", CancellationToken.None);
        var blank = await Writer().FindVariantIdByCodeAsync(TenantId, "   ", CancellationToken.None);

        Assert.Equal(inactive.Variants.Single().Id.Value, found);
        Assert.Null(lowercase);
        Assert.Null(otherTenant);
        Assert.Null(blank);
    }

    [Fact]
    public async Task CreatedProductHasNoTaxRate()
    {
        await Writer().CreateAsync(Draft(price: null), CancellationToken.None);

        Assert.Null(Assert.Single(_products.Added).TaxRateId);
    }
}

// Dobles a mano, sin librería de mocking, como el resto del repositorio. Lo que el escritor no usa en estas pruebas lanza.
internal sealed class WriterProductRepository : IProductRepository
{
    private readonly List<Product> _seeded = [];

    public List<Product> Added { get; } = [];

    public void Seed(Product product) => _seeded.Add(product);

    public Task<Product?> FindVariantByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken) =>
        Task.FromResult(_seeded.Concat(Added).FirstOrDefault(product => product.TenantId == tenantId
            && product.Variants.Any(variant => string.Equals(variant.Code, code, StringComparison.Ordinal))));

    public Task<IReadOnlySet<string>> FindExistingCodesAsync(
        Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(_seeded.Concat(Added)
            .Where(product => product.TenantId == tenantId)
            .SelectMany(product => product.Variants)
            .Select(variant => variant.Code)
            .Where(codes.Contains)
            .ToHashSet(StringComparer.Ordinal));

    public Task<IReadOnlySet<string>> FindExistingBarcodesAsync(
        Guid tenantId, IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

    public Task<Product?> FindAsync(Guid tenantId, ProductId productId, CancellationToken cancellationToken) =>
        Task.FromResult(_seeded.Concat(Added).FirstOrDefault(product => product.TenantId == tenantId && product.Id == productId));

    public void Add(Product product) => Added.Add(product);

    public Task<Product?> FindVariantByBarcodeAsync(Guid tenantId, string barcode, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Product>> FindVariantsByIdsAsync(
        Guid tenantId, IReadOnlyCollection<VariantId> variantIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<(IReadOnlyList<Product> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, string? name, string? code, bool? isActive, IReadOnlyCollection<Guid>? categoryIds, int page, int pageSize,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<(IReadOnlyList<Product> Items, int Total)> SearchActiveFilteredAsync(
        Guid tenantId, string? search, string? pricedInCurrency, IReadOnlyCollection<Guid>? categoryIds,
        IReadOnlyCollection<Guid>? excludedCategoryIds, IReadOnlyCollection<Guid>? excludedProductIds, bool orderByName, int page,
        int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<ProductBasics>> FindBasicsByIdsAsync(
        Guid tenantId, IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Product>> ListForExportAsync(
        Guid tenantId, string? search, string? name, string? code, bool? isActive, ProductKind? kind, int skip, int take,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Product>> FindVariantsByIdsForUpdateAsync(
        Guid tenantId, IReadOnlyCollection<VariantId> variantIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<SellableVariantSummary>> ListActiveVariantsAsync(
        Guid tenantId, IReadOnlyCollection<ProductId> productIds, bool includeCosts, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<(IReadOnlyList<ProductSummary> Items, int Total)> SearchSummariesAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        bool? visibleInStore,
        BuildingBlocks.Domain.Catalog.ProductKind? kind,
        IReadOnlyCollection<Guid>? categoryIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Product>> FindManyForUpdateAsync(
        Guid tenantId, IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<(IReadOnlyList<Product> Items, int Total)> SearchStoreVisibleAsync(
        Guid tenantId, string currency, string? search, IReadOnlyCollection<Guid>? categoryIds, int page, int pageSize,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<ProductCategoryId, int>> CountStoreVisibleByCategoryAsync(
        Guid tenantId, string currency, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Product?> FindStoreVisibleAsync(
        Guid tenantId, ProductId id, string currency, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Product>> FindStoreVisibleVariantsAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Remove(Product product) => throw new NotSupportedException();

    public void AddPriceChanges(IReadOnlyList<ProductPriceChange> changes) => throw new NotSupportedException();

    public Task<bool> AnyWithTaxRateAsync(Guid tenantId, TaxRateId taxRateId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class WriterTaxRateRepository : ITaxRateRepository
{
    public Task<IReadOnlyList<TaxRate>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Sin tasa en el borrador el resolver no consulta: si lo hiciera, la prueba lo delataría.
    public Task<TaxRate?> FindAsync(Guid tenantId, TaxRateId taxRateId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TaxRate?> FindByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(TaxRate taxRate) => throw new NotSupportedException();

    public void Remove(TaxRate taxRate) => throw new NotSupportedException();
}

internal sealed class WriterOptionRepository : IOptionDefinitionRepository
{
    public Task<IReadOnlyList<OptionDefinition>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OptionDefinition>>([]);

    public Task<OptionDefinition?> FindAsync(Guid tenantId, OptionDefinitionId optionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<OptionDefinition>> ListByIdsAsync(
        Guid tenantId, IReadOnlyCollection<OptionDefinitionId> optionIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OptionDefinition>>([]);

    public Task<OptionUsage> GetUsageAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(OptionDefinition definition) => throw new NotSupportedException();

    public void Remove(OptionDefinition definition) => throw new NotSupportedException();
}

internal sealed class WriterUsageChecker : IVariantUsageChecker
{
    public Task<IReadOnlySet<Guid>> FindUsedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}

internal sealed class WriterUnitOfWork : ICatalogUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(1);

    public Task<ICatalogTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class WriterAuditPublisher : ICatalogAuditPublisher
{
    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)
    {
    }
}

internal sealed class WriterImageReleasePublisher : IProductImageReleasePublisher
{
    public void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt) =>
        throw new NotSupportedException();
}
