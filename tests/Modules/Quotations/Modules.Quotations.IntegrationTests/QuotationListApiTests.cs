using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>US-8: listado paginado con filtros combinables por cliente, asesora, estado y rango
/// de fechas.</summary>
public sealed class QuotationListApiTests
{
    [Fact]
    public async Task ListReturnsAnEmptyPageForANewTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Empty(response.Items);
        Assert.Equal(0, response.Total);
    }

    [Fact]
    public async Task ListReturnsQuotationsFromTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var first = await CreateQuotationAsync(client, tenantId, clientId);
        var second = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Equal(2, response.Total);
        Assert.Contains(response.Items, item => item.Id == first.Id);
        Assert.Contains(response.Items, item => item.Id == second.Id);
    }

    [Fact]
    public async Task ListFiltersByClientId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientA = await CreateActiveCustomerAsync(client, tenantId);
        var clientB = await CreateActiveCustomerAsync(client, tenantId);
        var quotationA = await CreateQuotationAsync(client, tenantId, clientA);
        await CreateQuotationAsync(client, tenantId, clientB);

        var response = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?clientId={clientA}", TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        var item = Assert.Single(response.Items);
        Assert.Equal(quotationA.Id, item.Id);
    }

    // Quotation no guarda el NIT del cliente -- el handler lo resuelve a ids contra Customers
    // antes de filtrar (ListQuotationsHandler + IQuotationCustomerLookup.SearchIdsByIdentificationAsync).
    [Fact]
    public async Task ListFiltersByPartialClientNit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientA = await CreateActiveCustomerAsync(client, tenantId, "900.111.222-3");
        var clientB = await CreateActiveCustomerAsync(client, tenantId, "800.999.888-7");
        var quotationA = await CreateQuotationAsync(client, tenantId, clientA);
        await CreateQuotationAsync(client, tenantId, clientB);

        var response = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?clientNit=111.222",
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        var item = Assert.Single(response.Items);
        Assert.Equal(quotationA.Id, item.Id);
    }

    [Fact]
    public async Task ListWithAClientNitThatMatchesNoCustomerReturnsAnEmptyPage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?clientNit=no-existe-este-nit",
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task ListFiltersByPartialQuotationNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var partial = quotation.QuotationNumber[4..];

        var response = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?quotationNumber={partial}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        var item = Assert.Single(response.Items);
        Assert.Equal(quotation.Id, item.Id);
    }

    [Fact]
    public async Task ListFiltersByStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, clientId);

        var draft = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?status=Draft", TestContext.Current.CancellationToken);
        var sent = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?status=Sent", TestContext.Current.CancellationToken);

        Assert.NotNull(draft);
        Assert.Single(draft.Items);
        Assert.NotNull(sent);
        Assert.Empty(sent.Items);
    }

    [Fact]
    public async Task ListWithAnInvalidStatusIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}?status=NotAStatus", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ListPaginatesResults()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, clientId);
        await CreateQuotationAsync(client, tenantId, clientId);
        await CreateQuotationAsync(client, tenantId, clientId);

        var firstPage = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?page=1&pageSize=2", TestContext.Current.CancellationToken);
        var secondPage = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?page=2&pageSize=2", TestContext.Current.CancellationToken);

        Assert.NotNull(firstPage);
        Assert.Equal(3, firstPage.Total);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.NotNull(secondPage);
        Assert.Equal(3, secondPage.Total);
        Assert.Single(secondPage.Items);
    }

    // El handler revalida el tenant activo del llamador contra el tenant de la ruta, asi que
    // esto es 403 y no simplemente una lista vacia -- un tenant vacio no distinguiria "sin
    // permiso" de "sin cotizaciones todavia".
    [Fact]
    public async Task ListForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;

        var (_, _, otherOwner) = await RegisterTenantAsync(
            factory, QuotationsPermissions.QuotationRead);
        using var __ = otherOwner;

        var response = await otherOwner.GetAsync(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
