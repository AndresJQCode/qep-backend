using System.Net;
using System.Net.Http.Json;
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

        var item = Assert.Single(page.Items, row => row.Field == "PriceBaseCop");
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

        var cop = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?field=PriceBaseCop",
            TestContext.Current.CancellationToken);
        var usd = await client.GetFromJsonAsync<ReportPageDto<PriceChangeReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes?field=PriceBaseUsd",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, cop?.Total);
        Assert.Equal(0, usd?.Total);
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

    [Fact]
    public async Task ExportReturnsAnExcelFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/export",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExcelContentType, response.Content.Headers.ContentType?.MediaType);
        var content = await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal([0x50, 0x4B], content[..2]);
    }

    [Fact]
    public async Task ExportWithNoMatchingRowsFails()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/export",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("reporting.export.empty", problem?.Code);
    }
}
