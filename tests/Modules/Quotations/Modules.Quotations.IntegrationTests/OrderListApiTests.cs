using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// SALE-01: el listado operativo de pedidos. Ruta propia del modulo y no una vista del reporte:
/// Reporting es lectura analitica --agrega, exporta y no filtra por estado de pedido ni por
/// numero-- mientras que esta es la pantalla desde la que se trabaja.
///
/// Cliente, asesora, moneda y totales viven en la cotizacion de origen y el pedido no los repite,
/// asi que cada fila los trae ya resueltos: sin eso, quien pinta la tabla hace un GET por fila.
/// </summary>
public sealed class OrderListApiTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    [Fact]
    public async Task ListReturnsTheOrderWithItsClientAdvisorAndTotalsResolved()
    {
        await using var database = await StartDatabaseAsync();
        // Reloj fijo: el número del pedido lleva el año del tenant (spec 2026-09-17, punto 2b).
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToOrderAsync(client, tenantId, quotation.Id);

        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            OrdersUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
        var row = Assert.Single(page.Items);
        Assert.StartsWith("PED-2026-", row.OrderNumber, StringComparison.Ordinal);
        Assert.Equal(quotation.Id, row.QuotationId);
        Assert.Equal(quotation.QuotationNumber, row.QuotationNumber);
        Assert.Equal(clientId, row.ClientId);
        Assert.Equal("Verde Esencial S.A.S.", row.ClientName);
        // El owner nace sin nombre (CreateActive): la fila trae su correo como respaldo. El nombre
        // y el respaldo se recorren punta a punta en QuotationAdvisorNameApiTests (spec 2026-09-11,
        // D1, nota del 2026-09-15).
        Assert.NotNull(row.AdvisorName);
        Assert.Equal("Pending", row.Status);
        Assert.Equal("PaymentPending", row.PaymentStatus);
        Assert.Equal(quotation.Currency, row.Currency);
        Assert.Equal(quotation.Total, row.Total);
    }

    // El listado es del tenant, no de la cotizacion: dos pedidos de dos cotizaciones distintas
    // viajan en la misma pagina, la mas reciente primero.
    [Fact]
    public async Task ListReturnsEveryOrderOfTheTenantNewestFirst()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var first = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToOrderAsync(client, tenantId, first.Id);
        var second = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToOrderAsync(client, tenantId, second.Id);

        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            OrdersUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(2, page.Total);
        Assert.Equal(
            [second.Id, first.Id],
            page.Items.Select(row => row.QuotationId).ToArray());
    }

    [Fact]
    public async Task ListFiltersByOrderNumberAsPartialText()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var first = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstOrder = await ConvertToOrderAsync(client, tenantId, first.Id);
        var second = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToOrderAsync(client, tenantId, second.Id);

        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?orderNumber={firstOrder.OrderNumber}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(firstOrder.OrderNumber, Assert.Single(page.Items).OrderNumber);
    }

    // El CUC no vive en el pedido ni en la cotizacion: el handler lo resuelve a ids contra
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
        await ConvertToOrderAsync(client, tenantId, wanted.Id);
        var other = await CreateSentQuotationAsync(client, factory, tenantId, otherId, productId);
        await ConvertToOrderAsync(client, tenantId, other.Id);

        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?clientCuc={wantedCuc}", TestContext.Current.CancellationToken);

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
        var approvedOrder = await ConvertToOrderAsync(client, tenantId, approved.Id);
        (await client.PostAsync(
            $"{OrdersUrl(tenantId)}/{approvedOrder.Id}/approve",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var pending = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToOrderAsync(client, tenantId, pending.Id);

        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?status=Approved", TestContext.Current.CancellationToken);

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
            $"{OrdersUrl(tenantId)}?status=NotAStatus", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("order.order.status_invalid", problem.Code);
    }

    [Fact]
    public async Task ListRequiresThePermissionToReadOrders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [QuotationsPermissions.QuotationRead]);
        using var _ = client;

        var response = await client.GetAsync(
            OrdersUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // SALE-04: el detalle se direcciona por el pedido, no por su cotizacion. La respuesta trae las
    // dos --el pedido no guarda cliente, productos ni totales, los lee de ella-- para que la
    // pantalla se dibuje con una sola consulta en vez de encadenar dos.
    [Fact]
    public async Task GetReturnsTheOrderWithItsQuotationComposed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var order = await ConvertToOrderAsync(client, tenantId, quotation.Id);

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{order.Id}", TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal(order.Id, detail.Order.Id);
        Assert.Equal(order.OrderNumber, detail.Order.OrderNumber);
        Assert.Equal("Pending", detail.Order.Status);
        // La cotizacion llega compuesta, igual que en su propio detalle: cliente resuelto y
        // lineas con el nombre del producto, que es lo que la pantalla pinta.
        Assert.Equal(quotation.Id, detail.Quotation.Id);
        Assert.NotNull(detail.Quotation.Client);
        Assert.Equal("Verde Esencial S.A.S.", detail.Quotation.Client.Name);
        var item = Assert.Single(detail.Quotation.Items);
        Assert.False(string.IsNullOrWhiteSpace(item.ProductName));
        Assert.Equal(quotation.Total, detail.Quotation.Total);
    }

    [Fact]
    public async Task GetAnUnknownOrderIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            $"{OrdersUrl(tenantId)}/{Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Spec 2026-09-17, punto 3: el pedido convertido el 31 a las 23:00 de Bogotá —ya 2027 en UTC—
    // entra en el día 31 del tenant y no en el 1 de enero.
    [Fact]
    public async Task ListFiltersTheConversionRangeByTheTenantsLocalDay()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToOrderAsync(client, tenantId, quotation.Id);

        var lastDay = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?convertedFrom=2026-12-31&convertedTo=2026-12-31",
            TestContext.Current.CancellationToken);
        var nextDay = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?convertedFrom=2027-01-01&convertedTo=2027-01-01",
            TestContext.Current.CancellationToken);

        Assert.NotNull(lastDay);
        Assert.Equal(quotation.Id, Assert.Single(lastDay.Items).QuotationId);
        Assert.Equal(1, lastDay.Total);
        Assert.NotNull(nextDay);
        Assert.Empty(nextDay.Items);
        Assert.Equal(0, nextDay.Total);
    }

    /// <summary>Convierte sin comprobantes: el pago queda pendiente, que es el unico caso en el
    /// que la conversion no los exige. Estas pruebas miran el listado, no el asistente.</summary>
    private static async Task<OrderResponse> ConvertToOrderAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
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
