using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Reporte 3: cambios de precio del **catalogo de productos**.
///
/// Crear un producto no deja historico —no hay un "antes" del que se haya cambiado nada—, asi
/// que toda siembra de este reporte es un alta seguida de un PUT.
/// </summary>
public sealed class PriceChangeReportApiTests
{
    [Fact]
    public async Task ListReturnsTheChangeWithTheProductAuthorAndDifference()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);

        var item = Assert.Single(page.Items, row => row.Field == "PriceBase");
        Assert.Equal("COP", item.Currency);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal("Vela de soja", item.ProductName);
        Assert.NotEmpty(item.ProductCode);
        // Spec 2026-10-20 §3.5: el cambio es de una variante. En un producto sin opciones la
        // variante por defecto lleva el nombre del producto, y la fila trae su id.
        Assert.NotEqual(Guid.Empty, item.VariantId);
        Assert.Equal("Vela de soja", item.VariantName);
        Assert.Equal(100_000m, item.PreviousValue);
        Assert.Equal(120_000m, item.NewValue);
        Assert.Equal(20_000m, item.Difference);
        // Un precio base es del producto entero: no tiene rango de escala.
        Assert.Null(item.ScaleFromUnit);
        Assert.Null(item.ScaleToUnit);
        Assert.Equal(tenant.OwnerUserId, item.ChangedById);
        Assert.Equal(tenant.OwnerEmail, item.ChangedByName);
    }

    /// <summary>
    /// El filtro por variante (spec 2026-10-20 §3.5): una camiseta con M y L cuyos dos precios
    /// cambian deja dos filas, y <c>variantId</c> deja la de esa variante, con su código y su nombre
    /// visible. Combinado con <c>productId</c> es un Y, y un id que no es de nadie da una página
    /// vacía, no un error.
    /// </summary>
    [Fact]
    public async Task ReportFiltersByVariant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var shirt = await CreateSizedProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await SetSizedProductPricesAsync(client, tenant.TenantId, shirt, mediumCop: 110_000m, largeCop: 130_000m);
        var otherProduct = await CreateProductAsync(client, tenant.TenantId, baseCop: 50_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, otherProduct, 60_000m);
        var url = $"{ReportsUrl(tenant.TenantId)}/price-changes";

        var all = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            url, TestContext.Current.CancellationToken);
        var medium = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{url}?variantId={shirt.Medium.Id}", TestContext.Current.CancellationToken);
        var largeOfItsProduct = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{url}?productId={shirt.ProductId}&variantId={shirt.Large.Id}",
            TestContext.Current.CancellationToken);
        var largeOfAnotherProduct = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{url}?productId={otherProduct}&variantId={shirt.Large.Id}",
            TestContext.Current.CancellationToken);
        var unknown = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{url}?variantId={Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        // Dos variantes de la camiseta y la única del otro producto: tres cambios.
        Assert.Equal(3, all?.Total);
        Assert.Equal(
            ["Camiseta – L", "Camiseta – M", "Vela de soja"],
            all!.Items.Select(row => row.VariantName).Order(StringComparer.Ordinal));

        var mediumRow = Assert.Single(medium!.Items);
        Assert.Equal(1, medium.Total);
        Assert.Equal(shirt.Medium.Id, mediumRow.VariantId);
        Assert.Equal(shirt.ProductId, mediumRow.ProductId);
        Assert.Equal("Camiseta – M", mediumRow.VariantName);
        Assert.Equal("Camiseta", mediumRow.ProductName);
        Assert.Equal(shirt.Medium.Code, mediumRow.ProductCode);
        Assert.Equal((100_000m, 110_000m), (mediumRow.PreviousValue, mediumRow.NewValue));

        var largeRow = Assert.Single(largeOfItsProduct!.Items);
        Assert.Equal(shirt.Large.Id, largeRow.VariantId);
        Assert.Equal("Camiseta – L", largeRow.VariantName);
        Assert.Equal((100_000m, 130_000m), (largeRow.PreviousValue, largeRow.NewValue));

        Assert.Equal(0, largeOfAnotherProduct?.Total);
        Assert.Equal(0, unknown?.Total);
    }

    [Fact]
    public async Task ListFiltersByField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);

        var basePrice = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?field=PriceBase",
            TestContext.Current.CancellationToken);
        var discount = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?field=ScaleDiscount",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, basePrice?.Total);
        Assert.Equal(0, discount?.Total);
    }

    [Theory]
    [InlineData("PriceBaseUsd")]
    [InlineData("PriceBaseCop")]
    public async Task ListRejectsTheRetiredPerCurrencyFields(string field)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);

        var response = await tenant.Client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?field={field}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Field", out _));
    }

    [Fact]
    public async Task FilteringByCurrencyReturnsOnlyThatCurrencysBasePriceRows()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        var client = tenant.Client;
        var productId = await CreateProductAsync(
            client, tenant.TenantId, prices: new Dictionary<string, decimal> { ["COP"] = 100_000m, ["USD"] = 25m });
        await ChangeProductBaseCopAsync(
            client, tenant.TenantId, productId, 110_000m,
            new Dictionary<string, decimal> { ["COP"] = 110_000m, ["USD"] = 30m });

        var page = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?field=PriceBase&currency=usd",
            TestContext.Current.CancellationToken);

        var row = Assert.Single(page!.Items);
        Assert.Equal(("PriceBase", "USD", 25m, 30m), (row.Field, row.Currency, row.PreviousValue, row.NewValue));
    }

    /// <summary>Regression: the author lookup used to map the (field, currency) pair back to a
    /// per-currency enum and threw for any currency it did not know, so one EUR change turned the
    /// unfiltered list into a 500.</summary>
    [Fact]
    public async Task AnEurPriceChangeAppearsInTheUnfilteredList()
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

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            TestContext.Current.CancellationToken);
        var row = Assert.Single(page!.Items);
        Assert.Equal(("PriceBase", "EUR", 20m, 24m), (row.Field, row.Currency, row.PreviousValue, row.NewValue));

        var eurOnly = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?currency=EUR",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, eurOnly?.Total);
    }

    [Fact]
    public async Task AnUnknownCurrencyFilterIsA422OnTheCurrencyField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);

        var response = await tenant.Client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?currency=XYZ", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Currency", out _));
    }

    [Fact]
    public async Task ListRejectsAFieldThatDoesNotExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?field=FinalPrice",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
    }

    [Fact]
    public async Task ListReturnsAnEmptyPageWhenNoPriceEverChanged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        await CreateProductAsync(client, tenant.TenantId);

        var page = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task ListRejectsAnotherTenantsReport()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(Guid.CreateVersion7())}/price-changes",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListRejectsACallerWithoutTheReportingPermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, SeedOnlyPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
