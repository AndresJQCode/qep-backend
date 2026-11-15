using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using static Modules.Catalog.IntegrationTests.CatalogTestDatabase;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Las lecturas de la tienda pública: un solo predicado (producto activo, publicado y con una variante
/// activa con precio en la moneda) para la búsqueda, los conteos por categoría, la ficha y el carrito.
/// </summary>
public sealed class ProductStoreReadsTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-00000000f001");
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StoreSearchSkipsHiddenInactiveAndUnpricedProducts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var visible = Build("Visible", "V-1", ["COP"], visible: true);
        var hidden = Build("Oculto", "H-1", ["COP"], visible: false);
        var inactive = Build("Inactivo", "I-1", ["COP"], visible: true);
        inactive.Deactivate(Now.AddMinutes(1));
        var usdOnly = Build("Solo dolares", "U-1", ["USD"], visible: true);
        await SaveAsync(factory, visible, hidden, inactive, usdOnly);

        var (items, total) = await SearchAsync(factory, "COP");

        Assert.Equal(1, total);
        Assert.Equal(visible.Id, Assert.Single(items).Id);
    }

    [Fact]
    public async Task StoreSearchSkipsAProductWhoseOnlyPricedVariantIsInactive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var talla = OptionDefinition.Create(TenantId, "Talla", ["M", "L"], isActive: true, Now);
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Camiseta",
            ProductDetails.Empty,
            [talla.Id.Value],
            [
                // La activa sólo tiene USD; la que tiene COP está inactiva.
                Variant("C-M", ["USD"], [talla.Values[0].Id], isActive: true),
                Variant("C-L", ["COP"], [talla.Values[1].Id], isActive: false),
            ],
            [],
            Now,
            visibleInStore: true);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<CatalogDbContext>().OptionDefinitions.Add(talla);
            scope.ServiceProvider.GetRequiredService<IProductRepository>().Add(product);
            await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (items, total) = await SearchAsync(factory, "COP");

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task StoreCategoryCountsUseTheSamePredicate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var category = await SaveCategoryAsync(factory);
        var counted = Build("A", "A-1", ["COP"], visible: true, category.Id);
        var hidden = Build("B", "B-1", ["COP"], visible: false, category.Id);
        var unpriced = Build("C", "C-1", ["USD"], visible: true, category.Id);
        await SaveAsync(factory, counted, hidden, unpriced);

        await using var readScope = factory.Services.CreateAsyncScope();
        var counts = await readScope.ServiceProvider.GetRequiredService<IProductRepository>()
            .CountStoreVisibleByCategoryAsync(TenantId, "COP", TestContext.Current.CancellationToken);

        var only = Assert.Single(counts);
        Assert.Equal(category.Id, only.Key);
        Assert.Equal(1, only.Value);
    }

    [Fact]
    public async Task StoreReadsArePerCurrency()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var category = await SaveCategoryAsync(factory);
        var a = Build("A", "A-1", ["COP", "EUR"], visible: true, category.Id);
        var b = Build("B", "B-1", ["COP"], visible: true, category.Id);
        await SaveAsync(factory, a, b);

        var (items, _) = await SearchAsync(factory, "EUR");
        Assert.Equal(a.Id, Assert.Single(items).Id);

        await using var readScope = factory.Services.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IProductRepository>();
        var counts = await repository.CountStoreVisibleByCategoryAsync(
            TenantId, "EUR", TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(counts).Value);
        Assert.Null(await repository.FindStoreVisibleAsync(
            TenantId, b.Id, "EUR", TestContext.Current.CancellationToken));
        Assert.NotNull(await repository.FindStoreVisibleAsync(
            TenantId, b.Id, "COP", TestContext.Current.CancellationToken));

        // El carrito: la variante de B no se vende en EUR, la de A sí.
        var sellable = await repository.FindStoreVisibleVariantsAsync(
            TenantId,
            "EUR",
            [a.Variants.Single().Id.Value, b.Variants.Single().Id.Value],
            TestContext.Current.CancellationToken);
        Assert.Equal(a.Id, Assert.Single(sellable).Id);
    }

    private static Product Build(
        string name, string code, string[] currencies, bool visible, ProductCategoryId? categoryId = null) =>
        Product.Create(
            ProductId.New(),
            TenantId,
            name,
            new ProductDetails { CategoryId = categoryId },
            [],
            [Variant(code, currencies, [], isActive: true)],
            [],
            Now,
            visibleInStore: visible);

    private static ProductVariantInput Variant(
        string code, string[] currencies, IReadOnlyList<Guid> values, bool isActive) =>
        new()
        {
            Code = code,
            OptionValueIds = values,
            IsActive = isActive,
            Pricing = new ProductPricing { Prices = currencies.ToDictionary(currency => currency, _ => 10m) },
        };

    private static async Task<ProductCategory> SaveCategoryAsync(QepApiFactory factory)
    {
        var category = ProductCategory.Create(ProductCategoryId.New(), TenantId, "Velas", null, Now);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProductCategoryRepository>().Add(category);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return category;
    }

    private static async Task SaveAsync(QepApiFactory factory, params Product[] products)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductRepository>();
        foreach (var product in products)
        {
            repository.Add(product);
        }

        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(IReadOnlyList<Product> Items, int Total)> SearchAsync(
        QepApiFactory factory, string currency)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProductRepository>()
            .SearchStoreVisibleAsync(
                TenantId, currency, search: null, categoryIds: null, page: 1, pageSize: 50,
                TestContext.Current.CancellationToken);
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
