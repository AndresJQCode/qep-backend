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
        Assert.Equal(100_000m, item.PreviousValue);
        Assert.Equal(120_000m, item.NewValue);
        Assert.Equal(20_000m, item.Difference);
        // Un precio base es del producto entero: no tiene rango de escala.
        Assert.Null(item.ScaleFromUnit);
        Assert.Null(item.ScaleToUnit);
        Assert.Equal(tenant.OwnerUserId, item.ChangedById);
        Assert.Equal(tenant.OwnerEmail, item.ChangedByName);
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
