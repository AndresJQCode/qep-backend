using Modules.Catalog.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Los porcentajes de IVA de un lote de productos salen de una sola lectura de las tasas del tenant, no de una por tasa:
/// en producción cada consulta en serie paga la red hasta la base.
/// </summary>
public sealed class CatalogVariantReadsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThePercentagesOfThreeRatesAreOneListNotThreeFinds()
    {
        var rates = new[] { Rate(19), Rate(5), Rate(0) };
        var repository = new FakeTaxRateRepository(rates);
        var products = rates.Select(rate => ProductTaxedWith(rate.Id)).Concat([ProductTaxedWith(rates[0].Id)]).ToArray();

        var percentages = await CatalogVariantReads.LoadTaxPercentagesAsync(repository, TenantId, products, CancellationToken.None);

        Assert.Equal(3, percentages.Count);
        Assert.Equal(19, percentages[rates[0].Id]);
        Assert.Equal(1, repository.ListCalls);
        Assert.Equal(0, repository.FindCalls);
    }

    // Review Focus 1: una tasa que ya no existe no aparece, y no sale con 0.
    [Fact]
    public async Task ADeletedRateIsAbsentNotZero()
    {
        var repository = new FakeTaxRateRepository(Rate(19));
        var products = new[] { ProductTaxedWith(TaxRateId.New()) };

        var percentages = await CatalogVariantReads.LoadTaxPercentagesAsync(repository, TenantId, products, CancellationToken.None);

        Assert.Empty(percentages);
    }

    private static TaxRate Rate(int vat) =>
        TaxRate.Create(TaxRateId.New(), TenantId, $"IVA {vat}", vat, "IVA", "CO", null, null, Now);

    private static Product ProductTaxedWith(TaxRateId taxRateId) =>
        Product.Create(
            ProductId.New(), TenantId, "Shampoo 400 ml",
            new ProductDetails { TaxRateId = taxRateId },
            new ProductVariantInput { Code = $"SH-{Guid.NewGuid():N}", Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 11_900m } } },
            [],
            Now);
}
