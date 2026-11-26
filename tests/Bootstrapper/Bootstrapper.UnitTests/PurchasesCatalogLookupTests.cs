using Modules.Catalog.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Catalog → Purchases. El nombre de la línea se congela en <c>name varchar(300)</c>, y el nombre visible de una variante
/// («{producto} – {v1} / {v2} / {v3}») llega a 389 caracteres: se recorta aquí, en el único camino que usan la vista previa,
/// el alta y la edición, para que los tres respondan lo mismo.
/// </summary>
public sealed class PurchasesCatalogLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Letters = ["A", "B", "C"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALongVariantNameIsFittedTo300AndIsTheSameOnEveryCall()
    {
        var options = Letters
            .Select(letter => OptionDefinition.Create(TenantId, "Opción " + letter, [new string(letter[0], 60)], isActive: true, Now))
            .ToArray();
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            new string('P', 200),
            new ProductDetails(),
            [.. options.Select(option => option.Id.Value)],
            [
                new ProductVariantInput
                {
                    Code = "LARGO-1",
                    OptionValueIds = [.. options.Select(option => option.Values.Single().Id)],
                    Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 1_000m } },
                },
            ],
            [],
            Now);
        var variantId = product.Variants.Single().Id.Value;
        var lookup = new PurchasesCatalogLookup(
            new FakeProductRepository(product), new FakeOptionDefinitionRepository(options), new FakeTaxRateRepository());

        var first = (await lookup.FindVariantsAsync(TenantId, [variantId], Ct))[variantId].Name;
        var second = (await lookup.FindVariantsAsync(TenantId, [variantId], Ct))[variantId].Name;

        Assert.Equal(300, first.Length);
        Assert.EndsWith("…", first);
        Assert.Equal(first, second);
    }
}
