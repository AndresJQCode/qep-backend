using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// SALE-01: el listado operativo de ventas. Ruta propia del modulo y no una vista del reporte:
/// Reporting es lectura analitica --agrega, exporta y no filtra por estado de venta ni por
/// numero-- mientras que esta es la pantalla desde la que se trabaja.
///
/// Cliente, asesora, moneda y totales viven en la cotizacion de origen y la venta no los repite,
/// asi que cada fila los trae ya resueltos: sin eso, quien pinta la tabla hace un GET por fila.
/// </summary>
public sealed class SaleListApiTests
{
    private static string SalesUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/sales";

    [Fact]
    public async Task ListReturnsTheSaleWithItsClientAdvisorAndTotalsResolved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToSaleAsync(client, tenantId, quotation.Id);

        var page = await client.GetFromJsonAsync<SalesPageResponse>(
            SalesUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
        var row = Assert.Single(page.Items);
        Assert.StartsWith($"VEN-{DateTime.UtcNow.Year}-", row.SaleNumber, StringComparison.Ordinal);
        Assert.Equal(quotation.Id, row.QuotationId);
        Assert.Equal(quotation.QuotationNumber, row.QuotationNumber);
        Assert.Equal(clientId, row.ClientId);
        Assert.Equal("Verde Esencial S.A.S.", row.ClientName);
        // La asesora se muestra por correo: el sistema no guarda nombre de persona en ninguna
        // parte, mismo criterio que el listado de cotizaciones.
        Assert.NotNull(row.AdvisorEmail);
        Assert.Equal("Pending", row.Status);
        Assert.Equal("PaymentPending", row.PaymentStatus);
        Assert.Equal(quotation.Currency, row.Currency);
        Assert.Equal(quotation.Total, row.Total);
    }

    // El listado es del tenant, no de la cotizacion: dos ventas de dos cotizaciones distintas
    // viajan en la misma pagina, la mas reciente primero.
    [Fact]
    public async Task ListReturnsEverySaleOfTheTenantNewestFirst()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var first = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToSaleAsync(client, tenantId, first.Id);
        var second = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToSaleAsync(client, tenantId, second.Id);

        var page = await client.GetFromJsonAsync<SalesPageResponse>(
            SalesUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(2, page.Total);
        Assert.Equal(
            [second.Id, first.Id],
            page.Items.Select(row => row.QuotationId).ToArray());
    }

    [Fact]
    public async Task ListFiltersBySaleNumberAsPartialText()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var first = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstSale = await ConvertToSaleAsync(client, tenantId, first.Id);
        var second = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToSaleAsync(client, tenantId, second.Id);

        var page = await client.GetFromJsonAsync<SalesPageResponse>(
            $"{SalesUrl(tenantId)}?saleNumber={firstSale.SaleNumber}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(firstSale.SaleNumber, Assert.Single(page.Items).SaleNumber);
    }

    // El CUC no vive en la venta ni en la cotizacion: el handler lo resuelve a ids contra
    // Customers antes de filtrar, mismo camino que el filtro por NIT del listado de cotizaciones.
    [Fact]
    public async Task ListFiltersByClientCuc()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var wantedId = await CreateActiveCustomerAsync(client, tenantId);
        var otherId = await CreateActiveCustomerAsync(client, tenantId);
        var wantedCuc = await ReadCucAsync(client, tenantId, wantedId);
        var wanted = await CreateSentQuotationAsync(client, factory, tenantId, wantedId, productId);
        await ConvertToSaleAsync(client, tenantId, wanted.Id);
        var other = await CreateSentQuotationAsync(client, factory, tenantId, otherId, productId);
        await ConvertToSaleAsync(client, tenantId, other.Id);

        var page = await client.GetFromJsonAsync<SalesPageResponse>(
            $"{SalesUrl(tenantId)}?clientCuc={wantedCuc}", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(wantedId, Assert.Single(page.Items).ClientId);
    }

    [Fact]
    public async Task ListFiltersByStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var approved = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToSaleAsync(client, tenantId, approved.Id);
        (await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{approved.Id}/sale/approve",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var pending = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToSaleAsync(client, tenantId, pending.Id);

        var page = await client.GetFromJsonAsync<SalesPageResponse>(
            $"{SalesUrl(tenantId)}?status=Approved", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(approved.Id, Assert.Single(page.Items).QuotationId);
    }

    // Un estado que no existe es un 422 con codigo de dominio, no un filtro que en silencio no
    // devuelve nada -- mismo criterio que `quotation.quotation.status_invalid`.
    [Fact]
    public async Task ListRejectsAnUnknownStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            $"{SalesUrl(tenantId)}?status=NotAStatus", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("sale.sale.status_invalid", problem.Code);
    }

    [Fact]
    public async Task ListRequiresThePermissionToReadSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [QuotationsPermissions.QuotationRead]);
        using var _ = client;

        var response = await client.GetAsync(
            SalesUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Convierte sin comprobantes: el pago queda pendiente, que es el unico caso en el
    /// que la conversion no los exige. Estas pruebas miran el listado, no el asistente.</summary>
    private static async Task<SaleResponse> ConvertToSaleAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/sale",
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        return sale;
    }

    private static async Task<string> ReadCucAsync(
        HttpClient client, Guid tenantId, Guid customerId)
    {
        var customer = await client.GetFromJsonAsync<CustomerCucDto>(
            $"/api/v1/tenants/{tenantId}/customers/{customerId}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer.Cuc;
    }

    private sealed record CustomerCucDto(string Cuc);

    private sealed record ProblemPayload(string Code);
}
