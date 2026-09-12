using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>Reporte 2: cotizaciones.</summary>
public sealed class QuotationsReportApiTests
{
    [Fact]
    public async Task ListReturnsTheQuotationWithItsAdvisorAndClient()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/quotations", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ReportPageDto<QuotationsReportItem>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal(quotation.Id, item.QuotationId);
        Assert.Equal(quotation.QuotationNumber, item.QuotationNumber);
        Assert.Equal("Sent", item.Status);
        Assert.Equal(customer.Id, item.ClientId);
        Assert.Equal(customer.Cuc, item.ClientCuc);
        Assert.Equal("Verde Esencial S.A.S.", item.ClientName);
        Assert.Equal(tenant.OwnerEmail, item.AdvisorName);
        Assert.Equal(quotation.Total, item.Total);
    }

    [Fact]
    public async Task ListFiltersByStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        await CreateSentQuotationAsync(client, factory, tenant.TenantId, customer.Id, productId);

        var sent = await client.GetFromJsonAsync<ReportPageDto<QuotationsReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/quotations?status=Sent",
            TestContext.Current.CancellationToken);
        var drafts = await client.GetFromJsonAsync<ReportPageDto<QuotationsReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/quotations?status=Draft",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, sent?.Total);
        Assert.Equal(0, drafts?.Total);
    }

    [Fact]
    public async Task ListReturnsAnEmptyPageWhenTheTenantHasNoQuotations()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var page = await client.GetFromJsonAsync<ReportPageDto<QuotationsReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/quotations", TestContext.Current.CancellationToken);

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
            $"{ReportsUrl(Guid.CreateVersion7())}/quotations",
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
            $"{ReportsUrl(tenant.TenantId)}/quotations", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
