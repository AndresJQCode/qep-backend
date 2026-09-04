using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Reporte 1: ventas convertidas, listado y exportacion.
///
/// La siembra pasa por los endpoints reales de Customers, Catalog y Quotations, no por SQL: el
/// reporte cruza cuatro modulos, y una fila insertada a mano probaria la consulta pero no que la
/// venta que el sistema produce tenga los datos que el reporte espera encontrar.
/// </summary>
public sealed class SalesReportApiTests
{
    [Fact]
    public async Task ListReturnsTheConvertedSaleWithItsQuotationAdvisorAndClient()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        var sale = await ConvertToSaleAsync(client, factory, tenant.TenantId, quotation);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/sales", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ReportPageDto<SalesReportItem>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(50, page.PageSize);

        var item = Assert.Single(page.Items);
        Assert.Equal(sale.Id, item.SaleId);
        Assert.Equal(sale.SaleNumber, item.SaleNumber);
        Assert.Equal(quotation.Id, item.QuotationId);
        Assert.Equal(quotation.QuotationNumber, item.QuotationNumber);
        Assert.Equal("Approved", item.Status);
        Assert.Equal("FullPaymentReceived", item.PaymentStatus);
        Assert.Equal(customer.Id, item.ClientId);
        Assert.Equal(customer.Cuc, item.ClientCuc);
        Assert.Equal("Verde Esencial S.A.S.", item.ClientName);
        // El sistema no guarda nombre de persona: advisorName es el email, que es el unico
        // identificador legible que existe. Ver la seccion del contrato al respecto.
        Assert.Equal(tenant.OwnerEmail, item.AdvisorName);
        Assert.Equal(quotation.Subtotal, item.Subtotal);
        Assert.Equal(quotation.TaxAmount, item.TaxAmount);
        Assert.Equal(quotation.Total, item.Total);
    }

    [Fact]
    public async Task ListReturnsAnEmptyPageWhenTheTenantHasNoSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/sales", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ReportPageDto<SalesReportItem>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    /// <summary>403 y no 404: un 404 confirmaria que el tenant de la ruta existe.</summary>
    [Fact]
    public async Task ListRejectsAnotherTenantsReport()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var otherTenantId = Guid.CreateVersion7();

        var response = await client.GetAsync(
            $"{ReportsUrl(otherTenantId)}/sales", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("authorization.denied", problem?.Code);
    }

    /// <summary>El 403 tiene que venir del permiso de reporting que falta, no de otro: por eso el
    /// cliente lleva todos los de siembra y ninguno de los cuatro de Reporting.</summary>
    [Fact]
    public async Task ListRejectsACallerWithoutTheReportingPermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, SeedOnlyPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/sales", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportReturnsAnExcelFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToSaleAsync(client, factory, tenant.TenantId, quotation);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/sales/export", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExcelContentType, response.Content.Headers.ContentType?.MediaType);
        var content = await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken);
        // "PK": todo .xlsx es un zip. Que las columnas sean las del contrato lo verifica
        // ReportExcelBuilderTests, que abre el workbook.
        Assert.True(content.Length > 0);
        Assert.Equal([0x50, 0x4B], content[..2]);
        Assert.Contains(
            "reporte-ventas-",
            response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName
                ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportWithNoMatchingRowsFails()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/sales/export", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("reporting.export.empty", problem?.Code);
    }

    [Fact]
    public async Task ListRejectsAPaymentStatusThatDoesNotExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/sales?paymentStatus=Refunded",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
    }

    [Fact]
    public async Task ListFiltersByPaymentStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToSaleAsync(client, factory, tenant.TenantId, quotation);

        var matching = await client.GetFromJsonAsync<ReportPageDto<SalesReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/sales?paymentStatus=FullPaymentReceived",
            TestContext.Current.CancellationToken);
        var other = await client.GetFromJsonAsync<ReportPageDto<SalesReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/sales?paymentStatus=PaymentPending",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, matching?.Total);
        Assert.Equal(0, other?.Total);
    }
}
