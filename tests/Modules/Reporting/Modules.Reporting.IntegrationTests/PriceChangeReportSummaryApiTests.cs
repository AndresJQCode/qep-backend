using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El resumen del reporte de cambios de precio: los agregados que el panel dibuja sin bajarse una
/// sola fila.
///
/// Lo que se prueba aca y no en el handler es la consulta: la direccion del cambio y la serie
/// mensual se resuelven **en la base**, con una expresion que EF tiene que saber traducir. Una
/// prueba unitaria contra un doble no toca ese SQL.
/// </summary>
public sealed class PriceChangeReportSummaryApiTests
{
    /// <summary>The four slices, always, in the order the contract returns them: one PriceBase per
    /// catalogue currency, then the discount.</summary>
    private static readonly (string Field, string? Currency)[] EverySlice =
        [("PriceBase", "COP"), ("PriceBase", "USD"), ("PriceBase", "EUR"), ("ScaleDiscount", null)];

    // Spec 2026-09-17, punto 5: el cambio de precio del 31 de diciembre a las 23:00 de Bogotá —ya
    // enero en UTC— cuenta en la serie de diciembre del tenant.
    [Fact]
    public async Task TheMonthlySeriesGroupsByTheTenantsLocalMonth()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);

        var summary = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?from=2026-12-01&to=2026-12-31",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.ChangeCount);
        var month = Assert.Single(summary.Monthly);
        Assert.Equal((2026, 12, 1), (month.Year, month.Month, month.Count));
    }

    [Fact]
    public async Task SummaryCountsTheChangesTheirDirectionAndTheProductsTouched()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 90_000m);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PriceChangeReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);

        Assert.Equal(2, summary.ChangeCount);
        // Dos cambios sobre **un** producto: el conteo de productos es el denominador que dice
        // que no fueron dos productos distintos.
        Assert.Equal(1, summary.ProductCount);
        Assert.Equal(1, summary.IncreaseCount);
        Assert.Equal(1, summary.DecreaseCount);

        var month = Assert.Single(summary.Monthly);
        Assert.Equal(2, month.Count);

        var product = Assert.Single(summary.ByProduct);
        Assert.Equal(productId, product.ProductId);
        Assert.Equal("Vela de soja", product.ProductName);
        Assert.NotNull(product.ProductCode);
        Assert.Equal(1, product.EntityCount);
        Assert.Equal(2, product.Count);

        // Un producto sin opciones se desglosa en su única variante, que lleva el nombre del
        // producto y el código que el producto muestra.
        var variant = Assert.Single(product.Variants);
        Assert.Equal(("Vela de soja", product.ProductCode, 2), (variant.Name, variant.Code, variant.Count));

        // Sin rango de fechas no hay ventana anterior contra la cual comparar.
        Assert.Null(summary.Previous);
    }

    /// <summary>
    /// Spec 2026-10-20 §3.5: el ranking sigue siendo por producto y cada uno trae adentro el
    /// desglose por variante, con los conteos que suman el del producto. La camiseta (M dos veces,
    /// L una) le gana a la vela (una) y se desglosa en el orden de presentación; la vela, de una
    /// sola variante, conserva su código, y la camiseta, de dos, no tiene uno que decir.
    /// </summary>
    [Fact]
    public async Task SummaryGroupsByProductWithVariantBreakdown()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var shirt = await CreateSizedProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await SetSizedProductPricesAsync(client, tenant.TenantId, shirt, mediumCop: 110_000m, largeCop: 130_000m);
        await SetSizedProductPricesAsync(client, tenant.TenantId, shirt, mediumCop: 120_000m, largeCop: 130_000m);
        var candle = await CreateProductAsync(client, tenant.TenantId, baseCop: 50_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, candle, 60_000m);

        var summary = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary", TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(4, summary.ChangeCount);
        // Tres variantes tocadas en dos productos: el denominador sigue siendo el producto.
        Assert.Equal(2, summary.ProductCount);

        Assert.Equal(2, summary.ByProduct.Count);
        var shirtEntry = summary.ByProduct[0];
        Assert.Equal(shirt.ProductId, shirtEntry.ProductId);
        Assert.Equal("Camiseta", shirtEntry.ProductName);
        Assert.Equal(3, shirtEntry.Count);
        Assert.Null(shirtEntry.ProductCode);
        Assert.Equal(
            [(shirt.Medium.Id, "Camiseta – M", shirt.Medium.Code, 2), (shirt.Large.Id, "Camiseta – L", shirt.Large.Code, 1)],
            shirtEntry.Variants.Select(variant => (variant.VariantId, variant.Name, variant.Code, variant.Count)));
        Assert.Equal(shirtEntry.Count, shirtEntry.Variants.Sum(variant => variant.Count));

        var candleEntry = summary.ByProduct[1];
        Assert.Equal(candle, candleEntry.ProductId);
        Assert.Equal(1, candleEntry.Count);
        var candleVariant = Assert.Single(candleEntry.Variants);
        Assert.Equal(("Vela de soja", candleEntry.ProductCode, 1), (candleVariant.Name, candleVariant.Code, candleVariant.Count));
    }

    /// <summary>El resumen toma el filtro por variante igual que el listado: sin ella el panel y la
    /// tabla de la misma pantalla hablarían de conjuntos distintos. El desglose del producto trae
    /// sólo la variante filtrada.</summary>
    [Fact]
    public async Task SummaryFiltersByVariantLikeTheList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var shirt = await CreateSizedProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await SetSizedProductPricesAsync(client, tenant.TenantId, shirt, mediumCop: 110_000m, largeCop: 130_000m);

        var large = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?variantId={shirt.Large.Id}",
            TestContext.Current.CancellationToken);
        var unknown = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?variantId={Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, large?.ChangeCount);
        Assert.Equal(1, large?.ProductCount);
        var entry = Assert.Single(large!.ByProduct);
        Assert.Equal(shirt.ProductId, entry.ProductId);
        var variant = Assert.Single(entry.Variants);
        Assert.Equal((shirt.Large.Id, "Camiseta – L", 1), (variant.VariantId, variant.Name, variant.Count));
        Assert.Equal(0, unknown?.ChangeCount);
        Assert.Empty(unknown!.ByProduct);
    }

    /// <summary>
    /// Los tres campos vienen siempre, incluso en cero: una pantalla que recibe solo los campos
    /// con datos tendria que conocer el enum del backend para dibujar el que falta.
    /// </summary>
    [Fact]
    public async Task SummaryAlwaysReturnsTheThreeFieldsEvenWithoutChanges()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var summary = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.ChangeCount);
        Assert.Equal(EverySlice, summary.ByField.Select(slice => (slice.Field, slice.Currency)));
        Assert.All(summary.ByField, slice => Assert.Equal(0, slice.Count));
        Assert.Empty(summary.Monthly);
        Assert.Empty(summary.ByProduct);
    }

    /// <summary>El resumen toma exactamente los mismos filtros que el listado: si contara otro
    /// conjunto, el panel y la tabla de la misma pantalla dirian cosas distintas.</summary>
    [Fact]
    public async Task SummaryFiltersByFieldLikeTheList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);

        var basePrice = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?field=PriceBase",
            TestContext.Current.CancellationToken);
        var discount = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?field=ScaleDiscount",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, basePrice?.ChangeCount);
        Assert.Equal(0, discount?.ChangeCount);
    }

    [Fact]
    public async Task SummaryCountsAnEurChangeInItsOwnSlice()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        var client = tenant.Client;
        var productId = await CreateProductAsync(
            client, tenant.TenantId, prices: new Dictionary<string, decimal> { ["COP"] = 100_000m, ["EUR"] = 20m });
        await ChangeProductBaseCopAsync(
            client, tenant.TenantId, productId, 100_000m,
            new Dictionary<string, decimal> { ["COP"] = 100_000m, ["EUR"] = 24m });

        var all = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary", TestContext.Current.CancellationToken);
        var usd = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?currency=USD",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, all?.ChangeCount);
        Assert.Equal(
            [("PriceBase", "COP", 0), ("PriceBase", "USD", 0), ("PriceBase", "EUR", 1), ("ScaleDiscount", null, 0)],
            all!.ByField.Select(slice => (slice.Field, slice.Currency, slice.Count)));
        Assert.Equal(0, usd?.ChangeCount);
    }

    [Fact]
    public async Task SummaryRejectsAnUnknownCurrency()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);

        var response = await tenant.Client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?currency=XYZ",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SummaryRejectsAFieldThatDoesNotExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?field=FinalPrice",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
