using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Reporte 4: padron de clientes (Clientes CUC).
///
/// Sin columna de lista de precios: esa relacion se retiro del sistema el 2026-08-23 y el dato
/// ya no existe. Ver el contrato.
/// </summary>
public sealed class CustomerReportApiTests
{
    [Fact]
    public async Task ListReturnsTheCustomerWithItsClassificationAndGeography()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/customers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);

        var item = Assert.Single(page.Items);
        Assert.Equal(customer.Id, item.CustomerId);
        Assert.Equal(customer.Cuc, item.Cuc);
        Assert.Equal("Verde Esencial S.A.S.", item.Name);
        // El nombre del enum, no el valor en mayusculas que usan los endpoints de customers.
        Assert.Equal("Nit", item.IdentificationType);
        Assert.Equal(customer.ClassificationId, item.ClassificationId);
        Assert.False(string.IsNullOrWhiteSpace(item.ClassificationName));
        Assert.Equal(customer.CityId, item.CityId);
        Assert.False(string.IsNullOrWhiteSpace(item.CityName));
        // El departamento no esta en Customer: se resuelve por CityId contra Geography.
        Assert.NotNull(item.DepartmentId);
        Assert.False(string.IsNullOrWhiteSpace(item.DepartmentName));
        Assert.True(item.IsActive);
    }

    [Fact]
    public async Task ListFiltersByActiveState()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var deactivated = await client.PostAsync(
            $"/api/v1/tenants/{tenant.TenantId}/customers/{customer.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        deactivated.EnsureSuccessStatusCode();

        var active = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers?isActive=true",
            TestContext.Current.CancellationToken);
        var inactive = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers?isActive=false",
            TestContext.Current.CancellationToken);
        var both = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers", TestContext.Current.CancellationToken);

        Assert.Equal(0, active?.Total);
        Assert.Equal(1, inactive?.Total);
        // Sin el filtro vienen los dos estados, que es lo que el contrato dice de isActive nulo.
        Assert.Equal(1, both?.Total);
    }

    [Fact]
    public async Task ListFiltersByClassification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);

        var matching = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers?classificationId={customer.ClassificationId}",
            TestContext.Current.CancellationToken);
        var other = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers?classificationId={Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, matching?.Total);
        Assert.Equal(0, other?.Total);
    }

    [Fact]
    public async Task ListReturnsAnEmptyPageWhenTheTenantHasNoCustomers()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var page = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers", TestContext.Current.CancellationToken);

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
            $"{ReportsUrl(Guid.CreateVersion7())}/customers",
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
            $"{ReportsUrl(tenant.TenantId)}/customers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportReturnsAnExcelFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        await CreateActiveCustomerAsync(client, tenant.TenantId);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/customers/export",
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
            $"{ReportsUrl(tenant.TenantId)}/customers/export",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("reporting.export.empty", problem?.Code);
    }
}
