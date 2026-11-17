using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 §7 y (onboarding) §7 H7, plan Tarea 7: el cálculo de la cobertura, sin autorización ni guardado, para el
/// <c>GET …/products/coverage</c> y el BFF. Una variante casa por vínculo o porque su SKU recortado ya es un código de QEP; la
/// medición lleva la hora del reloj.
/// </summary>
public sealed class ShopifyProductCoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 16, 0, 0, TimeSpan.Zero);

    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static readonly ShopifyConnectionRef Connection =
        new(TenantId, Guid.CreateVersion7(), "954889", "mi-tienda.myshopify.com", true, "shpat_coverage");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCalculatorMatchesByLinkOrSku()
    {
        var source = new FakeProductsSource();
        source.VariantPages.Enqueue(new ShopifyVariantKeysPage(
            [new ShopifyVariantKey(1, "LINKED-BUT-OTHER-SKU", "Camiseta"), new ShopifyVariantKey(2, " SKU-QEP ", "Gorra")], "page-2"));
        source.VariantPages.Enqueue(new ShopifyVariantKeysPage(
            [new ShopifyVariantKey(3, null, "Sin SKU"), new ShopifyVariantKey(4, "SKU-NUEVO", "Medias")], null));
        var links = new FakeProductLinks().Link(TenantId, Connection.ConnectionId, 10, 1, Guid.CreateVersion7(), Guid.CreateVersion7());
        var products = new FakeProductLookup().With("SKU-QEP");
        var calculator = new ShopifyProductCoverageCalculator(source, links, products, new FakeClock(Now));

        var coverage = await calculator.MeasureAsync(Connection, Ct);

        Assert.Equal((4, 2, false, Now), (coverage.ShopifyVariants, coverage.Matched, coverage.CatalogEmpty, coverage.MeasuredAt));
        Assert.Equal([new ShopifyCoverageSample(null, "Sin SKU"), new ShopifyCoverageSample("SKU-NUEVO", "Medias")], coverage.MissingSample);
        Assert.False(coverage.IsComplete);
    }

    [Fact]
    public async Task AnEmptyCatalogIsSaidSoAndTheSampleStopsAt20()
    {
        var source = new FakeProductsSource();
        source.VariantPages.Enqueue(new ShopifyVariantKeysPage(
            [.. Enumerable.Range(1, 25).Select(index => new ShopifyVariantKey(index, $"SKU-{index}", $"Producto {index}"))], null));
        var calculator = new ShopifyProductCoverageCalculator(source, new FakeProductLinks(), new FakeProductLookup(), new FakeClock(Now));

        var coverage = await calculator.MeasureAsync(Connection, Ct);

        Assert.True(coverage.CatalogEmpty);
        Assert.Equal(0, coverage.Matched);
        Assert.Equal(ShopifyProductCoverageCalculator.MissingSampleSize, coverage.MissingSample.Count);
    }

    [Fact]
    public async Task AShopifyFailureIsLeftToTheCaller()
    {
        var source = new FakeProductsSource { Failure = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.Unavailable, null) };
        var calculator = new ShopifyProductCoverageCalculator(source, new FakeProductLinks(), new FakeProductLookup(), new FakeClock(Now));

        var error = await Assert.ThrowsAsync<ShopifyProductsSourceException>(() => calculator.MeasureAsync(Connection, Ct));

        Assert.Equal(ShopifyProductsSourceFailure.Unavailable, error.Failure);
    }
}
