using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Modules.Store.IntegrationTests.PublicStoreFixture;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Las lecturas públicas de la tienda de punta a punta: resolución del host, DTO sin datos internos, el predicado de visibilidad y de
/// moneda, el árbol de categorías, la ficha con y sin escalas y la caché. Necesitan Docker; se corren una vez en la tanda final.
/// </summary>
public sealed class PublicCatalogApiTests
{
    private static readonly string[] ColombiaAndSpain = ["CO", "ES"];

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(item => (string)item!).ToArray();

    [Fact]
    public async Task AnUnpublishedStoreAndAnUnknownHostAreTheSame404()
    {
        await using var fixture = await OpenAsync(publish: false);

        var unpublished = await fixture.GetAsync("/");
        var unknown = await fixture.GetAsync("/", host: "no-existe.lulocrm.com");

        Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var first = await PublicStoreFixture.JsonAsync(unpublished);
        var second = await PublicStoreFixture.JsonAsync(unknown);
        Assert.Equal("store.public.not_found", (string)first["code"]!);
        // Idéntico salvo el traceId y el instance, que sólo repite la ruta que pidió el mismo que llama: quien sondea hosts no
        // distingue una tienda en preparación de una inexistente.
        foreach (var problem in new[] { first, second })
        {
            problem.AsObject().Remove("traceId");
            problem.AsObject().Remove("instance");
        }

        Assert.Equal(first.ToJsonString(), second.ToJsonString());
    }

