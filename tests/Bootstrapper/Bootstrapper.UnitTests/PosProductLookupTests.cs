using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// La portada que el POS le muestra al cajero: sólo la del tenant y sólo si el archivo ya terminó su
/// ciclo de subida (preflight F-16). Una en cuarentena, pendiente o ajena sale como null.
/// </summary>
public sealed class PosProductLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnAvailableImageOfTheTenantGivesItsUrl()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = LookupFor(fileId, new ProductImageRef(fileId, TenantId, "image/png", true, "https://cdn.example.co/a.png"), out var productId);

        var found = await lookup.FindManyAsync(TenantId, [productId], TestContext.Current.CancellationToken);

        Assert.Equal("https://cdn.example.co/a.png", found[productId].ImageUrl);
    }

    [Fact]
    public async Task AQuarantinedOrUnavailableImageGivesNoUrl()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = LookupFor(fileId, new ProductImageRef(fileId, TenantId, "image/png", false, "https://cdn.example.co/a.png"), out var productId);

        var found = await lookup.FindManyAsync(TenantId, [productId], TestContext.Current.CancellationToken);

        Assert.Null(found[productId].ImageUrl);
    }

    [Fact]
    public async Task AnImageOfAnotherTenantGivesNoUrl()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = LookupFor(fileId, new ProductImageRef(fileId, Guid.CreateVersion7(), "image/png", true, "https://cdn.example.co/a.png"), out var productId);

        var found = await lookup.FindManyAsync(TenantId, [productId], TestContext.Current.CancellationToken);

        Assert.Null(found[productId].ImageUrl);
    }

    [Fact]
    public async Task AProductWithoutImageGivesNoUrlAndNoImageLookup()
    {
        var product = NewProduct(imageFileId: null);
        var images = new StubImageLookup([]);
        var lookup = new PosProductLookup(new StubProductRepository(product), new StubTaxRateRepository(), images);

        var found = await lookup.FindManyAsync(TenantId, [product.Id.Value], TestContext.Current.CancellationToken);

        Assert.Null(found[product.Id.Value].ImageUrl);
        Assert.Equal(0, images.Calls);
    }

    // El POS elige la lista por la moneda de la caja; el adaptador entrega las dos.
    [Fact]
    public async Task AProductCarriesBothPriceLists()
    {
        var product = NewProduct(imageFileId: null, baseUsd: 3.25m);
        var lookup = new PosProductLookup(
            new StubProductRepository(product), new StubTaxRateRepository(), new StubImageLookup([]));

        var found = await lookup.FindManyAsync(TenantId, [product.Id.Value], TestContext.Current.CancellationToken);

        Assert.Equal((11_900m, 3.25m), (found[product.Id.Value].PriceCop, found[product.Id.Value].PriceUsd));
    }

    private static PosProductLookup LookupFor(Guid fileId, ProductImageRef image, out Guid productId)
    {
        var product = NewProduct(fileId);
        productId = product.Id.Value;
        return new PosProductLookup(
            new StubProductRepository(product), new StubTaxRateRepository(), new StubImageLookup([image]));
    }

    private static Product NewProduct(Guid? imageFileId, decimal? baseUsd = null) =>
        Product.Create(
            ProductId.New(), TenantId, "Shampoo 400 ml", $"SH-{Guid.CreateVersion7():N}"[..12],
            new ProductDetails { ImageFileId = imageFileId },
            new ProductPricing
            {
                Prices = baseUsd is { } usd
                    ? new Dictionary<string, decimal> { ["COP"] = 11_900m, ["USD"] = usd }
                    : new Dictionary<string, decimal> { ["COP"] = 11_900m }
            },
            Now);

    private sealed class StubImageLookup(IReadOnlyCollection<ProductImageRef> images) : IProductImageLookup
    {
        public int Calls { get; private set; }

        public Task<ProductImageRef?> FindAsync(Guid fileId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, ProductImageRef>> FindManyAsync(
            IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<Guid, ProductImageRef>>(
                images.Where(image => fileIds.Contains(image.FileId)).ToDictionary(image => image.FileId));
        }
    }

    private sealed class StubTaxRateRepository : ITaxRateRepository
    {
        public Task<IReadOnlyList<TaxRate>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaxRate?> FindAsync(Guid tenantId, TaxRateId taxRateId, CancellationToken cancellationToken) =>
            Task.FromResult<TaxRate?>(null);

        public Task<TaxRate?> FindByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(TaxRate taxRate) => throw new NotSupportedException();

        public void Remove(TaxRate taxRate) => throw new NotSupportedException();
    }

    private sealed class StubProductRepository(Product product) : IProductRepository
    {
        public Task<IReadOnlyList<Product>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Product>>(
                product.TenantId == tenantId && productIds.Contains(product.Id) ? [product] : []);

        public Task<(IReadOnlyList<Product> Items, int Total)> SearchAsync(
            Guid tenantId, string? search, string? name, string? code, bool? isActive, int page, int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Product>> ListForExportAsync(
            Guid tenantId, string? name, string? code, bool? isActive, int skip, int take,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Product>> ListByIdsForUpdateAsync(
            Guid tenantId, IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Product?> FindAsync(Guid tenantId, ProductId productId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Product?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> FindExistingCodesAsync(
            Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(Product product) => throw new NotSupportedException();

        public void AddPriceChanges(IReadOnlyList<ProductPriceChange> changes) => throw new NotSupportedException();

        public Task<bool> AnyWithTaxRateAsync(Guid tenantId, TaxRateId taxRateId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
