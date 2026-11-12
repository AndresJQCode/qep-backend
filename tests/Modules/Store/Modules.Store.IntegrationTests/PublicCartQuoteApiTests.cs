using System.Net;
using System.Text.Json.Nodes;
using static Modules.Store.IntegrationTests.PublicStoreFixture;

namespace Modules.Store.IntegrationTests;

public sealed class PublicCartQuoteApiTests
{
    private static object Cart(string currency, params (Guid VariantId, int Quantity)[] lines) =>
        new { currency, lines = lines.Select(line => new { variantId = line.VariantId, quantity = line.Quantity }).ToArray() };

    // Contrato E: cada no disponible es { variantId, reason: "NotAvailable" }, no un id suelto.
    private static Guid[] Ids(JsonNode? node) =>
        node!.AsArray().Select(item =>
        {
            Assert.Equal("NotAvailable", (string)item!["reason"]!);
            return (Guid)item["variantId"]!;
        }).ToArray();

    [Fact]
    public async Task TheQuoteMatchesTheOrderEngineIncludingGrouping()
    {
        await using var fixture = await OpenAsync((_, body) => body["pricingMode"] = "QuantityScales");
        // Dos tallas de un mismo producto: 6 + 6 llegan juntas al tramo de 12 (10 %) porque la escala agrupa.
        var shirt = await fixture.CreateShirtInTwoSizesAsync(new[]
        {
            new { fromUnit = 1, toUnit = 11, discount = 0m, restriction = "multiple", multiple = 1, allowGrouping = false },
            new { fromUnit = 12, toUnit = 999, discount = 10m, restriction = "multiple", multiple = 6, allowGrouping = true },
        });

        var response = await fixture.QuoteAsync(Cart("COP", (shirt.M, 6), (shirt.L, 6)));
        var quote = await PublicStoreFixture.JsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = quote["lines"]!.AsArray();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(10m, (decimal)line!["discountPercentage"]!);
            Assert.True((bool)line["scaleApplied"]!);
        });
        Assert.Empty(quote["unavailable"]!.AsArray());
    }

    [Fact]
    public async Task AStaleLineIsReportedAndTheRestIsPriced()
    {
        await using var fixture = await OpenAsync();
        var (_, sellable) = await fixture.CreateProductAsync("Vendible", "V-1", Prices(10_000m));
        var (_, hidden) = await fixture.CreateProductAsync("Oculto", "H-1", Prices(10_000m), visible: false);

        var quote = await PublicStoreFixture.JsonAsync(await fixture.QuoteAsync(Cart("COP", (sellable, 2), (hidden, 1))));

        Assert.Equal([hidden], Ids(quote["unavailable"]));
        var line = Assert.Single(quote["lines"]!.AsArray());
        Assert.Equal(sellable, (Guid)line!["variantId"]!);
        // El total sólo cuenta la línea vendible (el precio de lista lleva el IVA adentro).
        Assert.Equal(20_000m, (decimal)quote["total"]!);
    }

    [Fact]
    public async Task RepeatedVariantsAreMergedIntoOneLine()
    {
        await using var fixture = await OpenAsync();
        var (_, variant) = await fixture.CreateProductAsync("Polo", "P-1", Prices(10_000m));

        var quote = await PublicStoreFixture.JsonAsync(await fixture.QuoteAsync(Cart("COP", (variant, 2), (variant, 3))));

        var line = Assert.Single(quote["lines"]!.AsArray());
        Assert.Equal(5, (int)line!["quantity"]!);
    }

    [Fact]
    public async Task MoreThan100LinesIs422()
    {
        await using var fixture = await OpenAsync();
        var lines = Enumerable.Range(0, 101).Select(_ => (Guid.CreateVersion7(), 1)).ToArray();

        var response = await fixture.QuoteAsync(Cart("COP", lines));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AQuoteInTheSecondCurrencyUsesItsPrices()
    {
        await using var fixture = await OpenAsync(TwoCurrencies);
        var (_, variant) = await fixture.CreateProductAsync("Polo", "P-1", Prices(100_000m, 25m));

        var quote = await PublicStoreFixture.JsonAsync(await fixture.QuoteAsync(Cart("EUR", (variant, 2))));

        Assert.Equal("EUR", (string)quote["currency"]!);
        var line = Assert.Single(quote["lines"]!.AsArray());
        Assert.Equal(25m, (decimal)line!["unitPrice"]!);
        Assert.Equal(50m, (decimal)quote["total"]!);
    }

    [Fact]
    public async Task ALineWithoutAPriceInTheNewCurrencyIsUnavailable()
    {
        await using var fixture = await OpenAsync(TwoCurrencies);
        var (_, both) = await fixture.CreateProductAsync("A", "A-1", Prices(100_000m, 25m));
        var (_, copOnly) = await fixture.CreateProductAsync("B", "B-1", Prices(40_000m));

        var quote = await PublicStoreFixture.JsonAsync(await fixture.QuoteAsync(Cart("EUR", (both, 1), (copOnly, 1))));

        Assert.Equal([copOnly], Ids(quote["unavailable"]));
        Assert.Equal(both, (Guid)Assert.Single(quote["lines"]!.AsArray())!["variantId"]!);
        Assert.Equal(25m, (decimal)quote["total"]!);
    }

    [Fact]
    public async Task AnUnavailableLineCarriesTheNameOfItsVariant()
    {
        await using var fixture = await OpenAsync(TwoCurrencies);
        var (_, hidden) = await fixture.CreateProductAsync("Oculto", "H-1", Prices(10_000m, 3m), visible: false);
        var (_, copOnly) = await fixture.CreateProductAsync("Gorra clásica", "G-1", Prices(40_000m));

        var hiddenQuote = await PublicStoreFixture.JsonAsync(await fixture.QuoteAsync(Cart("COP", (hidden, 1))));
        var currencyQuote = await PublicStoreFixture.JsonAsync(await fixture.QuoteAsync(Cart("EUR", (copOnly, 1))));

        Assert.Equal("Oculto", (string)Assert.Single(hiddenQuote["unavailable"]!.AsArray())!["name"]!);
        var entry = Assert.Single(currencyQuote["unavailable"]!.AsArray())!;
        Assert.Equal("Gorra clásica", (string)entry["name"]!);
        // Sólo variantId, reason y name: ni precio ni existencias.
        Assert.Equal(["name", "reason", "variantId"], entry.AsObject().Select(pair => pair.Key).Order().ToArray());
    }

    [Fact]
    public async Task AnUnavailableLineForAMissingVariantHasANullName()
    {
        await using var fixture = await OpenAsync();
        var missing = Guid.CreateVersion7();

        var quote = await PublicStoreFixture.JsonAsync(await fixture.QuoteAsync(Cart("COP", (missing, 1))));

        var entry = Assert.Single(quote["unavailable"]!.AsArray())!;
        Assert.Equal(missing, (Guid)entry["variantId"]!);
        Assert.Equal("NotAvailable", (string)entry["reason"]!);
        Assert.True(entry.AsObject().ContainsKey("name"));
        Assert.Null(entry["name"]);
    }

    [Fact]
    public async Task AnUnavailableLineForAVariantOfAnotherTenantHasANullName()
    {
        await using var fixture = await OpenAsync();
        await StoreApiHarness.SeedTenantAsync(fixture.Settings.Factory, StoreApiHarness.OtherTenantId);
        var other = fixture.Settings.ClientFor(StoreApiHarness.OtherTenantId, "catalog.product.manage", "catalog.product.read");
        var otherTenant = Guid.Parse(StoreApiHarness.OtherTenantId);
        var otherProduct = await StoreApiHarness.CreateProductAsync(other, otherTenant, "AJENO-1", "Producto ajeno", Prices(10_000m));
        var detail = await JsonAsync(await other.GetAsync(
            $"/api/v1/tenants/{otherTenant}/catalog/products/{otherProduct}", TestContext.Current.CancellationToken));
        var foreignVariant = (Guid)detail["variants"]![0]!["id"]!;

        var quote = await JsonAsync(await fixture.QuoteAsync(Cart("COP", (foreignVariant, 1))));

        var entry = Assert.Single(quote["unavailable"]!.AsArray())!;
        Assert.Equal(foreignVariant, (Guid)entry["variantId"]!);
        Assert.Null(entry["name"]);
    }

    [Fact]
    public async Task AnUnavailableLineForAVariantThatWasDeletedHasANullName()
    {
        await using var fixture = await OpenAsync();
        var (product, variant) = await fixture.CreateProductAsync("Efímero", "E-1", Prices(10_000m));
        await fixture.ExecuteAsync("DELETE FROM catalog.products WHERE id = @id", product);

        var quote = await JsonAsync(await fixture.QuoteAsync(Cart("COP", (variant, 1))));

        var entry = Assert.Single(quote["unavailable"]!.AsArray())!;
        Assert.Equal(variant, (Guid)entry["variantId"]!);
        Assert.Null(entry["name"]);
    }

    [Fact]
    public async Task AnUnavailableVariantWithOptionValuesUsesTheNameOfAnAvailableLine()
    {
        await using var fixture = await OpenAsync(TwoCurrencies);
        var shirt = await fixture.CreateShirtInTwoSizesAsync(NoDiscountScales());

        // En COP la talla M se vende y da su nombre de línea; en EUR (sin precio) queda fuera y debe llamarse igual.
        var sold = await JsonAsync(await fixture.QuoteAsync(Cart("COP", (shirt.M, 1))));
        var skipped = await JsonAsync(await fixture.QuoteAsync(Cart("EUR", (shirt.M, 1))));

        var lineName = (string)Assert.Single(sold["lines"]!.AsArray())!["name"]!;
        var entry = Assert.Single(skipped["unavailable"]!.AsArray())!;
        Assert.Contains("M", lineName);
        Assert.NotEqual("Camiseta polo", lineName);
        Assert.Equal(lineName, (string)entry["name"]!);
    }

    [Fact]
    public async Task AQuoteInACurrencyOutsideTheStoreIs422()
    {
        await using var fixture = await OpenAsync();
        var (_, variant) = await fixture.CreateProductAsync(
            "Polo", "P-1", new Dictionary<string, decimal> { ["COP"] = 10_000m, ["USD"] = 3m });

        var response = await fixture.QuoteAsync(Cart("USD", (variant, 1)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.public.currency_invalid", (string)(await PublicStoreFixture.JsonAsync(response))["code"]!);
    }
}
