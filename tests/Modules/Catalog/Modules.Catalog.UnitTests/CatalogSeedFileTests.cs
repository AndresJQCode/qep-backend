using Modules.Catalog.Infrastructure.Seed;

namespace Modules.Catalog.UnitTests;

// Contra el recurso embebido real, no contra un JSON de prueba: lo que se verifica es que el
// archivo que se va a sembrar es el correcto y se lee bien. Corre en milisegundos, así que un
// JSON mal formado se detecta sin levantar PostgreSQL.
public sealed class CatalogSeedFileTests
{
    [Fact]
    public void ReadsEveryProductFromTheEmbeddedResource()
    {
        var seed = CatalogSeeder.ReadSeedFile();

        Assert.Equal("IVA 19%", seed.TaxRate.Name);
        Assert.Equal(19, seed.TaxRate.Percentage);
        Assert.Equal(19, seed.Products.Count);
        Assert.Equal(19, seed.Products.Select(product => product.Sku).Distinct().Count());
        Assert.All(seed.Products, product => Assert.False(string.IsNullOrWhiteSpace(product.Name)));
        // Todo producto necesita precio en al menos una moneda: Product.ApplyPricing lo exige
        // incondicionalmente, así que un archivo sin precio revienta recién al sembrar.
        Assert.All(
            seed.Products,
            product => Assert.True(product.PriceCop is not null || product.PriceUsd is not null));

        var bronceador = seed.Products.Single(product => product.Sku == "7416");
        Assert.Equal(35900m, bronceador.PriceCop);
        Assert.Equal(9.97m, bronceador.PriceUsd);
    }
}
