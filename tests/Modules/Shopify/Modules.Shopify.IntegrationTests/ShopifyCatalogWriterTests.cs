using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 §5.4: el adaptador que escribe en Catalog lo hace dentro de la transacción abierta de Shopify, con un
/// <c>CatalogDbContext</c> enrolado: producto y vínculos se confirman juntos o no se confirman. Una regla de Catalog vuelve como
/// <see cref="ShopifyCatalogWrite.RejectedCode"/>, no como excepción. Host propio por prueba.
/// </summary>
public sealed class ShopifyCatalogWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyCatalogProduct Product(Guid tenantId, string name = "Camiseta", string code = "CAM-001") =>
        new(
            tenantId,
            name,
            true,
            null,
            "COP",
            [],
            [new ShopifyCatalogVariant(4455L, code, null, [], 50_000m)],
            Guid.Empty,
            Now);

    [Fact]
    public async Task WithoutAnOpenShopifyTransactionItThrows()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IShopifyCatalogWriter>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CreateAsync(Product(Guid.CreateVersion7()), Ct));
    }

    [Fact]
    public async Task ARolledBackShopifyTransactionLeavesNoProduct()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();

        ShopifyCatalogWrite write;
        using (var scope = factory.Services.CreateScope())
        {
            await using var transaction = await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().BeginTransactionAsync(Ct);
            write = await scope.ServiceProvider.GetRequiredService<IShopifyCatalogWriter>().CreateAsync(Product(tenantId), Ct);

            Assert.Null(write.RejectedCode);
            Assert.NotEqual(Guid.Empty, write.ProductId);
            Assert.Equal([4455L], write.VariantIds.Keys);
            // Dentro de la transacción el producto se ve; disponerla sin commit lo deshace.
        }

        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM catalog.products WHERE id = @id", ("id", write.ProductId)));
    }

    [Fact]
    public async Task ACommittedShopifyTransactionKeepsTheProduct()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();

        ShopifyCatalogWrite write;
        using (var scope = factory.Services.CreateScope())
        {
            await using var transaction = await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().BeginTransactionAsync(Ct);
            write = await scope.ServiceProvider.GetRequiredService<IShopifyCatalogWriter>().CreateAsync(Product(tenantId), Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal("shopify", await ScalarAsync<string>(
            connectionString, "SELECT external_source FROM catalog.products WHERE id = @id", ("id", write.ProductId)));
    }

    [Fact]
    public async Task ACatalogRuleComesBackAsRejectedCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();
        await using var transaction = await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().BeginTransactionAsync(Ct);

        var write = await scope.ServiceProvider.GetRequiredService<IShopifyCatalogWriter>()
            .CreateAsync(Product(Guid.CreateVersion7(), name: new string('x', 201)), Ct);

        Assert.Equal("catalog.product.name_too_long", write.RejectedCode);
        Assert.Equal(Guid.Empty, write.ProductId);
    }

    [Fact]
    public async Task DefaultTaxRatesNeedNoTransaction()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();

        var rates = await scope.ServiceProvider.GetRequiredService<IShopifyCatalogWriter>().FindDefaultTaxRatesAsync(Guid.CreateVersion7(), Ct);

        Assert.Equal(new ShopifyDefaultTaxRates(null, null), rates);
    }
}
