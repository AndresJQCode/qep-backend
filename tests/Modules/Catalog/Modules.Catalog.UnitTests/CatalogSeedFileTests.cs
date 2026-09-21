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

    // Las cinco escalas son las mismas para los 19 productos salvo el descuento —los tres
    // KIT KERATINA tienen su propia columna en la hoja "lista usd"— y la unidad de empaque, que
    // el owner dio por SKU el 2026-09-20. Se afirma el diccionario completo para que un SKU
    // con la unidad equivocada no pase escondido detrás de un `Assert.All`.
    [Fact]
    public void ReadsFiveScalesPerProductWithTheirRestrictions()
    {
        var seed = CatalogSeeder.ReadSeedFile();

        var expectedRanges = new[] { (6, 48), (50, 98), (100, 299), (300, 999), (1000, 999999) };
        var keratinaDiscounts = new[] { 20m, 25m, 33.33m, 35m, 37m };
        var defaultDiscounts = new[] { 15m, 20m, 25m, 30m, 35m };
        var packagingUnits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["7703"] = 150,
            ["7702"] = 100,
            ["7701"] = 25,
            ["7101"] = 100,
            ["3001"] = 100,
            ["7901"] = 100,
            ["7957"] = 100,
            ["7210"] = 50,
            ["7007"] = 50,
            ["7008"] = 50,
            ["7009"] = 50,
            ["7299"] = 70,
            ["3005"] = 50,
            ["3012"] = 150,
            ["7416"] = 108,
            ["7278"] = 70,
            ["7214"] = 50,
            ["7077"] = 50,
            ["3095"] = 13,
        };

        Assert.Equal(
            packagingUnits.Keys.Order(StringComparer.Ordinal),
            seed.Products.Select(product => product.Sku).Order(StringComparer.Ordinal));

        foreach (var product in seed.Products)
        {
            Assert.Equal(5, product.Scales.Count);
            Assert.Equal(
                expectedRanges,
                product.Scales.Select(scale => (scale.FromUnit, scale.ToUnit)).ToArray());

            var expectedDiscounts = product.Sku is "7701" or "7702" or "7703"
                ? keratinaDiscounts
                : defaultDiscounts;
            Assert.Equal(expectedDiscounts, product.Scales.Select(scale => scale.Discount).ToArray());

            var first = product.Scales[0];
            Assert.Equal("multiple", first.Restriction);
            Assert.Equal(3, first.Multiple);
            Assert.Null(first.PackagingUnit);
            Assert.True(first.AllowGrouping);

            var second = product.Scales[1];
            Assert.Equal("multiple", second.Restriction);
            Assert.Equal(6, second.Multiple);
            Assert.Null(second.PackagingUnit);
            Assert.False(second.AllowGrouping);

            foreach (var scale in product.Scales.Skip(2))
            {
                Assert.Equal("packaging_unit", scale.Restriction);
                Assert.Null(scale.Multiple);
                Assert.Equal(packagingUnits[product.Sku], scale.PackagingUnit);
                Assert.False(scale.AllowGrouping);
            }
        }
    }
}