    [Fact]
    public async Task WithTheModuleOffThePublishedStoreIs404()
    {
        await using var fixture = await OpenAsync();
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/")).StatusCode);

        // El módulo se apaga después de publicar: la tienda queda guardada y publicada, pero ya no se sirve.
        await using (var scope = fixture.Settings.Factory.Services.CreateAsyncScope())
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext>();
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM tenancy.tenant_modules WHERE tenant_id = {Tenant} AND module_key = {Modules.Tenancy.Domain.TenantModuleKeys.Store.Value}",
                TestContext.Current.CancellationToken);
        }

        var response = await fixture.GetAsync("/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("store.public.not_found", (string)(await PublicStoreFixture.JsonAsync(response))["code"]!);
    }

    [Fact]
    public async Task TheStoreDtoCarriesNoBillingAccountOrTenantId()
    {
        await using var fixture = await OpenAsync();

        var body = await StoreApiHarness.BodyAsync(await fixture.GetAsync("/"));

        Assert.DoesNotContain("billing", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenantId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("companyId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyVisibleActivePricedProductsAreListed()
    {
        await using var fixture = await OpenAsync();
        var (visible, _) = await fixture.CreateProductAsync("Visible", "VIS-1", Prices(10_000m));
        await fixture.CreateProductAsync("Oculto", "HID-1", Prices(10_000m), visible: false);
        // El catálogo no deja crear un producto sin variante activa (catalog.variant.active_required), así que los dos casos se arman
        // por SQL después: la única variante apagada y el producto inactivo.
        var (_, offVariant) = await fixture.CreateProductAsync("Variante apagada", "OFF-1", Prices(10_000m));
        var (inactiveProduct, _) = await fixture.CreateProductAsync("Producto inactivo", "INA-1", Prices(10_000m));
        await fixture.ExecuteAsync("UPDATE catalog.product_variants SET is_active = false WHERE id = @id", offVariant);
        await fixture.ExecuteAsync("UPDATE catalog.products SET is_active = false WHERE id = @id", inactiveProduct);

        var page = await fixture.GetJsonAsync("/products?currency=COP");

        // CAM-001 del fixture no está publicado en la tienda: tampoco sale.
        var item = Assert.Single(page["items"]!.AsArray());
        Assert.Equal(visible, (Guid)item!["id"]!);
        Assert.Equal(1, (int)page["total"]!);
    }

    [Fact]
    public async Task ACategoryIncludesItsDescendants()
    {
        await using var fixture = await OpenAsync();
        var root = await fixture.CreateCategoryAsync("Ropa");
        var child = await fixture.CreateCategoryAsync("Camisetas", root);
        var other = await fixture.CreateCategoryAsync("Hogar");
        var (inChild, _) = await fixture.CreateProductAsync("Polo", "P-1", Prices(10_000m), categoryId: child);
        await fixture.CreateProductAsync("Taza", "T-1", Prices(5_000m), categoryId: other);

        var page = await fixture.GetJsonAsync($"/products?currency=COP&category={root}");
        var categories = await fixture.GetJsonAsync("/categories?currency=COP");

        Assert.Equal(inChild, (Guid)Assert.Single(page["items"]!.AsArray())!["id"]!);
        var counts = categories["items"]!.AsArray().ToDictionary(node => (Guid)node!["id"]!, node => (int)node!["productCount"]!);
        Assert.Equal(1, counts[root]);
        Assert.Equal(1, counts[child]);
        Assert.Equal(1, counts[other]);
    }

    [Fact]
    public async Task TheSearchMatchesTheProductNameOnly()
    {
        await using var fixture = await OpenAsync();
        await fixture.CreateProductAsync("Camiseta roja", "SECRET-CODE", Prices(10_000m), barcode: "7701234567890");

        var byName = await fixture.GetJsonAsync("/products?currency=COP&q=roja");
        var byCode = await fixture.GetJsonAsync("/products?currency=COP&q=SECRET-CODE");
        var byBarcode = await fixture.GetJsonAsync("/products?currency=COP&q=7701234567890");

        Assert.Single(byName["items"]!.AsArray());
        Assert.Empty(byCode["items"]!.AsArray());
        Assert.Empty(byBarcode["items"]!.AsArray());
    }

    [Fact]
    public async Task TheProductDetailHidesCodesAndScalesInRetailMode()
    {
        await using var fixture = await OpenAsync();
        var (productId, _) = await fixture.CreateProductAsync(
            "Polo", "POLO-1", Prices(10_000m), barcode: "7701234567890",
            scales: new[] { new { fromUnit = 1, toUnit = 999, discount = 10m, restriction = "multiple", multiple = 1, allowGrouping = false } });

        var response = await fixture.GetAsync($"/products/{productId}?currency=COP");
        var body = await StoreApiHarness.BodyAsync(response);
        var product = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("\"code\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("barcode", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("POLO-1", body, StringComparison.Ordinal);
        Assert.Empty(product["variants"]![0]!["scales"]!.AsArray());
    }

    [Fact]
    public async Task InScalesModeEachTierCarriesItsFinal()
    {
        await using var fixture = await OpenAsync((_, body) => body["pricingMode"] = "QuantityScales");
        var (productId, _) = await fixture.CreateProductAsync(
            "Polo", "POLO-1", Prices(10_000m),
            scales: new[]
            {
                new { fromUnit = 1, toUnit = 9, discount = 0m, restriction = "multiple", multiple = 1, allowGrouping = false },
                new { fromUnit = 10, toUnit = 999, discount = 12.5m, restriction = "multiple", multiple = 1, allowGrouping = false },
            });

        var product = await fixture.GetJsonAsync($"/products/{productId}?currency=COP");

        var scales = product["variants"]![0]!["scales"]!.AsArray();
        Assert.Equal(2, scales.Count);
        foreach (var scale in scales)
        {
            Assert.Equal(
                Modules.Catalog.Domain.PriceScale.FinalFor(10_000m, (decimal)scale!["discountPercentage"]!),
                (decimal)scale["final"]!);
        }
    }

    [Fact]
    public async Task AHiddenProductDetailIs404()
    {
        await using var fixture = await OpenAsync();
        var (hidden, _) = await fixture.CreateProductAsync("Oculto", "HID-1", Prices(10_000m), visible: false);

        var response = await fixture.GetAsync($"/products/{hidden}?currency=COP");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("store.public.product_not_found", (string)(await PublicStoreFixture.JsonAsync(response))["code"]!);
    }

    [Fact]
    public async Task GetsAreCacheable()
    {
        await using var fixture = await OpenAsync();

        foreach (var path in new[] { "/", "/categories?currency=COP", "/products?currency=COP" })
        {
            var response = await fixture.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("public, max-age=60, stale-while-revalidate=300", response.Headers.CacheControl?.ToString());
        }
    }

    [Fact]
    public async Task TheStoreListsItsCurrenciesAndThePrimary()
    {
        await using var fixture = await OpenAsync(TwoCurrencies);

        var body = await StoreApiHarness.BodyAsync(await fixture.GetAsync("/"));
        var store = JsonNode.Parse(body)!;

        Assert.Equal(["COP", "EUR"], Strings(store["currencies"]));
        Assert.Equal("EUR", (string)store["primaryCurrency"]!);
        Assert.DoesNotContain("billing", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissingCurrencyIs422()
    {
        await using var fixture = await OpenAsync();

        var response = await fixture.GetAsync("/products");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.public.currency_invalid", (string)(await PublicStoreFixture.JsonAsync(response))["code"]!);
    }

    [Fact]
    public async Task ACurrencyInUseButNotInTheStoreIs422()
    {
        await using var fixture = await OpenAsync();
        var (productId, _) = await fixture.CreateProductAsync(
            "Con dólares", "USD-1", new Dictionary<string, decimal> { ["COP"] = 10_000m, ["USD"] = 3m });

        foreach (var path in new[] { "/categories?currency=USD", "/products?currency=USD", $"/products/{productId}?currency=USD" })
        {
            var response = await fixture.GetAsync(path);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("store.public.currency_invalid", (string)(await PublicStoreFixture.JsonAsync(response))["code"]!);
        }
    }

    [Fact]
    public async Task ProductsWithoutAPriceInTheRequestedCurrencyAreExcluded()
    {
        await using var fixture = await OpenAsync(TwoCurrencies);
        var (both, _) = await fixture.CreateProductAsync("A", "A-1", Prices(10_000m, 3m));
        var (copOnly, _) = await fixture.CreateProductAsync("B", "B-1", Prices(20_000m));

        var page = await fixture.GetJsonAsync("/products?currency=EUR");
        var detail = await fixture.GetAsync($"/products/{copOnly}?currency=EUR");

        var item = Assert.Single(page["items"]!.AsArray());
        Assert.Equal(both, (Guid)item!["id"]!);
        Assert.Equal(3m, (decimal)item["priceFrom"]!);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal("store.public.product_not_found", (string)(await PublicStoreFixture.JsonAsync(detail))["code"]!);
    }

    [Fact]
    public async Task CategoryCountsArePerCurrency()
    {
        await using var fixture = await OpenAsync(TwoCurrencies);
        var category = await fixture.CreateCategoryAsync("Ropa");
        await fixture.CreateProductAsync("A", "A-1", Prices(10_000m, 3m), categoryId: category);
        await fixture.CreateProductAsync("B", "B-1", Prices(20_000m), categoryId: category);

        var cop = await fixture.GetJsonAsync("/categories?currency=COP");
        var eur = await fixture.GetJsonAsync("/categories?currency=EUR");

        Assert.Equal(2, (int)Assert.Single(cop["items"]!.AsArray())!["productCount"]!);
        Assert.Equal(1, (int)Assert.Single(eur["items"]!.AsArray())!["productCount"]!);
    }

    [Fact]
    public async Task TheCurrencyIsCaseInsensitive()
    {
        await using var fixture = await OpenAsync(TwoCurrencies);
        await fixture.CreateProductAsync("A", "A-1", Prices(10_000m, 3m));

        var page = await fixture.GetJsonAsync("/products?currency=eur");

        Assert.Equal("EUR", (string)page["currency"]!);
        Assert.Equal(3m, (decimal)Assert.Single(page["items"]!.AsArray())!["priceFrom"]!);
    }

    [Fact]
    public async Task AHugePageNumberIsCorrectedAndAnswersAnEmptyPage()
    {
        await using var fixture = await OpenAsync();
        await fixture.CreateProductAsync("Camisa", "C-1", Prices(10_000m));

        var response = await fixture.GetAsync("/products?currency=COP&page=2147483647&pageSize=60");
        var page = await PublicStoreFixture.JsonAsync(response);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(page["items"]!.AsArray());
        Assert.Equal(1, (int)page["total"]!);
    }

    [Fact]
    public async Task TheStoreCarriesTheTenantRegionalFormat()
    {
        await using var fixture = await OpenAsync();

        var store = await fixture.GetJsonAsync("/");

        // El tenant del harness se crea con America/Bogota y yyyy-MM-dd; el resto sale de su configuración regional.
        var regional = store["regionalFormat"]!;
        Assert.Equal("America/Bogota", (string)regional["timeZone"]!);
        Assert.Equal("yyyy-MM-dd", (string)regional["dateFormat"]!);
        // numberFormat es el patrón del tenant, no una cultura (TenantNumberFormats.cs:9; el default de la columna). Los valores exactos
        // fijan que numberFormat y defaultCurrency no se crucen.
        Assert.Equal(Modules.Tenancy.Domain.TenantNumberFormats.CommaDecimal, (string)regional["numberFormat"]!);
        Assert.Equal("COP", (string)regional["defaultCurrency"]!);
        Assert.Equal("CO", (string)regional["country"]!);
    }

    [Fact]
    public async Task TheStoreCarriesOnlyResolvedColors()
    {
        await using var fixture = await OpenAsync();

        var body = await StoreApiHarness.BodyAsync(await fixture.GetAsync("/"));
        var colors = JsonNode.Parse(body)!["colors"]!.AsObject();

        Assert.True(colors.ContainsKey("primary"));
        Assert.True(colors.ContainsKey("onPrimary"));
        Assert.True(colors.ContainsKey("accentText"));
        Assert.DoesNotContain("lowContrast", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onPrimaryColor", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStoreListsItsSalesCountries()
    {
        await using var fixture = await OpenAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);

        var store = await fixture.GetJsonAsync("/");

        Assert.Equal(["CO", "ES"], Strings(store["salesCountries"]));
    }
}
